using System.Buffers;
using System.Text;
using System.Text.Json;
using Fisher.Events.Internal;
using Fisher.Storage;
using JasperFx.Events;
using JasperFx.Events.Fetching;
using Microsoft.Data.Sqlite;

namespace Fisher.Events;

/// <summary>
///     <c>FetchManyForWriting</c>: several streams fetched for writing in two statements rather than two
///     per stream (fisher#374 / jasperfx#930).
/// </summary>
public partial class EventOperations
{
    /// <summary>
    ///     Fetch the aggregate of each of <paramref name="ids" /> for writing, in the order given.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The many-stream form of <see cref="FetchForWriting{T}(Guid,CancellationToken)" />, and the
    ///         same thing per stream: each handle carries the version its own stream had when it was
    ///         read, and <c>SaveChangesAsync</c> guards each one separately. A stream that does not exist
    ///         comes back with a null aggregate and a handle that will start it.
    ///     </para>
    ///     <para>
    ///         <b>Two statements whatever the count</b>, where the contract's default makes two per id:
    ///         one reads every stream's version, the other every stream's events. Each stream's events
    ///         are bounded by the version just read, so an append committed between the two statements
    ///         cannot be folded into an aggregate labelled with the older version.
    ///     </para>
    ///     <para>
    ///         The aggregate write cache applies per stream exactly as it does to a single fetch: a
    ///         stream with a usable baseline reads only the events after it.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="ids" /> repeats an id.</exception>
    public Task<IReadOnlyList<IEventStream<T>>> FetchManyForWriting<T>(IReadOnlyList<Guid> ids,
        CancellationToken cancellation = default) where T : class
    {
        AssertGuidIdentity();
        return FetchManyForWritingAsync<T, Guid>(ids, nameof(ids), cancellation);
    }

    /// <inheritdoc cref="FetchManyForWriting{T}(IReadOnlyList{Guid},CancellationToken)" />
    /// <exception cref="ArgumentException"><paramref name="keys" /> repeats a key.</exception>
    public Task<IReadOnlyList<IEventStream<T>>> FetchManyForWriting<T>(IReadOnlyList<string> keys,
        CancellationToken cancellation = default) where T : class
    {
        AssertStringIdentity();
        return FetchManyForWritingAsync<T, string>(keys, nameof(keys), cancellation);
    }

    private async Task<IReadOnlyList<IEventStream<T>>> FetchManyForWritingAsync<T, TId>(IReadOnlyList<TId> ids,
        string paramName, CancellationToken token) where T : class where TId : notnull
    {
        ArgumentNullException.ThrowIfNull(ids, paramName);

        // Refused before anything is read, as the contract's default does: two handles on one stream
        // would each guard on the same starting version and the second commit would always lose.
        var seen = new HashSet<TId>();
        foreach (var id in ids)
        {
            if (!seen.Add(id))
            {
                throw new ArgumentException(
                    $"{nameof(FetchManyForWriting)} was given the stream identity '{id}' more than once. Each stream can be fetched for writing once per call, because two handles on one stream would race each other's expected version.",
                    paramName);
            }
        }

        if (ids.Count == 0)
        {
            return [];
        }

        var versions = await ReadStreamVersionsAsync(ids, token).ConfigureAwait(false);

        // Claim each stream's cached baseline before reading, so the event read can start after it.
        // Take-on-read, as in AggregateForWritingAsync: a claimed entry is ours to fold onto.
        var cache = Graph.AggregateWriteCaching.ResolveCache(typeof(T));
        var plans = new List<FetchPlan<T>>(ids.Count);

        foreach (var id in ids)
        {
            var databaseId = DatabaseStreamId(id);
            var version = versions.TryGetValue(databaseId, out var v) ? v : (long?)null;
            var plan = new FetchPlan<T>(id, databaseId, version, AggregateCacheKeyFor<T>(id));

            if (version > 0 && cache.TryTake(plan.CacheKey, out var claimed, out var baseline)
                            && claimed is T typed && baseline > 0 && baseline <= version)
            {
                plan.Baseline = typed;
                plan.FromVersion = baseline + 1;
            }

            plans.Add(plan);
        }

        var toRead = plans.Where(x => x.Version > 0 && x.FromVersion <= x.Version).ToList();
        if (toRead.Count > 0)
        {
            await ReadEventsForPlansAsync(toRead, token).ConfigureAwait(false);
        }

        var aggregator = Graph.AggregatorFor<T>();
        var streams = new IEventStream<T>[plans.Count];

        for (var i = 0; i < plans.Count; i++)
        {
            var plan = plans[i];
            T? aggregate = null;

            if (plan.Version > 0)
            {
                // Same rules as AggregateStreamAsync: nothing new folds to the baseline itself, and a
                // fold that produced something gets the stream's identity stamped on it.
                aggregate = plan.Events.Count == 0
                    ? plan.Baseline
                    : await aggregator.BuildAsync(plan.Events, _session, plan.Baseline, token).ConfigureAwait(false);

                if (aggregate is not null)
                {
                    AggregateIdentity.TrySetIdentity(aggregate, plan.Id);
                    RecordAggregateCacheWriteBack(cache, plan.CacheKey, aggregate, plan.Version!.Value);
                }
            }

            var action = TrackForWriting(plan.Id, plan.Version);

            streams[i] = plan.Id is Guid guid
                ? EventStream<T>.ForGuid(this, action, guid, aggregate, token)
                : EventStream<T>.ForString(this, action, (string)plan.Id, aggregate, token);
        }

        return streams;
    }

