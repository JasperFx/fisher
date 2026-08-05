using System.Linq.Expressions;
using Fisher.Events;
using Fisher.Storage;
using JasperFx.Events;

namespace Fisher.Linq.Members;

/// <summary>
///     Resolves <see cref="IEvent" /> members to <c>fi_events</c> columns.
/// </summary>
/// <remarks>
///     <para>
///         The event-store counterpart of <see cref="MemberFactory" />, and the reason
///         <see cref="IMemberResolver" /> is an interface rather than a concrete type: a document member
///         resolves into the JSON body through <c>json_extract</c>, while an event member is a real
///         column. Everything above this — <see cref="Parsing.WhereClauseParser" />, the fragment set,
///         the method-call parsers — is shared unchanged.
///     </para>
///     <para>
///         This is what makes <c>AssignTagWhere</c> a client of the LINQ layer rather than a
///         special-purpose translator, which is how Marten builds the same feature
///         (<c>EventQueryMapping.QueryMembers</c> fed to its <c>WhereClauseParser</c>).
///     </para>
/// </remarks>
internal class EventMemberFactory : IMemberResolver
{
    private readonly EventGraph _graph;

    public EventMemberFactory(EventGraph graph)
    {
        _graph = graph;
    }

    public IQueryableMember ResolveMember(MemberExpression expression)
    {
        var name = expression.Member.Name;

        return name switch
        {
            nameof(IEvent.Sequence) => Column("seq_id", typeof(long)),
            nameof(IEvent.Id) => Guid("id"),
            nameof(IEvent.Version) => Column("version", typeof(long)),
            nameof(IEvent.EventTypeName) => Column("type", typeof(string)),
            nameof(IEvent.DotNetTypeName) => Column("dotnet_type", typeof(string)),
            nameof(IEvent.TenantId) => Column("tenant_id", typeof(string)),
            nameof(IEvent.IsArchived) => new EventQueryableMember("is_archived", typeof(bool), isBoolean: true),
            nameof(IEvent.Timestamp) => Timestamp(),

            // Stream identity is one column under two names, picked by the configured identity style.
            nameof(IEvent.StreamId) => Guid("stream_id"),
            nameof(IEvent.StreamKey) => Column("stream_id", typeof(string)),

            _ => throw new BadLinqExpressionException(
                $"'IEvent.{name}' cannot be translated to a {_graph.EventsTableName} column. Supported "
                + "members are Sequence, Id, Version, EventTypeName, DotNetTypeName, TenantId, "
                + "IsArchived, Timestamp, StreamId and StreamKey.")
        };
    }

    private static EventQueryableMember Column(string column, Type memberType) => new(column, memberType);

    private static EventQueryableMember Guid(string column)
        => new(column, typeof(Guid), isGuid: true);

    /// <summary>
    ///     <c>fi_events.timestamp</c>, which — unlike a date inside a document body — <em>does</em> order
    ///     correctly as text.
    /// </summary>
    /// <remarks>
    ///     Worth stating because it is the exact opposite of <see cref="DateMember" />'s restriction.
    ///     A document's date is whatever System.Text.Json wrote, with the offset preserved and trailing
    ///     zeros trimmed, so it does not sort. This column is written by
    ///     <see cref="SqliteTimestamp" /> in a fixed-width UTC format chosen precisely so a string
    ///     comparison is an instant comparison, so range comparison is allowed here.
    /// </remarks>
    private static EventQueryableMember Timestamp()
        => new("timestamp", typeof(DateTimeOffset), isTimestamp: true);
}

/// <summary>
///     A single <c>fi_events</c> column as a queryable member.
/// </summary>
internal sealed class EventQueryableMember : IQueryableMember
{
    private readonly bool _isGuid;
    private readonly bool _isTimestamp;

    internal EventQueryableMember(string column, Type memberType, bool isBoolean = false, bool isGuid = false,
        bool isTimestamp = false)
    {
        RawLocator = column;
        TypedLocator = column;
        MemberType = memberType;
        IsBoolean = isBoolean;
        _isGuid = isGuid;
        _isTimestamp = isTimestamp;
    }

    public Type MemberType { get; }
    public string TypedLocator { get; }
    public string RawLocator { get; }
    public bool IsBoolean { get; }

    public object? ConvertValue(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (IsBoolean)
        {
            return (bool)value ? 1 : 0;
        }

        // Guid columns hold the lowercase canonical form; binding the Guid itself writes a BLOB that
        // matches nothing. The same conversion the write path applies.
        if (_isGuid || value is Guid)
        {
            return SqliteStorageDialect<Guid>.ToDatabaseValue(value);
        }

        if (_isTimestamp && value is DateTimeOffset timestamp)
        {
            return SqliteTimestamp.ToDatabaseValue(timestamp);
        }

        return value;
    }
}
