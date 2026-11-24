using System.Globalization;
using System.Text;
using Fisher.Events.Schema;
using Fisher.Events.Storage;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;
using Microsoft.Data.Sqlite;

namespace Fisher.Storage;

/// <summary>
///     Parked projection identities and projection slice indexes (fisher#652, marten#6003): the store side of the
///     daemon's parking, over <c>fi_parked_identities</c> and the <c>fi_slice_index_*</c> tables.
/// </summary>
/// <remarks>
///     <para>
///         The writes that have to commit with a projection batch (park, unpark, slice index rows) are not here: they
///         run inside the batch's <c>BEGIN IMMEDIATE</c> transaction, through the command builders at the bottom of
///         this file, which <see cref="Fisher.Events.Daemon.FisherProjectionBatch" /> calls. Everything here runs on
///         its own connection, out of band.
///     </para>
///     <para>
///         The reads answer an empty list when the table isn't there yet, for fisher#332's reason: a monitor is most
///         likely to ask before the schema has been applied.
///     </para>
/// </remarks>
public partial class FisherDatabase
{
    private const string ParkedColumns =
        "projection_name, projection_version, shard_key, tenant_id, identity, identity_type, first_failing_seq, "
        + "resume_after, failure_category, exception_type, exception_message, event_type, event_id, failing_stream, "
        + "parked_at, replay_attempts, last_attempt_at, last_attempt_error, requested_action, requested_at";

