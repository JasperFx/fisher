using Fisher.Storage;
using JasperFx.Events;
using JasperFx.Events.Tags;
using Microsoft.Data.Sqlite;

namespace Fisher.Events.Storage;

/// <summary>
///     Writes the DCB tag rows for a unit of work's appended events.
/// </summary>
/// <remarks>
///     <para>
///         This runs <em>after</em> the append operations and <em>inside</em> their transaction, and
///         both halves of that are load-bearing. A tag row is keyed by the event's <c>seq_id</c>, which
///         SQLite assigns on insert and Fisher only learns from the trailing sequence read-back in
///         <see cref="FisherQuickAppendEventsOperation" /> — so there is nothing to write until the
///         appends have postprocessed. Committing separately afterwards would leave a window where an
///         event is visible but untagged, which to a tag query is indistinguishable from an event that
///         was never tagged.
///     </para>
///     <para>
///         Writes are <c>on conflict do nothing</c> rather than read-then-insert. The tag table's
///         composite primary key already rejects a duplicate (value, seq_id), so the conflict clause
///         makes re-tagging idempotent without a round trip — which is what
///         <c>AssignTagWhere</c> will lean on too.
///     </para>
/// </remarks>
internal sealed class EventTagWriter
{
    private readonly EventGraph _graph;

    internal EventTagWriter(EventGraph graph)
    {
        _graph = graph;
    }

    /// <summary>
    ///     Write every tag carried by every event in <paramref name="streams" />.
    /// </summary>
    internal async Task WriteAsync(IReadOnlyList<StreamAction> streams, SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (_graph.TagTypes.Count == 0)
        {
            return;
        }

        // One prepared statement per run of rows landing in the same tag table, rather than one per
        // row — the same coalescing FisherSession.ExecuteBatchAsync applies to the operations, and it
        // matters here for the same reason: this runs inside the append's transaction, so every
        // prepare is time the file's one write lock is held. Events carrying several tag types
        // alternate tables and fall back to a command apiece.
        await using var reused = new Fisher.Internal.ReusedCommand(connection, transaction);

        foreach (var stream in streams)
        {
            foreach (var @event in stream.Events)
            {
                if (@event.Tags is not { Count: > 0 })
                {
                    continue;
                }

                foreach (var tag in @event.Tags)
                {
                    await WriteOneAsync(reused, @event, tag, connection, token).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task WriteOneAsync(Fisher.Internal.ReusedCommand reused, IEvent @event, EventTag tag,
        SqliteConnection connection, CancellationToken token)
    {
        var registration = _graph.FindTagType(tag.TagType)
                           ?? throw new InvalidOperationException(
                               $"Tag type '{tag.TagType.Name}' is not registered on this event store. Call "
                               + $"RegisterTagType<{tag.TagType.Name}>() before appending an event tagged with it.");

        var built = connection.CreateCommand();
        built.CommandText =
            $"insert into {_graph.TagTableName(registration)} (value, seq_id) values (@value, @seq) "
            + "on conflict do nothing;";

        built.Parameters.AddWithValue("@value", ToDatabaseValue(registration.ExtractValue(tag.Value)));
        built.Parameters.AddWithValue("@seq", @event.Sequence);

        var command = reused.Take(built);

        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Render a tag's unwrapped primitive for its TEXT or INTEGER column.
    /// </summary>
    /// <remarks>
    ///     The <see cref="Guid" /> case is the one that matters. Binding the raw value writes a
    ///     16-byte BLOB, and Microsoft.Data.Sqlite's own string form is uppercase — either way the row
    ///     is written but never matches a query, because SQLite's default collation is case-sensitive
    ///     and the query side renders the lowercase canonical form. Same trap as
    ///     <c>SqliteGuidIdentification</c>, and it fails by finding nothing rather than by erroring.
    /// </remarks>
    internal static object ToDatabaseValue(object value)
        => value is Guid guid ? guid.ToString() : value;
}
