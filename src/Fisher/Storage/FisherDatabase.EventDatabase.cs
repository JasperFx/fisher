using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Fisher.Storage;

/// <summary>
///     The <see cref="IEventDatabase" /> half of <see cref="FisherDatabase" /> — everything the async
///     daemon needs to read and record progress.
/// </summary>
/// <remarks>
///     <para>
///         The daemon machinery itself is JasperFx's: the coordinator, the subscription agents, the
///         shard tracker, the throttling and retry loaders are all shared. What a store supplies is the
///         storage seam, which is this plus the high-water detector, the event loader and the
///         projection batch.
///     </para>
///     <para>
///         Every read here opens its own connection through the store's resilience pipeline. The
///         database has no session to borrow one from, and the daemon runs on its own threads — sharing
///         a session's connection would put daemon reads and application writes on the same SQLite
///         handle, which is exactly what WAL exists to avoid.
///     </para>
/// </remarks>
public partial class FisherDatabase : IEventDatabase
{
    private ShardStateTracker? _tracker;

    /// <summary>
    ///     The daemon's in-memory view of where each shard has reached.
    /// </summary>
    /// <remarks>
    ///     Created lazily with a null logger. The daemon replaces it with a logging one when it starts,
    ///     which is why the setter exists — the tracker outlives any single daemon run.
    /// </remarks>
    public ShardStateTracker Tracker
    {
        get => _tracker ??= new ShardStateTracker(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        internal set => _tracker = value;
    }

    public Uri DatabaseUri => Describe().DatabaseUri();

    public string StorageIdentifier => Identifier;

    /// <summary>
    ///     Run a diagnostic read, answering <paramref name="whenStorageIsMissing" /> rather than throwing
    ///     when the table or column it reads does not exist (fisher#332).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         These are the reads a monitoring console makes, usually on a timer, and the likeliest reason
    ///         one fails is that the schema has not been applied yet — which is precisely when a console
    ///         is most likely to be pointed at the store, in the window before
    ///         <c>ApplyAllDatabaseChangesOnStartup()</c> has run. "No shards, no dead letters, sequence
    ///         zero" is the true answer about a store with no event tables; a raw
    ///         <c>no such table</c> answers nothing and fails the whole page over one number. Marten
    ///         made the same call in marten#5512.
    ///     </para>
    ///     <para>
    ///         <b>Keyed on the exception rather than on configuration</b>, because configuration cannot
    ///         say whether the schema has been applied yet. And narrowed to the two missing-storage
    ///         errors, where the call-site wrappers this replaced caught bare <see cref="Exception" /> and
    ///         so swallowed a genuine connection or corruption failure just as readily.
    ///     </para>
    ///     <para>
    ///         <b>Outside the resilience pipeline</b>, so a missing table is answered at once rather than
    ///         being offered to a retry policy first. <b>Reads only</b>: a progression or dead-letter
    ///         <em>write</em> that silently did nothing would be far worse than one that failed.
    ///     </para>
    /// </remarks>
    private async Task<T> ReadWhenStorageExistsAsync<T>(Func<CancellationToken, ValueTask<T>> read,
        T whenStorageIsMissing, CancellationToken token)
    {
        try
        {
            return await _options.ResiliencePipeline.ExecuteAsync(read, token).ConfigureAwait(false);
        }
        catch (Exception e) when (SqliteSchemaErrors.IsMissingStorage(e))
        {
            return whenStorageIsMissing;
        }
    }

    /// <summary>
    ///     How far one shard has processed.
    /// </summary>
    /// <remarks>
    ///     Zero for a shard with no row yet, which is what the daemon reads as "start from the
    ///     beginning" — a missing row and a row at zero mean the same thing and neither is an error.
    /// </remarks>
    public async Task<long> ProjectionProgressFor(ShardName name, CancellationToken token = default)
    {
        return await ReadWhenStorageExistsAsync(async ct =>
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"select last_seq_id from {_events.ProgressionTableName} where name = @name";
            command.Parameters.AddWithValue("@name", name.Identity);

            var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result is null or DBNull ? 0L : Convert.ToInt64(result);
        }, 0L, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Every shard's progress, including the high-water row.
    /// </summary>
    public async Task<IReadOnlyList<ShardState>> AllProjectionProgress(CancellationToken token = default)
    {
        return await ReadWhenStorageExistsAsync(async ct =>
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"select name, last_seq_id, last_updated from {_events.ProgressionTableName}";

            var states = new List<ShardState>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                // fisher#363 / jasperfx#924: the row's own last_updated, which is liveness rather than
                // progress — on the high-water row it moves on an idle cycle too (fisher#60), so a
                // monitor can tell "caught up, nothing new" from "no longer maintained". Parsed through
                // SqliteTimestamp, whose AssumeUniversal is what keeps the zone-less text UTC rather than
                // local; the column is NOT NULL with a default, so the null arm is only a guard.
                states.Add(new ShardState(reader.GetString(0), reader.GetInt64(1))
                {
                    LastUpdated = reader.IsDBNull(2) ? null : SqliteTimestamp.FromDatabaseValue(reader.GetString(2))
                });
            }

            return (IReadOnlyList<ShardState>)states;
        }, (IReadOnlyList<ShardState>)Array.Empty<ShardState>(), token).ConfigureAwait(false);
    }

