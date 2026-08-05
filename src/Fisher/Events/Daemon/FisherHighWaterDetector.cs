using Fisher.Storage;
using JasperFx.Events.Daemon.HighWater;
using JasperFx.Events.Projections;

namespace Fisher.Events.Daemon;

/// <summary>
///     Determines how far the async daemon may safely read.
/// </summary>
/// <remarks>
///     <para>
///         <strong>This is far simpler than its Marten and Polecat counterparts, and the reason is a
///         real property of SQLite rather than a shortcut.</strong> Those stores must distinguish the
///         highest sequence <em>issued</em> from the highest safe to <em>read</em>, because a
///         PostgreSQL sequence or SQL Server IDENTITY hands out numbers outside the transaction: a
///         writer can hold sequence 7 uncommitted while 8 commits ahead of it, so reading up to 8 would
///         skip 7 forever. That is what their safe-zone polling, stale-gap skipping and
///         <c>SafeStartMark</c> machinery exists to handle.
///     </para>
///     <para>
///         Neither hazard exists here, both verified against SQLite 3.51:
///     </para>
///     <list type="number">
///         <item>
///             <description>
///                 <strong>Writers are serialized.</strong> One writer per database file, and Fisher's
///                 appends take <c>BEGIN IMMEDIATE</c>, so a transaction's sequences are fully
///                 committed before the next writer can allocate any. There is no interleaving to
///                 create a hole.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <strong>A rollback does not consume the sequence.</strong> SQLite keeps the
///                 <c>AUTOINCREMENT</c> counter in <c>sqlite_sequence</c>, which is an ordinary table
///                 and rolls back with the transaction — after a rolled-back insert of two rows, the
///                 next insert reuses the number the failed one would have had. So an aborted write
///                 leaves no permanent gap either.
///             </description>
///         </item>
///     </list>
///     <para>
///         Together those mean committed <c>seq_id</c>s are contiguous, so the high-water mark simply
///         <em>is</em> <c>max(seq_id)</c> and <see cref="DetectInSafeZone" /> has no separate answer to
///         give. Do not reintroduce gap-skipping here on the assumption that Fisher must need it too;
///         it would be machinery guarding against a state that cannot occur.
///     </para>
///     <para>
///         What remains load-bearing is the <c>AUTOINCREMENT</c> keyword itself. A bare
///         <c>INTEGER PRIMARY KEY</c> aliases the rowid, which SQLite <em>reuses</em> after a delete —
///         a reused sequence would appear below the mark and be invisible to every async projection.
///         See <c>EventsTable</c>.
///     </para>
/// </remarks>
internal sealed class FisherHighWaterDetector : IHighWaterDetector
{
    private readonly FisherDatabase _database;
    private readonly EventGraph _events;

    internal FisherHighWaterDetector(FisherDatabase database, EventGraph events)
    {
        _database = database;
        _events = events;
    }

    public Uri DatabaseUri => _database.DatabaseUri;

    /// <summary>
    ///     Identical to <see cref="Detect" />: there is no unsafe zone to stay out of.
    /// </summary>
    public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);

    public async Task<HighWaterStatistics> Detect(CancellationToken token)
    {
        var lastMark = await _database.ProjectionProgressFor(HighWaterShard, token).ConfigureAwait(false);
        var highest = await _database.FetchHighestEventSequenceNumber(token).ConfigureAwait(false);

        var statistics = new HighWaterStatistics
        {
            LastMark = lastMark,
            HighestSequence = highest,

            // Contiguous sequences, so everything committed is safe to read.
            CurrentMark = highest,
            SafeStartMark = highest,
            Timestamp = DateTimeOffset.UtcNow
        };

        if (statistics.HasChanged)
        {
            await MarkAsync(highest, token).ConfigureAwait(false);
            statistics.LastUpdated = statistics.Timestamp;
        }

        return statistics;
    }

    /// <summary>
    ///     The committed ceiling a catch-up run aims at.
    /// </summary>
    /// <remarks>
    ///     Overridden rather than left to the interface default, which runs a full <see cref="Detect" />
    ///     and would therefore persist a mark as a side effect of merely asking how far there is to go.
    /// </remarks>
    public Task<long> FetchCommittedHighWaterCeilingAsync(CancellationToken token)
        => _database.FetchHighestEventSequenceNumber(token);

    /// <summary>
    ///     The progression row the mark lives in, sharing <c>fi_event_progression</c> with the shards.
    /// </summary>
    private static ShardName HighWaterShard => new(ShardState.HighWaterMark);

    private async Task MarkAsync(long sequence, CancellationToken token)
    {
        await using var connection = await _database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
                               insert into {_events.ProgressionTableName} (name, last_seq_id, last_updated)
                               values (@name, @seq, {SqliteTimestamp.NowExpression})
                               on conflict (name) do update
                                 set last_seq_id = excluded.last_seq_id,
                                     last_updated = excluded.last_updated;
                               """;
        command.Parameters.AddWithValue("@name", ShardState.HighWaterMark);
        command.Parameters.AddWithValue("@seq", sequence);

        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
