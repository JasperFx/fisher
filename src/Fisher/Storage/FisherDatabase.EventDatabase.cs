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
    ///     Polls rather than subscribing to the tracker, because the caller wants a definitive answer
    ///     about persisted progression rather than about what the in-memory tracker has been told.
    ///     Times out with <see cref="TimeoutException" /> rather than returning quietly, since a caller
    ///     that asked to wait for non-stale data and got stale data anyway has no way to tell.
    /// </remarks>
    public async Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);

        while (true)
        {
            var highWater = await FetchHighestEventSequenceNumber(cancellation.Token).ConfigureAwait(false);
            var progress = await AllProjectionProgress(cancellation.Token).ConfigureAwait(false);

            var shards = progress
                .Where(x => !string.Equals(x.ShardName, ShardState.HighWaterMark, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            // No events means nothing can be stale. Shards that exist must all have reached the head.
            if (highWater == 0 || (shards.Length > 0 && shards.All(x => x.Sequence >= highWater)))
            {
                return;
            }

            try
            {
                await Task.Delay(50, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"Projection data was still stale after {timeout}. High water is at {highWater}; "
                    + $"shards are at [{string.Join(", ", shards.Select(x => $"{x.ShardName}:{x.Sequence}"))}].");
            }
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
    ///     Dead-letter storage is not implemented, so a failing event stops its shard rather than being
    ///     quarantined.
    /// </summary>
    /// <remarks>
    ///     The interface's other dead-letter members default to empty rather than throwing, which is
    ///     honest for a store with no dead-letter table: there genuinely are none to report. This one
    ///     throws because silently discarding a poison event would lose it.
    /// </remarks>
    public Task StoreDeadLetterEventAsync(object storage, DeadLetterEvent deadLetterEvent, CancellationToken token)
        => throw new NotSupportedException(
            "Fisher has no dead letter queue yet, so a projection error cannot be quarantined. Configure the "
            + "projection to stop on error instead of skipping.");
}
