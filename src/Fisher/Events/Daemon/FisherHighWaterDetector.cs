using System.Data;
using Fisher.Storage;
using Microsoft.Data.Sqlite;
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
    ///     What advancing the mark to the committed ceiling would do, without writing anything
    ///     (jasperfx#1031).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The missing count is a count of holes, not of in-flight appends.</b> On SQLite nothing
    ///         is ever in flight below <c>max(seq_id)</c> — see this class's remarks — so a sequence
    ///         number between the mark and the ceiling with no row is one a delete removed: compaction,
    ///         masking's deletes, a tenant wipe. The contract still asks for it because Marten and
    ///         Polecat can genuinely lose a slow append there, and on Fisher it is an honest zero far
    ///         more often than not.
    ///     </para>
    /// </remarks>
    internal Task<HighWaterMarkPreview> PreviewAdvanceAsync(CancellationToken token)
    {
        return _database.ExecuteResilientlyAsync(async ct =>
        {
            await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
            var (current, ceiling, missing) = await ReadMarkAsync(connection, null, ct).ConfigureAwait(false);

            return new HighWaterMarkPreview(_database.Identifier, null, current, ceiling, missing);
        }, token);
    }

    /// <summary>
    ///     Move the high-water mark forward to the highest committed sequence, refusing when
    ///     <paramref name="expectedCurrentMark" /> no longer matches the stored mark (fisher#173,
    ///     jasperfx#1031).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>For retrofitting async projections onto a store that has never had any</b>, or for
    ///         jump-starting one an operator has decided to move on. Nothing is projected by this, and
    ///         every registered shard still starts from its own progression row, so a shard with no row
    ///         still replays from zero; what this skips is the <em>high-water</em> agent's climb. Marten's
    ///         own doc comment says "use with caution", and the reason is the same here.
    ///     </para>
    ///     <para>
    ///         <b>The read, the comparison and the write are one <c>BEGIN IMMEDIATE</c> transaction</b>,
    ///         which on SQLite is the whole of the compare-and-set: the transaction holds the file's one
    ///         write lock, so no running detector can move the mark between the read and the write. The
    ///         siblings need a row lock for the same guarantee.
    ///     </para>
    ///     <para>
    ///         <b>Never backwards.</b> A mark at or past <c>max(seq_id)</c> is left where it is and
    ///         reported <see cref="HighWaterAdvanceOutcome.AlreadyAtCeiling" />. Deleting the newest
    ///         events can leave the mark above the ceiling, and pulling it down is
    ///         <see cref="TryCorrectProgressInDatabaseAsync" />'s job rather than this one's — this used
    ///         to write <c>max(seq_id)</c> unconditionally, which did both jobs and said it did one.
    ///     </para>
    /// </remarks>
    internal Task<HighWaterMarkAdvance> AdvanceToCommittedCeilingAsync(long? expectedCurrentMark,
        CancellationToken token)
    {
        return _database.ExecuteResilientlyAsync(async ct =>
        {
            await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);

            var (current, ceiling, missing) = await ReadMarkAsync(connection, transaction, ct).ConfigureAwait(false);

            if (expectedCurrentMark.HasValue && expectedCurrentMark.Value != current)
            {
                return new HighWaterMarkAdvance(_database.Identifier, null,
                    HighWaterAdvanceOutcome.RefusedStaleExpectation, current, current, 0);
            }

            if (ceiling <= current)
            {
                return new HighWaterMarkAdvance(_database.Identifier, null,
                    HighWaterAdvanceOutcome.AlreadyAtCeiling, current, current, 0);
            }

            await MarkAsync(connection, transaction, ceiling, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);

            return new HighWaterMarkAdvance(_database.Identifier, null, HighWaterAdvanceOutcome.Advanced,
                current, ceiling, missing);
        }, token);
    }

    /// <summary>
    ///     The stored mark (zero when none has been stored), the committed ceiling, and how many sequence
    ///     numbers between them have no row.
    /// </summary>
    private async Task<(long Current, long Ceiling, long Missing)> ReadMarkAsync(SqliteConnection connection,
        SqliteTransaction? transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
                               select coalesce((select last_seq_id from {_events.ProgressionTableName} where name = @name), 0),
                                      (select coalesce(max(seq_id), 0) from {_events.EventsTableName})
                               """;
        command.Parameters.AddWithValue("@name", ShardState.HighWaterMark);

        long current, ceiling;
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            await reader.ReadAsync(token).ConfigureAwait(false);
            current = reader.GetInt64(0);
            ceiling = reader.GetInt64(1);
        }

        if (ceiling <= current)
        {
            return (current, ceiling, 0);
        }

        await using var counting = connection.CreateCommand();
        counting.Transaction = transaction;
        counting.CommandText =
            $"select count(*) from {_events.EventsTableName} where seq_id > @current and seq_id <= @ceiling";
        counting.Parameters.AddWithValue("@current", current);
        counting.Parameters.AddWithValue("@ceiling", ceiling);

        var present = Convert.ToInt64(await counting.ExecuteScalarAsync(token).ConfigureAwait(false));

        return (current, ceiling, ceiling - current - present);
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
        await MarkAsync(connection, null, sequence, token).ConfigureAwait(false);
    }

    private async Task MarkAsync(SqliteConnection connection, SqliteTransaction? transaction, long sequence,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
