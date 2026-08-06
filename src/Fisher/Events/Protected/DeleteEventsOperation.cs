using System.Data.Common;
using JasperFx.Events;
using Weasel.Core;
using Weasel.Storage;

namespace Fisher.Events.Protected;

/// <summary>
///     Permanently removes a set of events by sequence — the destructive half of stream compacting.
/// </summary>
/// <remarks>
///     <para>
///         <b>Tag rows go first, and that is not tidiness.</b> Every <c>fi_event_tag_*</c> table has a
///         real foreign key to <c>fi_events(seq_id)</c> and Weasel's default profile turns enforcement
///         on, so deleting the events first fails with <c>FOREIGN KEY constraint failed</c>. This is
///         the same ordering <c>DeleteAllEventDataAsync</c> had to learn in fisher#6, arrived at the
///         same way.
///     </para>
///     <para>
///         <b>Dead letters are deliberately not touched.</b> They have no foreign key precisely so they
///         outlive the events they describe — a dead letter is the record that something went wrong and
///         has to survive the event being compacted away, or the evidence disappears exactly when
///         somebody comes looking for it.
///     </para>
///     <para>
///         Safe only because <c>fi_events.seq_id</c> is declared <c>AUTOINCREMENT</c>. A bare
///         <c>INTEGER PRIMARY KEY</c> aliases the rowid, which SQLite reuses after a delete — and a
///         reused sequence below the daemon's high-water mark is an event no async projection would
///         ever see. This is the operation that would have discovered that the hard way.
///     </para>
/// </remarks>
internal sealed class DeleteEventsOperation : Weasel.Storage.IStorageOperation
{
    private readonly EventGraph _graph;
    private readonly long[] _sequences;

    internal DeleteEventsOperation(EventGraph graph, long[] sequences)
    {
        _graph = graph;
        _sequences = sequences;
    }

    public Type DocumentType => typeof(IEvent);

    public OperationRole Role() => OperationRole.Events;

    public void ConfigureCommand(ICommandBuilder builder, IStorageSession session)
    {
        if (_sequences.Length == 0)
        {
            return;
        }

        foreach (var registration in _graph.TagTypes)
        {
            AppendDelete(builder, _graph.TagTableName(registration));
        }

        AppendDelete(builder, _graph.EventsTableName);
    }

    public Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
        => Task.CompletedTask;

    private void AppendDelete(ICommandBuilder builder, string table)
    {
        builder.Append("delete from ");
        builder.Append(table);
        builder.Append(" where seq_id in (");

        for (var i = 0; i < _sequences.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            EventRewriteBinding.Bind(builder, _sequences[i], StorageColumnType.Long);
        }

        builder.Append(");");
    }
}