    /// <summary>
    ///     Every requested stream's current version in one statement, keyed by the database's rendering
    ///     of its id. A stream with no row is simply absent.
    /// </summary>
    private async Task<Dictionary<string, long>> ReadStreamVersionsAsync<TId>(IReadOnlyList<TId> ids,
        CancellationToken token) where TId : notnull
    {
        var sql = $"select id, version from {Graph.StreamsTableName} where id in (select value from json_each(@ids))";

        if (IsConjoined)
        {
            sql += " and tenant_id = @tenant_id";
        }

        var connection = await _session.EventConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = _session.Options.CommandTimeout;
        command.Parameters.Add(new SqliteParameter("ids", StreamIdsAsJsonArray(ids))
        {
            SqliteType = SqliteType.Text
        });
        BindTenantIfConjoined(command);

        var versions = new Dictionary<string, long>(ids.Count, StringComparer.Ordinal);

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            versions[reader.GetString(0)] = reader.GetInt64(1);
        }

        return versions;
    }

    /// <summary>
    ///     Every planned stream's events in one statement, each stream bounded by its own version range.
    /// </summary>
    /// <remarks>
    ///     The ranges travel as one JSON array and are unpacked into a CTE. <c>json_each</c> is not
    ///     joined directly because its own <c>id</c> and <c>type</c> columns collide with
    ///     <c>fi_events</c>' and the canonical select list is unqualified.
    /// </remarks>
    private async Task ReadEventsForPlansAsync<T>(IReadOnlyList<FetchPlan<T>> plans, CancellationToken token)
        where T : class
    {
        var options = _session.Options.Events;
        var sql = new StringBuilder()
            .Append("with wanted(stream_id, from_version, to_version) as (select json_extract(value, '$.s'), ")
            .Append("json_extract(value, '$.f'), json_extract(value, '$.t') from json_each(@ranges)) ")
            .Append("select ")
            .Append(FisherEventsRowReader.ComposeSelectColumns(options))
            .Append(" from ")
            .Append(Graph.EventsTableName)
            .Append(" where stream_id in (select stream_id from wanted)")
            .Append(" and exists (select 1 from wanted w where w.stream_id = ")
            .Append(Graph.EventsTableName)
            .Append(".stream_id and ")
            .Append(Graph.EventsTableName)
            .Append(".version between w.from_version and w.to_version)");

        if (IsConjoined)
        {
            sql.Append(" and tenant_id = @tenant_id");
        }

        sql.Append(" order by stream_id, version");

        var connection = await _session.EventConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        command.CommandTimeout = _session.Options.CommandTimeout;
        command.Parameters.Add(new SqliteParameter("ranges", RangesAsJsonArray(plans))
        {
            SqliteType = SqliteType.Text
        });
        BindTenantIfConjoined(command);

        var byDatabaseId = plans.ToDictionary(x => x.DatabaseId, StringComparer.Ordinal);

        // Empty stream id in the context: the rows span streams, so each event takes its identity off
        // its own row. See FisherEventsRowReader.ReadEventAcrossStreams.
        var ctx = new EventHydrationContext(Graph, _session.FisherSerializer, string.Empty, TenantId);
        var slots = MetadataSlots.For(options);
        var isGuid = IsGuidIdentity;

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var @event = await FisherEventsRowReader.ReadEventAcrossStreams(reader, ctx, slots, isGuid, token)
                .ConfigureAwait(false);

            // Null means dotnet_type named a type this process cannot resolve; skip it, as the single
            // stream read does.
            if (@event is null)
            {
                continue;
            }

            var databaseId = isGuid
                ? (string)SqliteStorageDialect<Guid>.ToDatabaseValue(@event.StreamId)
                : @event.StreamKey!;

            byDatabaseId[databaseId].Events.Add(@event);
        }
    }

    private void BindTenantIfConjoined(SqliteCommand command)
    {
        if (IsConjoined)
        {
            command.Parameters.Add(new SqliteParameter("tenant_id", TenantId) { SqliteType = SqliteType.Text });
        }
    }

    private static string DatabaseStreamId<TId>(TId id) where TId : notnull
        => id is Guid guid ? (string)SqliteStorageDialect<Guid>.ToDatabaseValue(guid) : (string)(object)id;

    // The two JSON parameters are written with Utf8JsonWriter rather than JsonSerializer, as
    // SqliteStorageDialect writes its id arrays: the serializer's generic overload is reflection-based
    // and carries RequiresDynamicCode (IL3050), which a Native AOT consumer sees as a warning against
    // Fisher. The shapes are fixed, so there is nothing for reflection to discover.
    private static string StreamIdsAsJsonArray<TId>(IReadOnlyList<TId> ids) where TId : notnull
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();

            foreach (var id in ids)
            {
                writer.WriteStringValue(DatabaseStreamId(id));
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RangesAsJsonArray<T>(IReadOnlyList<FetchPlan<T>> plans) where T : class
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();

            foreach (var plan in plans)
            {
                writer.WriteStartObject();
                writer.WriteString("s", plan.DatabaseId);
                writer.WriteNumber("f", plan.FromVersion);
                writer.WriteNumber("t", plan.Version!.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private sealed class FetchPlan<T>(object id, string databaseId, long? version, AggregateCacheKey cacheKey)
        where T : class
    {
        public object Id { get; } = id;
        public string DatabaseId { get; } = databaseId;
        public long? Version { get; } = version;
        public AggregateCacheKey CacheKey { get; } = cacheKey;
        public T? Baseline { get; set; }
        public long FromVersion { get; set; } = 1;
        public List<IEvent> Events { get; } = [];
    }
}
