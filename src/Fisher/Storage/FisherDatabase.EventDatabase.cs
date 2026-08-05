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
    ///     How far one shard has processed.
    /// </summary>
    /// <remarks>
    ///     Zero for a shard with no row yet, which is what the daemon reads as "start from the
    ///     beginning" — a missing row and a row at zero mean the same thing and neither is an error.
    /// </remarks>
    public async Task<long> ProjectionProgressFor(ShardName name, CancellationToken token = default)
    {
        return await _options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"select last_seq_id from {_events.ProgressionTableName} where name = @name";
            command.Parameters.AddWithValue("@name", name.Identity);

            var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result is null or DBNull ? 0L : Convert.ToInt64(result);
        }, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Every shard's progress, including the high-water row.
    /// </summary>
    public async Task<IReadOnlyList<ShardState>> AllProjectionProgress(CancellationToken token = default)
    {
        return await _options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"select name, last_seq_id from {_events.ProgressionTableName}";

            var states = new List<ShardState>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                states.Add(new ShardState(reader.GetString(0), reader.GetInt64(1)));
            }

            return (IReadOnlyList<ShardState>)states;
        }, token).ConfigureAwait(false);
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
        return await _options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"select coalesce(max(seq_id), 0) from {_events.EventsTableName}";

            return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }, token).ConfigureAwait(false);
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
        return await _options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"select max(seq_id) from {_events.EventsTableName} where timestamp <= @timestamp";
            command.Parameters.AddWithValue("@timestamp", SqliteTimestamp.ToDatabaseValue(timestamp));

            var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result is null or DBNull ? null : (long?)Convert.ToInt64(result);
        }, token).ConfigureAwait(false);
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
    ///         <strong>Every cancellation this method's own clock causes becomes that
    ///         <see cref="TimeoutException" />, wherever in the cycle it lands.</strong> The two reads
    ///         take the same token as the delay, so translating only the delay's cancellation meant the
    ///         caller saw an <see cref="OperationCanceledException" /> whenever the timeout happened to
    ///         elapse while a query was in flight — the same condition reported as two different
    ///         exception types depending on timing alone (fisher#7).
    ///     </para>
    /// </remarks>
    public async Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);

        var highWater = 0L;
        var shards = Array.Empty<ShardState>();

        try
        {
            while (true)
            {
                highWater = await FetchHighestEventSequenceNumber(cancellation.Token).ConfigureAwait(false);
                var progress = await AllProjectionProgress(cancellation.Token).ConfigureAwait(false);

                shards = progress
                    .Where(x => !string.Equals(x.ShardName, ShardState.HighWaterMark,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                // No events means nothing can be stale. Shards that exist must all have reached the head.
                if (highWater == 0 || (shards.Length > 0 && shards.All(x => x.Sequence >= highWater)))
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
            throw new TimeoutException(
                $"Projection data was still stale after {timeout}. High water is at {highWater}; "
                + $"shards are at [{string.Join(", ", shards.Select(x => $"{x.ShardName}:{x.Sequence}"))}].");
        }
    }

    /// <summary>
    ///     Create the storage a projection writes into, if it does not exist.
    /// </summary>
    /// <remarks>
    ///     Delegates to the same on-demand document-table path a synchronous <c>Store</c> takes, so a
    ///     snapshot type gets its table whether the first write comes from an inline projection or from
    ///     the daemon.
    /// </remarks>
    public Task EnsureStorageExistsAsync(Type storageType, CancellationToken token)
        => _options.Schema.HasMappingFor(storageType)
            ? EnsureDocumentTableAsync(storageType, token)
            : Task.CompletedTask;

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
        return await _options.ResiliencePipeline.ExecuteAsync(async ct =>
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
        }, token).ConfigureAwait(false);
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
        return await _options.ResiliencePipeline.ExecuteAsync(async ct =>
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
        }, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Every shard's dead-letter count in one read — the "give me every row" shape
    ///     <see cref="AllProjectionProgress" /> has, for the monitoring tools that render a table.
    /// </summary>
    public async Task<IReadOnlyList<DeadLetterShardCount>> FetchDeadLetterCountsAsync(
        CancellationToken token = default)
    {
        return await _options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                                   select projection_name, shard_name, count(*)
                                   from {_events.DeadLetterTableName}
                                   group by projection_name, shard_name
                                   """;

            var results = new List<DeadLetterShardCount>();

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add(new DeadLetterShardCount(reader.GetString(0), reader.GetString(1),
                    reader.GetInt64(2)));
            }

            return (IReadOnlyList<DeadLetterShardCount>)results;
        }, token).ConfigureAwait(false);
    }
}