    /// <summary>
    ///     The high-water poll's two inputs — the mark's recorded position and the highest sequence
    ///     physically present — in one statement on one connection.
    /// </summary>
    /// <remarks>
    ///     The high-water agent asks both questions on every poll cycle, forever. Reading them through
    ///     <see cref="ProjectionProgressFor" /> and <see cref="FetchHighestEventSequenceNumber" />
    ///     costs two pooled connections per tick, each paying the per-connection PRAGMA batch
    ///     <c>SqliteDataSource</c> applies — pure overhead for a loop that idles at
    ///     <c>SlowPollingTime</c>. Those two methods stay for their other callers; this read exists for
    ///     the poll loop. The subselects preserve their semantics exactly: a missing progression row
    ///     reads as zero, and an empty events table reads as zero.
    /// </remarks>
    internal async Task<(long LastMark, long HighestSequence)> FetchHighWaterInputsAsync(ShardName name,
        CancellationToken token)
    {
        return await _options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                                   select coalesce((select last_seq_id from {_events.ProgressionTableName} where name = @name), 0),
                                          (select coalesce(max(seq_id), 0) from {_events.EventsTableName})
                                   """;
            command.Parameters.AddWithValue("@name", name.Identity);

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);

            return (reader.GetInt64(0), reader.GetInt64(1));
        }, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     The high-water row, with the time its poll loop last stamped it — or null when the daemon has
    ///     never run against this database.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         fisher#60. Separate from <see cref="AllProjectionProgress" />, which returns every shard's
    ///         row to keep one. That read carries <c>last_updated</c> too since jasperfx#924 gave
    ///         <see cref="ShardState" /> a field for it (fisher#363); this one predates the field and
    ///         stays for its caller, the health check.
    ///     </para>
    ///     <para>
    ///         The age of <c>last_updated</c> is a liveness signal precisely because
    ///         <c>FisherHighWaterDetector</c> re-stamps it on an idle cycle — see
    ///         <see cref="EventStoreOptions.HighWaterLivenessInterval" />. With that turned off it moves
    ///         only when the mark advances, which says nothing about whether the loop is running.
    ///     </para>
    /// </remarks>
    public async Task<HighWaterStatus?> FetchHighWaterStatusAsync(CancellationToken token = default)
    {
        return await ReadWhenStorageExistsAsync(async ct =>
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                                   select last_seq_id, last_updated
                                   from {_events.ProgressionTableName}
                                   where name = @name
                                   """;
            command.Parameters.AddWithValue("@name", ShardState.HighWaterMark);

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            return new HighWaterStatus(reader.GetInt64(0), SqliteTimestamp.FromDatabaseValue(reader.GetString(1)));
        }, (HighWaterStatus?)null, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Drop one shard's progress row by its exact identity.
    /// </summary>
    /// <remarks>
    ///     Exact equality rather than a prefix match, so ejecting <c>tally:All</c> cannot also drop
    ///     <c>tally:AllOther</c>. A missing row is a clean no-op — the abstraction deliberately targets
    ///     orphaned shards that may never have been registered.
    /// </remarks>
    public async Task DeleteProjectionProgressByShardNameAsync(string shardIdentity,
        CancellationToken token = default)
    {
        await _options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"delete from {_events.ProgressionTableName} where name = @name";
            command.Parameters.AddWithValue("@name", shardIdentity);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     The highest sequence physically present in <c>fi_events</c>.
    /// </summary>
    /// <remarks>
    ///     Distinct from the high-water mark, which is the highest sequence safe to <em>read</em>. On
    ///     SQLite the two are usually equal, because one writer per file means there is no window where
    ///     a lower sequence is still uncommitted behind a higher one — the gap the sibling stores'
    ///     high-water detectors exist to handle.
    /// </remarks>
    public async Task<long> FetchHighestEventSequenceNumber(CancellationToken token)
    {
        return await ReadWhenStorageExistsAsync(async ct =>
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"select coalesce(max(seq_id), 0) from {_events.EventsTableName}";

            return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }, 0L, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     The highest sequence at or before a point in time, or null when the store has nothing that
    ///     old — used to floor a rebuild at a timestamp.
    /// </summary>
    /// <remarks>
    ///     Comparing ISO-8601 UTC text lexicographically is the same ordering as comparing the instants,
    ///     which is the property <see cref="SqliteTimestamp" />'s fixed-width format exists for.
    /// </remarks>
    public async Task<long?> FindEventStoreFloorAtTimeAsync(DateTimeOffset timestamp, CancellationToken token)
    {
        return await ReadWhenStorageExistsAsync(async ct =>
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"select max(seq_id) from {_events.EventsTableName} where timestamp <= @timestamp";
            command.Parameters.AddWithValue("@timestamp", SqliteTimestamp.ToDatabaseValue(timestamp));

            var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result is null or DBNull ? null : (long?)Convert.ToInt64(result);
        }, (long?)null, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Block until every registered async shard has caught up to the current high-water mark.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Polls rather than subscribing to the tracker, because the caller wants a definitive answer
    ///         about persisted progression rather than about what the in-memory tracker has been told.
    ///         Times out with <see cref="TimeoutException" /> rather than returning quietly, since a
    ///         caller that asked to wait for non-stale data and got stale data anyway has no way to tell.
    ///     </para>
    ///     <para>
    ///         <b>"Every registered shard" means the shards the store has registered, not the rows
    ///         <c>fi_event_progression</c> happens to hold</b> (fisher#102). Reading the answer off the
    ///         rows makes a shard that has not run yet <em>invisible</em>, because a shard with no row
    ///         has no sequence to be behind — so with two async projections the wait returned the moment
    ///         the first one reached the head, while the second had never started. That is a window
    ///         rather than a state: it lasts from the first shard committing its progression row to the
    ///         second committing its first, which is why it presented as an intermittent on a loaded
    ///         two-core CI runner and never locally. What it costs is the whole point of the call —
    ///         behind <c>QueryForNonStaleData</c> an application is told its data is current while a
    ///         projection has never run, and reads empty documents that look like answers.
    ///     </para>
    ///     <para>
    ///         The consequence worth knowing: <b>a store with a registered async projection whose daemon
    ///         is not running now waits out the timeout</b> where it used to return early. That is the
    ///         honest answer — the data really is stale — but it turns a fast bogus success into a
    ///         <see cref="TimeoutException" />, and the message names the shards that are missing rather
    ///         than only the ones that are behind, because "never started" and "still catching up" are
    ///         different operational situations.
    ///     </para>
    ///     <para>
    ///         Subscriptions are shards too and are included, since the daemon runs them and they record
    ///         progression exactly as a projection does — a subscription left behind is as much a reason
    ///         to call the data stale.
    ///     </para>
    ///     <para>
    ///         <strong>Every cancellation this method's own clock causes becomes that
    ///         <see cref="TimeoutException" />, wherever in the cycle it lands.</strong> The two reads
    ///         take the same token as the delay, so translating only the delay's cancellation meant the
    ///         caller saw an <see cref="OperationCanceledException" /> whenever the timeout happened to
    ///         elapse while a query was in flight — the same condition reported as two different
    ///         exception types depending on timing alone (fisher#7).
    ///     </para>
    ///     <para>
    ///         <b>The correlation is <see cref="ProjectionLagCalculator" />'s, not a fourth hand-rolled
    ///         one</b> (jasperfx#619). Its xmldoc names Marten's <c>WaitForNonStaleDataAsync</c> as one
    ///         of the three independent implementations the lift exists to collapse, and this is
    ///         Fisher's copy of the same semantic — anchor on the shards the store <em>registers</em>,
    ///         treat a missing row as fully behind rather than caught up, and never let a bookkeeping
    ///         row masquerade as a projection. Adopting it also buys two rules Fisher's own version did
    ///         not have: a progression row is matched at the shard's <em>current version</em>, so a
    ///         version bump reads as "no progress yet" rather than silently borrowing the previous
    ///         version's row; and a row whose name does not parse as a shard identity is dropped
    ///         rather than compared by string (marten#5161).
    ///     </para>
    ///     <para>
    ///         <b>The bar stays <c>max(seq_id)</c> rather than
    ///         <see cref="ProjectionLag.HighWaterMark" />, and that is deliberate.</b> The calculator
    ///         measures each cell against the persisted high-water row, which is the right bar for a
    ///         status endpoint — a shard cannot advance past a mark the agent has not published. It is
    ///         the wrong bar for <em>this</em> caller: a session that just committed and then asked for
    ///         non-stale data is asking about its own events, which are at <c>max(seq_id)</c> and may
    ///         sit above a mark the agent has not reached yet, so measuring against the mark would
    ///         return early on exactly the question the call exists to answer. On SQLite that ceiling
    ///         is honest with no safe-zone reasoning behind it — committed sequences are contiguous,
    ///         the same fact <c>FisherHighWaterDetector</c> rests on — so it is strictly stricter than
    ///         the mark. <see cref="ProjectionLag.HasProgressionRow" /> and
    ///         <see cref="ProjectionLag.Sequence" /> are what is read here;
    ///         <see cref="ProjectionLag.Lag" /> and <see cref="ProjectionLag.IsCaughtUp" /> are
    ///         measured against the mark and deliberately are not.
    ///     </para>
    /// </remarks>
    public async Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);

        // Resolved once: the registration is fixed for the store's lifetime, and the whole point is
        // that this set does not shrink to whatever has managed to write a row yet.
        var registered = _options.Projections.AllShards().Select(x => x.Name).ToArray();

        var highWater = 0L;
        var lags = Array.Empty<ProjectionLag>();

        try
        {
            while (true)
            {
                highWater = await FetchHighestEventSequenceNumber(cancellation.Token).ConfigureAwait(false);
                var progress = await AllProjectionProgress(cancellation.Token).ConfigureAwait(false);

                lags = ProjectionLagCalculator.Calculate(registered, progress, Identifier).ToArray();

                // No events means nothing can be stale, whatever any shard has recorded.
                if (highWater == 0)
                {
                    return;
                }

                // Every registered cell, present and at the head. A cell with no row is behind by
                // definition, which is exactly the case the row-derived version could not see.
                if (lags.All(x => x.HasProgressionRow && x.Sequence >= highWater))
                {
                    return;
                }

                await Task.Delay(50, cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException e) when (e.CancellationToken == cancellation.Token
                                                   || cancellation.IsCancellationRequested)
        {
            // Filtered on this method's own token so that if an overload ever accepts the caller's,
            // their cancellation still surfaces as a cancellation rather than as a timeout.
            var missing = lags.Where(x => !x.HasProgressionRow).Select(x => x.Shard.Identity).ToArray();

            var detail = missing.Length > 0
                ? $" Registered shards that have not recorded any progress: [{string.Join(", ", missing)}]"
                  + (_tracker is null ? " — the daemon may not be running." : ".")
                : string.Empty;

            var recorded = lags.Where(x => x.HasProgressionRow)
                .Select(x => $"{x.Shard.Identity}:{x.Sequence}");

            throw new TimeoutException(
                $"Projection data was still stale after {timeout}. High water is at {highWater}; "
                + $"shards are at [{string.Join(", ", recorded)}]."
                + detail
                + DescribeLaggingAgents(lags.Where(x => !x.HasProgressionRow || x.Sequence < highWater)));
        }
    }

    /// <summary>
    ///     What the daemon in this process last said about each lagging shard, for the timeout message
    ///     (fisher#329).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A timeout that names a shard and nothing else cannot tell "slow" from "stopped". fisher#329
    ///         was exactly that: one CI run reported <c>AggregateMemoVectors:All</c> with no progress and
    ///         "the daemon may not be running" beside a sibling shard at the head — so the daemon WAS
    ///         running, and whatever stopped that one agent was thrown away with its logger. The tracker
    ///         the daemon publishes through holds each agent's last state, including a pause reason and the
    ///         exception, so the timeout now says which.
    ///     </para>
    ///     <para>
    ///         Read, never created: a store with no daemon in this process has no tracker yet, and building
    ///         one here would claim an observation that was never made. That case keeps the old hint.
    ///     </para>
    /// </remarks>
    private string DescribeLaggingAgents(IEnumerable<ProjectionLag> lagging)
    {
        if (_tracker is null)
        {
            return string.Empty;
        }

        var described = lagging
            .Select(lag =>
            {
                var identity = lag.Shard.Identity;

                if (!_tracker.TryGetCurrentState(identity, out var state))
                {
                    return $"{identity}: no agent has reported any state in this process";
                }

                var parts = new List<string> { $"{identity}: last {state.Action} at {state.Sequence}" };

                if (state.AgentStatus is { } status)
                {
                    parts.Add($"agent {status}");
                }

                if (state.PauseReason is { } reason)
                {
                    parts.Add($"paused: {Headline(reason)}");
                }

                if (state.Exception is { } exception)
                {
                    parts.Add($"{exception.GetType().Name}: {exception.Message}");
                }
                else if (state.Failure is { } failure)
                {
                    parts.Add($"{failure.Category} failure, {failure.ExceptionType}: {failure.Message}");
                }

                return string.Join(", ", parts);
            })
            .ToArray();

        return described.Length == 0 ? string.Empty : $" Agents: [{string.Join("; ", described)}].";
    }

    /// <summary>
    ///     An exception's text without its stack trace: the outer line and every inner
    ///     <c>---&gt;</c> line.
    /// </summary>
    /// <remarks>
    ///     JasperFx records a pause reason as the whole exception, frames included, and a timeout
    ///     carrying one per lagging shard is unreadable. The inner lines have to survive, though — an
    ///     apply failure arrives wrapped in <c>ApplyEventException</c>, whose own message names the event
    ///     and not what went wrong with it.
    /// </remarks>
    private static string Headline(string exceptionText)
    {
        var lines = exceptionText.Split('\n');

        return string.Join(" ", lines
            .Where((line, index) => index == 0 || line.TrimStart().StartsWith("--->", StringComparison.Ordinal))
            .Select(line => line.Trim()));
    }

    /// <summary>
    ///     Create the storage a projection writes into, if it does not exist.
    /// </summary>
    /// <remarks>
    ///     Delegates to the same on-demand document-table path a synchronous <c>Store</c> takes, so a
    ///     snapshot type gets its table whether the first write comes from an inline projection or from
    ///     the daemon.
    ///     <para>
    ///         <b><see cref="IEvent" /> means the event store's own tables</b> (fisher#333), which is how
    ///         the daemon asks before it starts. It used to fall through to the no-op below, so a daemon
    ///         built over a fresh file started against tables nobody had created.
    ///     </para>
    /// </remarks>
    public Task EnsureStorageExistsAsync(Type storageType, CancellationToken token)
    {
        if (storageType == typeof(IEvent) || storageType == typeof(StreamAction))
        {
            return EnsureEventStorageAsync(token);
        }

        return _options.Schema.HasMappingFor(storageType)
            ? EnsureDocumentTableAsync(storageType, token)
            : Task.CompletedTask;
    }

    /// <summary>
    ///     Quarantine an event a projection could not apply, so its shard can keep advancing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>On its own connection, outside any batch's transaction.</strong> The batch that
    ///         produced this failure is about to roll back; a dead letter written inside it would roll
    ///         back with the very failure it is recording, and the shard would skip the event with no
    ///         trace of why. That is why <paramref name="storage" /> — the session the daemon offers as
    ///         a storage context — is ignored.
    ///     </para>
    ///     <para>
    ///         The write is an upsert on the version-7 id JasperFx assigns at construction. The daemon
    ///         retries this write in the background, so a retry that lands after a successful first
    ///         attempt must not fail on the primary key.
    ///     </para>
    /// </remarks>
    public async Task StoreDeadLetterEventAsync(object storage, DeadLetterEvent deadLetterEvent,
        CancellationToken token)
    {
        await _options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                                   insert into {_events.DeadLetterTableName}
                                     (id, projection_name, shard_name, event_sequence, tenant_id,
                                      exception_type, exception_message, timestamp)
                                   values (@id, @projection, @shard, @seq, @tenant, @type, @message, @timestamp)
                                   on conflict (id) do update
                                     set exception_type = excluded.exception_type,
                                         exception_message = excluded.exception_message,
                                         timestamp = excluded.timestamp;
                                   """;

            // Lowercase canonical text, as every Guid in Fisher is. A raw Guid binds as a 16-byte BLOB
            // that never matches the TEXT column; the provider's own string form is uppercase and misses
            // under the case-sensitive default collation.
            command.Parameters.AddWithValue("@id", deadLetterEvent.Id.ToString("D").ToLowerInvariant());
            command.Parameters.AddWithValue("@projection", deadLetterEvent.ProjectionName);
            command.Parameters.AddWithValue("@shard", deadLetterEvent.ShardName);
            command.Parameters.AddWithValue("@seq", deadLetterEvent.EventSequence);
            command.Parameters.AddWithValue("@tenant", (object?)deadLetterEvent.TenantId ?? DBNull.Value);
            command.Parameters.AddWithValue("@type", (object?)deadLetterEvent.ExceptionType ?? DBNull.Value);
            command.Parameters.AddWithValue("@message", (object?)deadLetterEvent.ExceptionMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("@timestamp",
                SqliteTimestamp.ToDatabaseValue(deadLetterEvent.Timestamp));

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     How many events one shard has quarantined — the primary "this projection is unhealthy"
    ///     signal, since a skipping shard keeps advancing and reports healthy otherwise.
    /// </summary>
    public async Task<long> CountDeadLetterEventsAsync(ShardName shard, CancellationToken token = default)
    {
        return await ReadWhenStorageExistsAsync(async ct =>
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                                   select count(*) from {_events.DeadLetterTableName}
                                   where projection_name = @projection and shard_name = @shard
                                   """;
            command.Parameters.AddWithValue("@projection", shard.Name);
            command.Parameters.AddWithValue("@shard", shard.ShardKey);

            return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }, 0L, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     One shard's quarantined events, newest first, paged.
    /// </summary>
    /// <remarks>
    ///     A null <paramref name="tenantId" /> spans every tenant in the database. Fisher has no
    ///     tenant-partitioned event sequence, so in practice that is all of them — the parameter is
    ///     honoured rather than rejected because the column is there and a conjoined store can use it.
    /// </remarks>
    public async Task<IReadOnlyList<DeadLetterEvent>> QueryDeadLetterEventsAsync(ShardName shard,
        string? tenantId, int offset, int limit, CancellationToken token = default)
    {
        return await ReadWhenStorageExistsAsync(async ct =>
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();

            var tenantFilter = tenantId is null ? "" : " and tenant_id = @tenant";

            // `limit`/`offset`, not TOP or FETCH NEXT. A bare offset is a parse error in SQLite, which
            // is why the limit is always emitted even when the caller wanted everything.
            command.CommandText = $"""
                                   select id, projection_name, shard_name, event_sequence, tenant_id,
                                          exception_type, exception_message, timestamp
                                   from {_events.DeadLetterTableName}
                                   where projection_name = @projection and shard_name = @shard{tenantFilter}
                                   order by event_sequence desc
                                   limit @limit offset @offset
                                   """;
            command.Parameters.AddWithValue("@projection", shard.Name);
            command.Parameters.AddWithValue("@shard", shard.ShardKey);
            command.Parameters.AddWithValue("@limit", limit <= 0 ? -1 : limit);
            command.Parameters.AddWithValue("@offset", Math.Max(0, offset));

            if (tenantId is not null)
            {
                command.Parameters.AddWithValue("@tenant", tenantId);
            }

            var results = new List<DeadLetterEvent>();

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add(new DeadLetterEvent
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    ProjectionName = reader.GetString(1),
                    ShardName = reader.GetString(2),
                    EventSequence = reader.GetInt64(3),
                    TenantId = await reader.IsDBNullAsync(4, ct).ConfigureAwait(false) ? null : reader.GetString(4),
                    ExceptionType = await reader.IsDBNullAsync(5, ct).ConfigureAwait(false)
                        ? null!
                        : reader.GetString(5),
                    ExceptionMessage = await reader.IsDBNullAsync(6, ct).ConfigureAwait(false)
                        ? null!
                        : reader.GetString(6),
                    Timestamp = SqliteTimestamp.FromDatabaseValue(reader.GetString(7))
                });
            }

            return (IReadOnlyList<DeadLetterEvent>)results;
        }, (IReadOnlyList<DeadLetterEvent>)Array.Empty<DeadLetterEvent>(), token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Every shard's dead-letter count in one read — the "give me every row" shape
    ///     <see cref="AllProjectionProgress" /> has, for the monitoring tools that render a table.
    /// </summary>
    public Task<IReadOnlyList<DeadLetterShardCount>> FetchDeadLetterCountsAsync(
        CancellationToken token = default)
        => FetchDeadLetterCountsAsync(tenantId: null, token);

    /// <summary>
    ///     The same counts scoped to one tenant, with the tenant stamped onto each row (fisher#77).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Without this the call lands on JasperFx's default, which throws
    ///         <see cref="NotSupportedException" /> for a non-null tenant — so a monitoring console
    ///         rendering per-tenant dead-letter badges got an exception where the store-global overload
    ///         beside it worked.
    ///     </para>
    ///     <para>
    ///         Cheap because Fisher's dead-letter table is store-global and records the failing event's
    ///         tenant as an ordinary data column: this is the store-global query with a
    ///         <c>where tenant_id = …</c> and the same grouping, not a second query shape. That is true
    ///         under conjoined tenancy; under database-per-tenant every tenant has its own
    ///         <c>fi_dead_letters</c>, so the filter is a no-op that costs nothing and still returns
    ///         rows stamped with the tenant a consumer keyed by.
    ///     </para>
    ///     <para>
    ///         <b>A null tenant is store-global and leaves <c>TenantId</c> null</b> rather than
    ///         defaulting it, which is what the interface asks for — a consumer keying by
    ///         <c>{ProjectionName}:{ShardKey}</c> must be able to tell "every tenant" from "the default
    ///         tenant". Rows whose <c>tenant_id</c> is NULL are counted in the store-global answer and
    ///         reachable from no tenant-scoped one, which is the honest reading of a dead letter the
    ///         daemon recorded without a tenant.
    ///     </para>
    /// </remarks>
    public async Task<IReadOnlyList<DeadLetterShardCount>> FetchDeadLetterCountsAsync(string? tenantId,
        CancellationToken token = default)
    {
        return await ReadWhenStorageExistsAsync(async ct =>
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();

            var tenantFilter = tenantId is null ? "" : " where tenant_id = @tenant";

            command.CommandText = $"""
                                   select projection_name, shard_name, count(*)
                                   from {_events.DeadLetterTableName}{tenantFilter}
                                   group by projection_name, shard_name
                                   """;

            if (tenantId is not null)
            {
                command.Parameters.AddWithValue("@tenant", tenantId);
            }

            var results = new List<DeadLetterShardCount>();

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add(new DeadLetterShardCount(reader.GetString(0), reader.GetString(1),
                    reader.GetInt64(2), tenantId));
            }

            return (IReadOnlyList<DeadLetterShardCount>)results;
        }, (IReadOnlyList<DeadLetterShardCount>)Array.Empty<DeadLetterShardCount>(), token).ConfigureAwait(false);
    }
}

/// <summary>
///     Where the high-water mark stands, and when its agent last said so (fisher#60).
/// </summary>
/// <param name="Sequence">The highest event sequence the daemon has marked as safe to read.</param>
/// <param name="LastUpdated">
///     When the row was last written. Moves on every idle poll cycle while
///     <see cref="EventStoreOptions.HighWaterLivenessInterval" /> is positive, and only on an advance
///     otherwise.
/// </param>
public sealed record HighWaterStatus(long Sequence, DateTimeOffset LastUpdated);
