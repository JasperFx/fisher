using Fisher.Storage;
using JasperFx.Events;
using Weasel.Core;
using Weasel.Storage;

namespace Fisher.Events.Storage;

/// <summary>
///     One of <c>fi_events</c>' four optional metadata columns, as the shared
///     <see cref="PerEventQuickAppendOperationBase" /> writes them.
/// </summary>
/// <remarks>
///     Fisher's own rather than Weasel's column binders because the value rules are Fisher's and have to stay
///     byte-for-byte what was written before: a null is an explicit <c>DBNull</c>, and an empty header
///     dictionary is <c>NULL</c> rather than <c>{}</c>, which the row reader reads back as no headers.
/// </remarks>
internal sealed class FisherEventMetadataBinder : IEventMetadataBinder
{
    private readonly IStorageDialect _dialect;
    private readonly Func<IEvent, IStorageSession, object?> _value;
    private readonly StorageColumnType _type;

    private FisherEventMetadataBinder(IStorageDialect dialect, string columnName,
        Func<IEvent, IStorageSession, object?> value, StorageColumnType type)
    {
        _dialect = dialect;
        ColumnName = columnName;
        _value = value;
        _type = type;
    }

    public string ColumnName { get; }

    public string ValueSql => "?";

    public void Bind(IGroupedParameterBuilder pb, StreamAction stream, IEvent @event, IStorageSession session)
    {
        var parameter = pb.AppendParameter(SqliteStorageDialect<Guid>.ToDatabaseValue(_value(@event, session) ?? DBNull.Value));
        _dialect.SetParameterType(parameter, _type);
    }

    /// <summary>
    ///     The binders for the columns this store has turned on, in the order the columns were always
    ///     written: correlation, causation, headers, user name.
    /// </summary>
    internal static IEventMetadataBinder[] For(EventStoreOptions options, IStorageDialect dialect)
    {
        var binders = new List<IEventMetadataBinder>(4);

        if (options.EnableCorrelationId)
        {
            binders.Add(new FisherEventMetadataBinder(dialect, "correlation_id", (e, _) => e.CorrelationId,
                StorageColumnType.String));
        }

        if (options.EnableCausationId)
        {
            binders.Add(new FisherEventMetadataBinder(dialect, "causation_id", (e, _) => e.CausationId,
                StorageColumnType.String));
        }

        if (options.EnableHeaders)
        {
            binders.Add(new FisherEventMetadataBinder(dialect, "headers",
                (e, session) => e.Headers is { Count: > 0 } ? session.Serializer.ToJson(e.Headers) : null,
                StorageColumnType.Json));
        }

        if (options.EnableUserName)
        {
            binders.Add(new FisherEventMetadataBinder(dialect, "user_name", (e, _) => e.UserName,
                StorageColumnType.String));
        }

        return binders.ToArray();
    }
}