    /// <inheritdoc />
    public async Task<IReadOnlyList<ParkedIdentity>> FetchParkedIdentitiesAsync(ParkedIdentityQuery query,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return await ReadWhenStorageExistsAsync(async ct =>
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();

            var where = new StringBuilder(" where 1 = 1");
            if (query.ProjectionName is not null)
            {
                where.Append(" and projection_name = @name");
                command.Parameters.AddWithValue("@name", query.ProjectionName);
            }

            if (query.ProjectionVersion is { } version)
            {
                where.Append(" and projection_version = @version");
                command.Parameters.AddWithValue("@version", (long)version);
            }

            if (query.ShardKey is not null)
            {
                where.Append(" and shard_key = @key");
                command.Parameters.AddWithValue("@key", query.ShardKey);
            }

            if (query.TenantId is not null)
            {
                where.Append(" and tenant_id = @tenant");
                command.Parameters.AddWithValue("@tenant", query.TenantId);
            }

            command.CommandText =
                $"select {ParkedColumns} from {_events.ParkedIdentitiesTableName}{where} "
                + "order by parked_at, first_failing_seq limit @limit";
            command.Parameters.AddWithValue("@limit", Math.Max(query.Limit, 0));

            var parked = await readParkedAsync(command, ct).ConfigureAwait(false);
            return await withNaturalKeysAsync(parked, connection, ct).ConfigureAwait(false);
        }, (IReadOnlyList<ParkedIdentity>)[], token).ConfigureAwait(false);
    }

    // At most this many stream identities go into one natural key lookup, well under SQLite's 32,766 variables
    private const int NaturalKeyLookupChunk = 500;

    /// <summary>
    ///     fisher#667 (jasperfx#1273, port of marten#6132): fill <see cref="ParkedIdentity.NaturalKey" /> for every
    ///     parked slice whose single-stream aggregate declares a natural key, with one lookup per projection against
    ///     that aggregate's <c>fi_natural_key_*</c> table. A row matches on the stream identity, and on the tenant too
    ///     under conjoined tenancy, where the same key can exist once per tenant. No natural key, or no row for the
    ///     stream, leaves it null. The key is only read here, never stored on the parked row.
    /// </summary>
    private async Task<IReadOnlyList<ParkedIdentity>> withNaturalKeysAsync(IReadOnlyList<ParkedIdentity> parked,
        SqliteConnection connection, CancellationToken token)
    {
        if (parked.Count == 0) return parked;

        List<ParkedIdentity>? filled = null;

        foreach (var byProjection in parked.Select((row, index) => (row, index)).GroupBy(x => x.row.ProjectionName))
        {
            if (naturalKeyForProjection(byProjection.Key) is not { } naturalKey) continue;

            var keys = await readNaturalKeysAsync(naturalKey,
                    byProjection.Select(x => x.row.IdentityText).Distinct().ToList(), connection, token)
                .ConfigureAwait(false);

            foreach (var (row, index) in byProjection)
            {
                if (!keys.TryGetValue((tenantForNaturalKey(row.TenantId), row.IdentityText), out var key)) continue;

                filled ??= [..parked];
                filled[index] = row with { NaturalKey = key };
            }
        }

        return filled ?? parked;
    }

    // Only a single-stream aggregate's identity is a stream identity, which is what fi_natural_key_* maps a key to.
    // The owned key first; an aggregate with only mapped keys shows its first one, read from the lookup its
    // source owns (fisher#679, as marten#6150 does).
    private NaturalKeyDefinition? naturalKeyForProjection(string projectionName)
        => _options.Projections.All
            .Where(x => x.Name == projectionName)
            .OfType<IAggregateProjection>()
            .Where(x => x.Scope == AggregationScope.SingleStream)
            .Select(x => x.NaturalKeyDefinition
                         ?? x.MappedNaturalKeyDefinitions.FirstOrDefault(m => m.SourceProjectionDefinition is not null))
            .FirstOrDefault(x => x is not null);

    // Without conjoined tenancy the lookup table has no tenant column: one database, one key per stream
    private string tenantForNaturalKey(string tenantId)
        => _events.TenancyStyle == TenancyStyle.Conjoined ? tenantId : "";

    private async Task<Dictionary<(string Tenant, string Stream), string>> readNaturalKeysAsync(
        NaturalKeyDefinition naturalKey, IReadOnlyList<string> streams, SqliteConnection connection,
        CancellationToken token)
    {
        var keys = new Dictionary<(string Tenant, string Stream), string>();
        var streamColumn = NaturalKeySql.StreamColumn(_events);
        var tenantColumn = _events.TenancyStyle == TenancyStyle.Conjoined ? StorageConstants.TenantIdColumn : "''";
        var table = _events.QuotedNaturalKeyTableName(naturalKey.LookupAggregateType);

        try
        {
            foreach (var chunk in streams.Chunk(NaturalKeyLookupChunk))
            {
                await using var command = connection.CreateCommand();
                var parameters = new string[chunk.Length];
                for (var i = 0; i < chunk.Length; i++)
                {
                    parameters[i] = $"@s{i}";
                    command.Parameters.AddWithValue(parameters[i], chunk[i]);
                }

                command.CommandText = $"select {tenantColumn}, {streamColumn}, {NaturalKeyTable.KeyColumn} from {table} "
                                      + $"where {streamColumn} in ({string.Join(", ", parameters)})";

                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    keys[(reader.GetString(0), reader.GetString(1))] =
                        Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture)!;
                }
            }
        }
        catch (SqliteException e) when (SqliteSchemaErrors.IsMissingStorage(e))
        {
            // No lookup table yet: the parked list still answers, without natural keys
            return [];
        }

        return keys;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ParkedShardCount>> FetchParkedCountsAsync(string? tenantId,
        CancellationToken token = default)
    {
        return await ReadWhenStorageExistsAsync(async ct =>
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();

            var filter = tenantId is null ? "" : " where tenant_id = @tenant";
            if (tenantId is not null) command.Parameters.AddWithValue("@tenant", tenantId);

            command.CommandText = $"""
                                   select projection_name, projection_version, shard_key, tenant_id, count(*)
                                   from {_events.ParkedIdentitiesTableName}{filter}
                                   group by projection_name, projection_version, shard_key, tenant_id
                                   order by projection_name, projection_version, shard_key, tenant_id
                                   """;

            var counts = new List<ParkedShardCount>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                counts.Add(new ParkedShardCount(reader.GetString(0), (uint)reader.GetInt64(1), reader.GetString(2),
                    reader.GetString(3), (int)reader.GetInt64(4)));
            }

            return (IReadOnlyList<ParkedShardCount>)counts;
        }, (IReadOnlyList<ParkedShardCount>)[], token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Mark every parked row that matches with a durable replay or discard request. The node that runs the shard
    ///     picks it up on its heartbeat.
    /// </summary>
    /// <remarks>
    ///     One autocommit <c>update</c>. "The oldest N" is the N rows with the earliest <c>parked_at</c>, chosen by a
    ///     subselect on the rowid so the order and the limit are SQLite's, not a read followed by a write.
    /// </remarks>
    public async Task<int> RequestParkedActionAsync(ParkedActionRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await _options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();

            var where = new StringBuilder(" where projection_name = @name");
            command.Parameters.AddWithValue("@name", request.ProjectionName);

            if (request.ProjectionVersion is { } version)
            {
                where.Append(" and projection_version = @version");
                command.Parameters.AddWithValue("@version", (long)version);
            }

            if (request.TenantId is not null)
            {
                where.Append(" and tenant_id = @tenant");
                command.Parameters.AddWithValue("@tenant", request.TenantId);
            }

            if (request.IdentityText is not null)
            {
                where.Append(" and identity = @identity");
                command.Parameters.AddWithValue("@identity", request.IdentityText);
            }

            var limit = request.IdentityText is null && request.Oldest is { } oldest ? oldest : -1;

            command.CommandText = $"""
                                   update {_events.ParkedIdentitiesTableName}
                                   set requested_action = @action, requested_at = @at
                                   where rowid in (select rowid from {_events.ParkedIdentitiesTableName}{where}
                                                   order by parked_at, first_failing_seq limit @limit)
                                   """;
            command.Parameters.AddWithValue("@action", request.Action.ToString());
            command.Parameters.AddWithValue("@at", SqliteTimestamp.ToDatabaseValue(_events.TimeProvider.GetUtcNow()));
            command.Parameters.AddWithValue("@limit", limit);

            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Every parked row of one shard, read once when the shard starts. An empty table answers with one indexed
    ///     read: the primary key leads with the projection name.
    /// </summary>
    public Task<IReadOnlyList<ParkedIdentity>> LoadParkedIdentitiesAsync(ShardName shard,
        CancellationToken token = default)
        => loadShardParkedAsync(shard, requestedOnly: false, token);

    /// <summary>The parked rows of one shard that carry a durable request, oldest request first.</summary>
    public Task<IReadOnlyList<ParkedIdentity>> LoadRequestedParkedActionsAsync(ShardName shard,
        CancellationToken token = default)
        => loadShardParkedAsync(shard, requestedOnly: true, token);

    /// <summary>
    ///     A replay or discard failed: count the attempt, keep the error, and clear the request so the heartbeat
    ///     doesn't retry it until someone asks again. Best effort and out of band.
    /// </summary>
    public async Task RecordParkedReplayFailureAsync(ParkedIdentityKey key, DateTimeOffset attemptedAt,
        string error, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        await _options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                                   update {_events.ParkedIdentitiesTableName}
                                   set replay_attempts = replay_attempts + 1,
                                       last_attempt_at = @at,
                                       last_attempt_error = @error,
                                       requested_action = null,
                                       requested_at = null
                                   where {ParkedKeyPredicate}
                                   """;
            command.Parameters.AddWithValue("@at", SqliteTimestamp.ToDatabaseValue(attemptedAt));
            command.Parameters.AddWithValue("@error", error);
            BindParkedKey(command, key);

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Delete the slice index rows of one projection version that are at or below
    ///     <see cref="SliceIndexPruneRequest.DeleteThrough" /> and whose event was appended before
    ///     <see cref="SliceIndexPruneRequest.OlderThan" /> (or is gone). Answers the highest sequence it deleted.
    /// </summary>
    /// <remarks>
    ///     The highest sequence and the delete run in one <c>BEGIN IMMEDIATE</c> transaction, so a batch can't add
    ///     a row between them. A projection version with no index table answers null.
    /// </remarks>
    public async Task<long?> PruneSliceIndexAsync(SliceIndexPruneRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var table = _events.SliceIndexTableName(request.ProjectionName, request.ProjectionVersion);

        try
        {
            return await _options.ResiliencePipeline.ExecuteAsync(async ct =>
            {
                await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
                await using var transaction = (SqliteTransaction)await connection
                    .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);

                var where = $"""
                             where i.seq_id <= @through
                               {(request.TenantId is null ? "" : "and i.tenant_id = @tenant")}
                               and not exists (select 1 from {_events.EventsTableName} e
                                               where e.seq_id = i.seq_id and e.timestamp >= @older)
                             """;

                long? highest;
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = $"select max(i.seq_id) from {table} i {where}";
                    bindPrune(command, request);

                    var raw = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    highest = raw is null or DBNull ? null : Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                }

                if (highest is not null)
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = $"delete from {table} as i {where}";
                    bindPrune(command, request);
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return highest;
            }, token).ConfigureAwait(false);
        }
        catch (Exception e) when (SqliteSchemaErrors.IsMissingStorage(e))
        {
            return null;
        }
    }

    private static void bindPrune(SqliteCommand command, SliceIndexPruneRequest request)
    {
        command.Parameters.AddWithValue("@through", request.DeleteThrough);
        command.Parameters.AddWithValue("@older", SqliteTimestamp.ToDatabaseValue(request.OlderThan));
        if (request.TenantId is not null) command.Parameters.AddWithValue("@tenant", request.TenantId);
    }

    private async Task<IReadOnlyList<ParkedIdentity>> loadShardParkedAsync(ShardName shard, bool requestedOnly,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(shard);

        return await ReadWhenStorageExistsAsync(async ct =>
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();

            var tenant = shard.TenantId is null ? "" : " and tenant_id = @tenant";
            var requested = requestedOnly ? " and requested_action is not null" : "";
            var order = requestedOnly ? "requested_at, parked_at" : "parked_at, first_failing_seq";

            command.CommandText = $"""
                                   select {ParkedColumns} from {_events.ParkedIdentitiesTableName}
                                   where projection_name = @name and projection_version = @version and shard_key = @key{tenant}{requested}
                                   order by {order}
                                   """;
            command.Parameters.AddWithValue("@name", shard.Name);
            command.Parameters.AddWithValue("@version", (long)shard.Version);
            command.Parameters.AddWithValue("@key", shard.ShardKey);
            if (shard.TenantId is not null) command.Parameters.AddWithValue("@tenant", shard.TenantId);

            return await readParkedAsync(command, ct).ConfigureAwait(false);
        }, (IReadOnlyList<ParkedIdentity>)[], token).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ParkedIdentity>> readParkedAsync(SqliteCommand command,
        CancellationToken token)
    {
        var rows = new List<ParkedIdentity>();

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            rows.Add(readParked(reader));
        }

        return rows;
    }

    private static ParkedIdentity readParked(SqliteDataReader reader)
    {
        string? text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        DateTimeOffset? at(int i) => reader.IsDBNull(i) ? null : SqliteTimestamp.FromDatabaseValue(reader.GetString(i));

        var identityText = reader.GetString(4);
        var identityType = reader.GetString(5);

        return new ParkedIdentity
        {
            ProjectionName = reader.GetString(0),
            ProjectionVersion = (uint)reader.GetInt64(1),
            ShardKey = reader.GetString(2),
            TenantId = reader.GetString(3),
            IdentityText = identityText,
            IdentityType = identityType,
            Identity = IdentityFromText(identityText, identityType),
            FirstFailingSequence = reader.GetInt64(6),
            ResumeAfter = reader.GetInt64(7),
            Category = Enum.TryParse<JasperFx.Events.Daemon.ShardFailureCategory>(text(8), out var category)
                ? category
                : default,
            ExceptionType = text(9),
            ExceptionMessage = text(10),
            EventTypeName = text(11),
            EventId = text(12) is { } eventId ? Guid.Parse(eventId) : null,
            FailingStream = text(13),
            ParkedAt = SqliteTimestamp.FromDatabaseValue(reader.GetString(14)),
            ReplayAttempts = (int)reader.GetInt64(15),
            LastAttemptAt = at(16),
            LastAttemptError = text(17),
            RequestedAction = Enum.TryParse<ParkedAction>(text(18), out var action) ? action : null,
            RequestedAt = at(19)
        };
    }

    /// <summary>
    ///     The stored identity back as its simple value. The daemon wraps a strong-typed identifier itself.
    /// </summary>
    internal static object IdentityFromText(string text, string identityType) => identityType switch
    {
        "guid" => Guid.Parse(text),
        "int" => int.Parse(text, CultureInfo.InvariantCulture),
        "long" => long.Parse(text, CultureInfo.InvariantCulture),
        _ => text
    };

    // ---- inside a projection batch's transaction ----

    private const string ParkedKeyPredicate =
        "projection_name = @name and projection_version = @version and shard_key = @key "
        + "and tenant_id = @tenant and identity = @identity";

    private static void BindParkedKey(SqliteCommand command, ParkedIdentityKey key)
    {
        command.Parameters.AddWithValue("@name", key.ProjectionName);
        command.Parameters.AddWithValue("@version", (long)key.ProjectionVersion);
        command.Parameters.AddWithValue("@key", key.ShardKey);
        command.Parameters.AddWithValue("@tenant", key.TenantId);
        command.Parameters.AddWithValue("@identity", key.IdentityText);
    }

    /// <summary>
    ///     Write (insert or replace) a parked row on the batch's connection, in its transaction.
    /// </summary>
    internal async Task WriteParkedAsync(ParkedIdentity parked, SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = _options.CommandTimeout;
        command.CommandText = $"""
                               insert into {_events.ParkedIdentitiesTableName} ({ParkedColumns})
                               values (@name, @version, @key, @tenant, @identity, @identity_type, @first, @resume,
                                       @category, @exception_type, @exception_message, @event_type, @event_id,
                                       @failing_stream, @parked_at, @attempts, @last_at, @last_error, @action, @requested_at)
                               on conflict (projection_name, projection_version, shard_key, tenant_id, identity) do update
                                 set identity_type = excluded.identity_type,
                                     first_failing_seq = excluded.first_failing_seq,
                                     resume_after = excluded.resume_after,
                                     failure_category = excluded.failure_category,
                                     exception_type = excluded.exception_type,
                                     exception_message = excluded.exception_message,
                                     event_type = excluded.event_type,
                                     event_id = excluded.event_id,
                                     failing_stream = excluded.failing_stream,
                                     parked_at = excluded.parked_at,
                                     replay_attempts = excluded.replay_attempts,
                                     last_attempt_at = excluded.last_attempt_at,
                                     last_attempt_error = excluded.last_attempt_error,
                                     requested_action = excluded.requested_action,
                                     requested_at = excluded.requested_at;
                               """;

        object nullable(object? value) => value ?? DBNull.Value;
        object timestamp(DateTimeOffset? value) =>
            value is { } at ? SqliteTimestamp.ToDatabaseValue(at) : DBNull.Value;

        BindParkedKey(command, parked.Key);
        command.Parameters.AddWithValue("@identity_type", parked.IdentityType);
        command.Parameters.AddWithValue("@first", parked.FirstFailingSequence);
        command.Parameters.AddWithValue("@resume", parked.ResumeAfter);
        command.Parameters.AddWithValue("@category", parked.Category.ToString());
        command.Parameters.AddWithValue("@exception_type", nullable(parked.ExceptionType));
        command.Parameters.AddWithValue("@exception_message", nullable(parked.ExceptionMessage));
        command.Parameters.AddWithValue("@event_type", nullable(parked.EventTypeName));
        command.Parameters.AddWithValue("@event_id", nullable(parked.EventId?.ToString("D")));
        command.Parameters.AddWithValue("@failing_stream", nullable(parked.FailingStream));
        command.Parameters.AddWithValue("@parked_at", SqliteTimestamp.ToDatabaseValue(
            parked.ParkedAt == default ? _events.TimeProvider.GetUtcNow() : parked.ParkedAt));
        command.Parameters.AddWithValue("@attempts", parked.ReplayAttempts);
        command.Parameters.AddWithValue("@last_at", timestamp(parked.LastAttemptAt));
        command.Parameters.AddWithValue("@last_error", nullable(parked.LastAttemptError));
        command.Parameters.AddWithValue("@action", nullable(parked.RequestedAction?.ToString()));
        command.Parameters.AddWithValue("@requested_at", timestamp(parked.RequestedAt));

        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    /// <summary>Delete a parked row on the batch's connection, in its transaction. A missing row is fine.</summary>
    internal async Task DeleteParkedAsync(ParkedIdentityKey key, SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = _options.CommandTimeout;
        command.CommandText = $"delete from {_events.ParkedIdentitiesTableName} where {ParkedKeyPredicate}";
        BindParkedKey(command, key);

        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    // Three parameters a row, well under SQLite's 32,766 variables
    private const int SliceIndexRowsPerStatement = 500;

    /// <summary>
    ///     Insert slice index rows on the batch's connection, in its transaction. <c>on conflict do nothing</c>, because
    ///     a rebuild flush may meet a row a crashed earlier attempt left behind.
    /// </summary>
    internal async Task WriteSliceIndexAsync(SliceIndexRows rows, SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (rows.Entries.Count == 0) return;

        var table = _events.SliceIndexTableName(rows.ProjectionName, rows.ProjectionVersion);

        for (var start = 0; start < rows.Entries.Count; start += SliceIndexRowsPerStatement)
        {
            var count = Math.Min(SliceIndexRowsPerStatement, rows.Entries.Count - start);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = _options.CommandTimeout;

            var sql = new StringBuilder($"insert into {table} (tenant_id, identity, seq_id) values ");
            for (var i = 0; i < count; i++)
            {
                var entry = rows.Entries[start + i];
                if (i > 0) sql.Append(", ");
                sql.Append($"(@t{i}, @i{i}, @s{i})");
                command.Parameters.AddWithValue($"@t{i}", entry.TenantId);
                command.Parameters.AddWithValue($"@i{i}", SliceIndexIdentityValue(entry.Identity));
                command.Parameters.AddWithValue($"@s{i}", entry.Sequence);
            }

            sql.Append(" on conflict do nothing");
            command.CommandText = sql.ToString();

            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     How a slice index identity is bound: a Guid as lowercase canonical text, a strong-typed id as the value
    ///     it wraps, anything else as it is.
    /// </summary>
    internal static object SliceIndexIdentityValue(object identity)
        => ParkedIdentityText.Unwrap(identity) switch
        {
            Guid guid => guid.ToString("D"),
            var other => other
        };

    // ---- cleanup beside the other projection bookkeeping ----

    /// <summary>
    ///     The slice index tables a shard identity (or a bare projection name) owns, by the projection versions the
    ///     store knows about: the registered one, and the one the identity names.
    /// </summary>
    internal IReadOnlyList<string> SliceIndexTablesFor(string shardIdentity, out string projectionName,
        out uint? version, out string? shardKey)
    {
        version = null;
        shardKey = null;
        projectionName = shardIdentity;

        if (shardIdentity.Contains(':') && ShardName.TryParse(shardIdentity, out var shard) && shard is not null)
        {
            projectionName = shard.Name;
            version = shard.Version;
            shardKey = shard.ShardKey;
        }

        var name = projectionName;
        var versions = _events.SliceIndexedProjections()
            .Where(x => x.Name == name)
            .Select(x => x.Version)
            .ToList();

        if (version is { } named && !versions.Contains(named)) versions.Add(named);
        if (version is { } only) versions.RemoveAll(x => x != only);

        return versions.Select(x => _events.SliceIndexTableName(name, x)).ToList();
    }

    /// <summary>
    ///     A rewind of <paramref name="shardIdentity" /> to <paramref name="floor" />: parked rows that failed above the
    ///     floor go (the shard parks them again when it gets there, and the ones below stay frozen), and so do the
    ///     slice index rows above it, which the shard writes again. A null floor clears everything the shard owns.
    /// </summary>
    internal async Task DeleteParkedAndSliceIndexAboveAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string shardIdentity, long? floor, CancellationToken token)
    {
        var tables = SliceIndexTablesFor(shardIdentity, out var projectionName, out var version, out var shardKey);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            var sql = new StringBuilder($"delete from {_events.ParkedIdentitiesTableName} where projection_name = @name");
            command.Parameters.AddWithValue("@name", projectionName);

            if (version is { } v)
            {
                sql.Append(" and projection_version = @version and shard_key = @key");
                command.Parameters.AddWithValue("@version", (long)v);
                command.Parameters.AddWithValue("@key", shardKey);
            }

            if (floor is { } f)
            {
                sql.Append(" and first_failing_seq > @floor");
                command.Parameters.AddWithValue("@floor", f);
            }

            command.CommandText = sql.ToString();
            await executeIgnoringMissingTableAsync(command, token).ConfigureAwait(false);
        }

        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = floor is null
                ? $"delete from {table}"
                : $"delete from {table} where seq_id > @floor";
            if (floor is { } f) command.Parameters.AddWithValue("@floor", f);

            await executeIgnoringMissingTableAsync(command, token).ConfigureAwait(false);
        }

        // fisher#654: the shard's parked_count follows its rows
        await RefreshParkedCountsAsync(connection, transaction,
            shard => shard.Name == projectionName && (version is null || shard.Version == version && shard.ShardKey == shardKey),
            token).ConfigureAwait(false);
    }

    /// <summary>
    ///     fisher#654: set <c>parked_count</c> on the progression rows of the shards <paramref name="which" /> picks to
    ///     the number of their rows in <c>fi_parked_identities</c>, on the caller's connection and transaction.
    /// </summary>
    /// <remarks>
    ///     For the paths that delete parked rows without a projection batch: a rewind, an eject, a tenant wipe. Reads
    ///     the progression rows' names first, because a shard name has to be parsed to find its rows.
    /// </remarks>
    internal async Task RefreshParkedCountsAsync(SqliteConnection connection, SqliteTransaction? transaction,
        Func<ShardName, bool> which, CancellationToken token)
    {
        if (!_events.EnableExtendedProgressionTracking) return;

        var names = new List<string>();
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"select name from {_events.ProgressionTableName}";

            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                if (name.Contains(':') && ShardName.TryParse(name, out var shard) && shard is not null &&
                    shard.Name != ShardState.HighWaterMark && which(shard))
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception e) when (SqliteSchemaErrors.IsMissingStorage(e))
        {
            return;
        }

        await RefreshParkedCountsAsync(connection, transaction, names, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     fisher#654: set <c>parked_count</c> on the named progression rows to the number of their shard's rows in
    ///     <c>fi_parked_identities</c>, on the caller's connection and transaction.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The parked table is the truth, so the count is counted from it in the same transaction that changed
    ///         it, rather than taken from the daemon's in-memory count. Before JasperFx 3.11.0,
    ///         <c>ExtendedProgressionWriter</c> dropped the <c>Parked</c> and <c>Unparked</c> publications that carry
    ///         the in-memory count while periodic heartbeats were off (jasperfx#1255, fixed upstream in 3.11.0). It
    ///         writes them now, but a state stamped before this commit can still be written after it. So <see cref="WriteExtendedProgressionAsync(IReadOnlyList{ShardState},CancellationToken)" />
    ///         only fills the column in while it is null and never overwrites a count written here.
    ///     </para>
    ///     <para>
    ///         A row whose column is null and whose shard has nothing parked stays null: null is "doesn't park, or
    ///         hasn't said yet", and the writer fills in 0 for a shard that parks. A row without a parsable shard
    ///         name, or that no longer exists, is left alone.
    ///     </para>
    /// </remarks>
    internal async Task RefreshParkedCountsAsync(SqliteConnection connection, SqliteTransaction? transaction,
        IEnumerable<string> shardIdentities, CancellationToken token)
    {
        if (!_events.EnableExtendedProgressionTracking) return;

        var progression = _events.ProgressionTableName;

        foreach (var identity in shardIdentities.Distinct(StringComparer.Ordinal))
        {
            if (!ShardName.TryParse(identity, out var shard) || shard is null) continue;

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = _options.CommandTimeout;

            var tenant = shard.TenantId is null ? "" : " and tenant_id = @tenant";
            command.CommandText = $"""
                                   update {progression}
                                   set parked_count = (select case when count(*) = 0 and {progression}.parked_count is null
                                                                   then null else count(*) end
                                                       from {_events.ParkedIdentitiesTableName}
                                                       where projection_name = @name and projection_version = @version
                                                         and shard_key = @key{tenant})
                                   where name = @shard
                                   """;
            command.Parameters.AddWithValue("@shard", identity);
            command.Parameters.AddWithValue("@name", shard.Name);
            command.Parameters.AddWithValue("@version", (long)shard.Version);
            command.Parameters.AddWithValue("@key", shard.ShardKey);
            if (shard.TenantId is not null) command.Parameters.AddWithValue("@tenant", shard.TenantId);

            await executeIgnoringMissingTableAsync(command, token).ConfigureAwait(false);
        }
    }

    /// <summary>The same as the other overload, on a connection of its own.</summary>
    internal async Task DeleteParkedAndSliceIndexAboveAsync(string shardIdentity, long? floor,
        CancellationToken token)
    {
        await _options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await DeleteParkedAndSliceIndexAboveAsync(connection, null, shardIdentity, floor, ct)
                .ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }

    // A file whose schema hasn't been applied has nothing to clean
    private static async Task executeIgnoringMissingTableAsync(SqliteCommand command, CancellationToken token)
    {
        try
        {
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        catch (Exception e) when (SqliteSchemaErrors.IsMissingStorage(e))
        {
        }
    }
}
