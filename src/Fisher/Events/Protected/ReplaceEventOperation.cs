using System.Data.Common;
using Fisher.Storage;
using JasperFx.Events;
using Weasel.Core;
using Weasel.Storage;

namespace Fisher.Events.Protected;

/// <summary>
///     Replaces the event at one sequence with a different event body, of a possibly different type.
/// </summary>
/// <remarks>
///     <para>
///         Where <see cref="OverwriteEventOperation" /> rewrites what an event says, this rewrites what
///         it is: <c>data</c>, <c>type</c> and <c>dotnet_type</c> all change, and the row gets a fresh
///         <c>id</c> because it is no longer the event that was appended. The stream, the version and
///         the sequence are what stay — the row keeps its place in both orders, which is the whole
///         point of replacing in place rather than deleting and appending.
///     </para>
///     <para>
///         <b>The timestamp is left alone.</b> Polecat's equivalent moves it to now. Fisher does not,
///         because <c>fi_events.timestamp</c> is what <c>FetchStreamAsync</c>'s timestamp bound and the
///         daemon's timestamp floor read, and both assume it rises with the sequence — moving one row's
///         timestamp forward puts the column out of order with <c>seq_id</c> and makes a bounded read
///         return a set that is neither the old answer nor the new one.
///     </para>
///     <para>
///         Metadata that described the original event and cannot describe the replacement is cleared
///         rather than carried over: headers, correlation id, causation id. Each only when the store
///         keeps that column.
///     </para>
///     <para>
///         The rewrite-visibility caveat on <see cref="OverwriteEventOperation" /> applies here in full,
///         and harder — a projection that folded the original event has folded something that no longer
///         exists.
///     </para>
/// </remarks>
internal sealed class ReplaceEventOperation : Weasel.Storage.IStorageOperation
{
    private readonly EventGraph _graph;
    private readonly long _sequence;
    private readonly object _eventBody;
    private readonly FisherEventType _mapping;

    internal ReplaceEventOperation(EventGraph graph, long sequence, object eventBody)
    {
        _graph = graph;
        _sequence = sequence;
        _eventBody = eventBody;
        _mapping = graph.EventMappingFor(eventBody.GetType());

        // Assigned here rather than in ConfigureCommand so the caller can be told the new id
        // synchronously, before the operation runs — CompletelyReplaceEvent returns it.
        Id = Guid.NewGuid();
    }

    /// <summary>The replacement event's new identity.</summary>
    internal Guid Id { get; }

    public Type DocumentType => typeof(IEvent);

    public OperationRole Role() => OperationRole.Events;

    public void ConfigureCommand(ICommandBuilder builder, IStorageSession session)
    {
        var options = _graph.EventOptions;

        // Tag rows describe the event that was appended, and this replaces it — new id, new type, new
        // body. Carrying them over would leave a tag asserting something about an event that no longer
        // exists, and a tag query would return the replacement as though it were the tagged event. That
        // matters most under compaction, where the replacement is a Compacted<T> snapshot: keeping the
        // last event's tag while every other compacted event's tag is deleted is the one outcome that
        // is neither "the stream is still tagged" nor "the tagged events are gone".
        //
        // No foreign key problem either way — the row survives, so this is semantics, not integrity.
        foreach (var registration in _graph.TagTypes)
        {
            builder.Append("delete from ");
            builder.Append(_graph.TagTableName(registration));
            builder.Append(" where seq_id = ");
            Bind(builder, _sequence, StorageColumnType.Long);
            builder.Append(";");
        }

        builder.Append("update ");
        builder.Append(_graph.EventsTableName);
        builder.Append(" set data = ");
        Bind(builder, session.Serializer.ToJson(_eventBody), StorageColumnType.Json);

        builder.Append(", type = ");
        Bind(builder, _mapping.EventTypeName, StorageColumnType.String);

        builder.Append(", dotnet_type = ");
        Bind(builder, _mapping.DotNetTypeName, StorageColumnType.String);

        builder.Append(", id = ");
        Bind(builder, Id, StorageColumnType.Guid);

        if (options.EnableHeaders)
        {
            builder.Append(", headers = null");
        }

        if (options.EnableCorrelationId)
        {
            builder.Append(", correlation_id = null");
        }

        if (options.EnableCausationId)
        {
            builder.Append(", causation_id = null");
        }

        builder.Append(" where seq_id = ");
        Bind(builder, _sequence, StorageColumnType.Long);
        builder.Append(";");
    }

    public Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
        => Task.CompletedTask;

    private static void Bind(ICommandBuilder builder, object value, StorageColumnType type)
        => EventRewriteBinding.Bind(builder, value, type);
}
