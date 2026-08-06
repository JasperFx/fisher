using System.Data.Common;
using JasperFx.Events;
using Weasel.Core;
using Weasel.Storage;

namespace Fisher.Events.Protected;

/// <summary>
///     Rewrites one already-committed event's body, and its headers where the store keeps them, in
///     place.
/// </summary>
/// <remarks>
///     <para>
///         The event is identified by its <c>seq_id</c>, not by its <c>id</c>: the sequence is the
///         primary key and the only column with an index that makes the update a seek.
///     </para>
///     <para>
///         Everything else about the row is left alone — the version, the stream, the timestamp, the
///         type. This rewrites what an event <em>says</em>, not what it <em>is</em>; replacing the
///         latter is <see cref="ReplaceEventOperation" />.
///     </para>
///     <para>
///         <b>An async projection that has already passed this event does not see the rewrite.</b>
///         The daemon's high-water mark is a sequence, and rewriting a row below it changes nothing
///         the shard will read again. A projection whose state depends on the old body stays wrong
///         until it is rebuilt. That is the same behaviour as Marten's, and it is why masking is a
///         data-at-rest operation rather than a correction.
///     </para>
/// </remarks>
internal sealed class OverwriteEventOperation : Weasel.Storage.IStorageOperation
{
    private readonly EventGraph _graph;
    private readonly IEvent _event;

    internal OverwriteEventOperation(EventGraph graph, IEvent @event)
    {
        _graph = graph;
        _event = @event;
    }

    public Type DocumentType => typeof(IEvent);

    public OperationRole Role() => OperationRole.Events;

    public void ConfigureCommand(ICommandBuilder builder, IStorageSession session)
    {
        builder.Append("update ");
        builder.Append(_graph.EventsTableName);
        builder.Append(" set data = ");
        Bind(builder, session.Serializer.ToJson(_event.Data), StorageColumnType.Json);

        // Only when the store keeps a headers column at all: writing to one that does not exist is a
        // "no such column" error rather than a no-op, and a masking rule that adds a header to a store
        // with headers disabled has asked for something the schema cannot hold.
        if (_graph.EventOptions.EnableHeaders)
        {
            builder.Append(", headers = ");

            object headers = _event.Headers is { Count: > 0 }
                ? session.Serializer.ToJson(_event.Headers)
                : DBNull.Value;

            Bind(builder, headers, StorageColumnType.Json);
        }

        builder.Append(" where seq_id = ");
        Bind(builder, _event.Sequence, StorageColumnType.Long);
        builder.Append(";");
    }

    public Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
        => Task.CompletedTask;

    private static void Bind(ICommandBuilder builder, object value, StorageColumnType type)
        => EventRewriteBinding.Bind(builder, value, type);
}
