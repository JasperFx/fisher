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
///         Together those mean the high-water mark simply <em>is</em> <c>max(seq_id)</c>, and
///         <see cref="DetectInSafeZone" /> has no separate answer to give. Do not reintroduce
///         gap-skipping here on the assumption that Fisher must need it too; it would be machinery
///         guarding against a state that cannot occur.
///     </para>
///     <para>
///         <strong>Say precisely what those two facts buy, because the obvious shorthand — "committed
///         sequences are contiguous" — is stronger than what is true and invites a wrong conclusion.</strong>
///         What holds is narrower and is exactly what the daemon needs:
///     </para>
///     <para>
///         <em>A sequence at or below the mark can never later become a committed row the daemon has
///         not read.</em>
///     </para>
///     <para>
///         Contiguity itself is <em>not</em> unconditional. Deleting events leaves permanent holes, and
///         deleting the newest events drops <c>max(seq_id)</c> below a mark already recorded — see
///         <see cref="TryCorrectProgressInDatabaseAsync" />, which exists for that state, and fisher#174,
///         which found it. Neither is a gap in the sense Marten's machinery guards against: a hole is a
///         sequence that is <em>gone</em>, not one that is <em>coming</em>. The daemon's loader pages a
///         range rather than counting rows, so it steps over a hole; and the mark cannot follow a fallen
///         ceiling down, because <c>HighWaterStatistics.HasChanged</c> is <c>CurrentMark &gt; LastMark</c>.
///     </para>
///     <para>
///         What remains load-bearing is the <c>AUTOINCREMENT</c> keyword itself, and the narrow property
///         above is exactly what it supplies. A bare <c>INTEGER PRIMARY KEY</c> aliases the rowid, which
///         SQLite <em>reuses</em> after a delete — a reused sequence would appear below the mark and be
///         invisible to every async projection. See <c>EventsTable</c>.
///     </para>
///     <para>
///         ⚠️ <strong>One reachable state would break it, and it is closed one repository away.</strong>
///         SQLite cannot alter most of a table, so any migration beyond <c>ALTER TABLE ADD COLUMN</c>
///         rebuilds it — create, copy, drop, rename — and a bare rebuild resets <c>sqlite_sequence</c>
///         to the highest <em>surviving</em> row. On a <c>fi_events</c> whose newest rows a tenant wipe
///         or a compaction had removed, that reissues numbers already handed out, which is precisely the
///         reuse the keyword is here to forbid. Weasel's <c>TableDelta</c> emits the carry-over that
///         prevents it; <c>high_water_contiguity_audit</c> is what pins the dependency, since nothing in
///         Fisher would otherwise notice it going away and the symptom is a projection permanently
///         missing events with nothing anywhere to say why.
///     </para>
///     <para>
///         <c>high_water_contiguity_audit</c> is the whole argument as tests — a crashed writer through
///         WAL recovery, WAL checkpointing, <c>VACUUM</c>, concurrent writers, two stores over one file,
///         the deletion paths, and the migration above.
///     </para>
/// </remarks>
internal sealed class FisherHighWaterDetector : IHighWaterDetector
{
    private readonly FisherDatabase _database;
    private readonly EventGraph _events;
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset? _lastLivenessTouch;

    internal FisherHighWaterDetector(FisherDatabase database, EventGraph events, TimeProvider? timeProvider = null)
    {
        _database = database;
        _events = events;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Uri DatabaseUri => _database.DatabaseUri;

    /// <summary>
    ///     Identical to <see cref="Detect" />: there is no unsafe zone to stay out of.
    /// </summary>
    public Task<HighWaterStatistics> DetectInSafeZone(CancellationToken token) => Detect(token);

    public async Task<HighWaterStatistics> Detect(CancellationToken token)
    {
        // One statement on one connection, not ProjectionProgressFor + FetchHighestEventSequenceNumber:
        // this runs every poll cycle forever, and each of those opens its own pooled connection with
        // its own per-connection PRAGMA batch.
        var (lastMark, highest) = await _database.FetchHighWaterInputsAsync(HighWaterShard, token)
            .ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();

        var statistics = new HighWaterStatistics
        {
            LastMark = lastMark,
            HighestSequence = highest,

            // Contiguous sequences, so everything committed is safe to read.
            CurrentMark = highest,
            SafeStartMark = highest,
            Timestamp = now
        };

        if (statistics.HasChanged)
        {
            await MarkAsync(highest, token).ConfigureAwait(false);
            statistics.LastUpdated = statistics.Timestamp;
            _lastLivenessTouch = now;

            return statistics;
        }

        if (IsLivenessTouchDue(now))
        {
            await TouchAsync(token).ConfigureAwait(false);
            statistics.LastUpdated = now;
            _lastLivenessTouch = now;
        }

        return statistics;
    }

    /// <summary>
    ///     Whether the mark's <c>last_updated</c> is due to be re-stamped purely to say the poll loop is
    ///     still cycling (fisher#60).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The high-water row's <c>last_updated</c> moves whenever the mark <em>advances</em>, which
    ///         is not the same question as whether the daemon is <em>alive</em> — a quiet store advances
    ///         nothing and would otherwise look identical to a dead one. So the loop re-stamps the row
    ///         on a cycle where nothing changed, and the age of that column becomes an honest liveness
    ///         signal that costs no extra column and does not depend on extended progression tracking.
    ///     </para>
    ///     <para>
    ///         <b>Throttled, where Marten's per-tenant equivalent writes on every cycle.</b> That
    ///         difference is SQLite's: a write takes the database file's one write lock, so an idle
    ///         daemon touching the row at <c>SlowPollingTime</c> — a second by default — would make an
    ///         otherwise read-only store a permanent 1 Hz writer, appending to the WAL and forcing
    ///         checkpoints on a database nothing else is touching. The throttle bounds that to one small
    ///         write per <see cref="Fisher.EventStoreOptions.HighWaterLivenessInterval" /> while keeping
    ///         the signal's resolution well inside any sane staleness threshold.
    ///     </para>
    ///     <para>
    ///         Setting the interval to zero or less turns the touch off, which leaves the health check on
    ///         the sequence-gap heuristic alone — a store that would rather have no daemon writes at all
    ///         than a periodic one.
    ///     </para>
    /// </remarks>
    private bool IsLivenessTouchDue(DateTimeOffset now)
    {
        var interval = _events.HighWaterLivenessInterval;

        if (interval <= TimeSpan.Zero)
        {
            return false;
        }

        // Nothing recorded yet means this process has not stamped the row; do it now, so an agent that
        // starts against an already-advanced store still proves it is alive.
        return _lastLivenessTouch is not { } last || now - last >= interval;
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
    ///     Move the high-water mark straight to the highest sequence in the database, without the
    ///     daemon having read a single event (fisher#173).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>For retrofitting async projections onto a store that has never had any.</b> A daemon
    ///         starting against a large existing event store begins at zero and replays everything to
    ///         reach the head; advancing the mark first means it starts in catch-up mode and processes
    ///         only what arrives afterwards.
    ///     </para>
    ///     <para>
    ///         <b>Nothing is projected by this, and that is the whole point — so it is also the
    ///         hazard.</b> Every registered shard still starts from its own progression row, so a shard
    ///         with no row will still replay from zero; what this skips is the *high-water* agent's
    ///         climb. Use it on a store whose projections are genuinely new and whose history is
    ///         genuinely not wanted, which is why Marten's own doc comment says "use with caution".
    ///     </para>
    ///     <para>
    ///         Reads <c>max(seq_id)</c> and writes it. On SQLite that is the honest ceiling with no
    ///         safe-zone reasoning behind it, for the reason this whole class documents: committed
    ///         sequences are contiguous.
    ///     </para>
    /// </remarks>
    internal async Task AdvanceHighWaterMarkToLatestAsync(CancellationToken token)
    {
        var highest = await _database.FetchHighestEventSequenceNumber(token).ConfigureAwait(false);

        await MarkAsync(highest, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Pull any progression row that has somehow advanced past the highest event sequence back down
    ///     to it (fisher#173).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is more reachable on Fisher than the Marten method it mirrors, and for a reason
    ///         that is Fisher's own.</b> Marten carries it for a PostgreSQL shutdown race it believes it
    ///         has since closed. Here the state arises from an ordinary, supported operation: stream
    ///         compacting and <c>DeleteEventsOperation</c> remove rows, and <c>seq_id</c> is
    ///         <c>AUTOINCREMENT</c>, so deleting from the top of the table lowers <c>max(seq_id)</c>
    ///         below progress that was already recorded. A shard left above the ceiling never advances
    ///         again, and <c>QueryForNonStaleData</c> waits on it forever.
    ///     </para>
    ///     <para>
    ///         <b>Every row, not only the high-water one, and clamped per row rather than reset
    ///         wholesale.</b> Marten's version resets every row to the highest sequence the moment the
    ///         high-water row is ahead; that drags shards genuinely behind the head *forward*, skipping
    ///         events they had not applied. Only a row that is impossible is corrected here, and only as
    ///         far as the ceiling.
    ///     </para>
    ///     <para>
    ///         A corrected shard will replay the range between the new ceiling and where it thought it
    ///         was — which is the honest outcome, since the events it recorded having processed are no
    ///         longer there to say otherwise.
    ///     </para>
    /// </remarks>
    internal async Task TryCorrectProgressInDatabaseAsync(CancellationToken token)
    {
        await using var connection = await _database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = $"""
                               update {_events.ProgressionTableName}
                                  set last_seq_id = (select coalesce(max(seq_id), 0) from {_events.EventsTableName}),
                                      last_updated = {SqliteTimestamp.NowExpression}
                                where last_seq_id > (select coalesce(max(seq_id), 0) from {_events.EventsTableName});
                               """;

        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

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

    /// <summary>
    ///     Re-stamp the mark's <c>last_updated</c> without moving the mark — see
    ///     <see cref="IsLivenessTouchDue" />.
    /// </summary>
    /// <remarks>
    ///     An <c>update</c> rather than the upsert <see cref="MarkAsync" /> uses: with no row there is no
    ///     poll cycle to attest to yet, and inserting one at sequence zero would tell a reader the daemon
    ///     had processed up to zero rather than that it had not run. A store where nothing has ever been
    ///     appended therefore reports no high-water row at all, which the health check already reads as
    ///     "the daemon has not started here" rather than as a fault.
    /// </remarks>
    private async Task TouchAsync(CancellationToken token)
    {
        await using var connection = await _database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
                               update {_events.ProgressionTableName}
                                  set last_updated = {SqliteTimestamp.NowExpression}
                                where name = @name;
                               """;
        command.Parameters.AddWithValue("@name", ShardState.HighWaterMark);

        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
