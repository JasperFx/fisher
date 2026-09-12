using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using Fisher.Events.Internal;
using Fisher.Storage;
using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using Microsoft.Extensions.Logging;

namespace Fisher;

/// <summary>
///     Fisher's <see cref="IEventStore" /> implementation — the store-agnostic surface that monitoring
///     and tooling code (CritterWatch, the event store explorer) reads a Critter Stack event store
///     through.
/// </summary>
/// <remarks>
///     <para>
///         Implemented explicitly, as Polecat does, so none of it lands on <see cref="DocumentStore" />'s
///         own public API. Application code uses sessions; this surface exists for tools.
///     </para>
///     <para>
///         Most of <see cref="IEventStore" /> is default-implemented by the interface itself and left
///         alone here. What Fisher overrides is the explorer reads it can answer from <c>fi_streams</c>
///         and <c>fi_events</c> — recent streams, one stream's events, one stream's metadata — at all
///         three of their scopes, plus the required members.
///     </para>
///     <para>
///         <b>Those reads have a database dimension as well as a tenant one</b> (fisher#240,
///         jasperfx#810), and on Fisher that is not theoretical: a database-per-tenant store is a file
///         per tenant. See the block above the reads themselves for what a store-global answer means
///         once there is more than one file, and why a listing merges where a single-stream lookup
///         refuses.
///     </para>
///     <para>
///         <b>Nothing here throws any more</b> (fisher#15). The standing discipline, for the next
///         member that arrives ahead of the feature, is that one Fisher cannot honour throws naming its
///         milestone rather than returning an empty result a monitoring tool would render as "no data".
///     </para>
/// </remarks>
public partial class DocumentStore : IEventStore
{
    private static readonly Meter _meter = new("Fisher", typeof(DocumentStore).Assembly.GetName().Version?.ToString());

    private static readonly ActivitySource _activitySource =
        new("Fisher", typeof(DocumentStore).Assembly.GetName().Version?.ToString());

    Meter IEventStore.Meter => _meter;

    ActivitySource IEventStore.ActivitySource => _activitySource;

    string IEventStore.MetricsPrefix => "fisher";

    Uri IEventStore.Subject => Database.Describe().DatabaseUri();

    /// <summary>
    ///     How many databases this store spans — the tenancy's answer, which is not always
    ///     <see cref="DatabaseCardinality.Single" /> (fisher#240).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This used to claim <c>Single</c> unconditionally in prose while deferring to the
    ///         tenancy in code, and the prose was the stale half.</b> One SQLite <em>file</em> is one
    ///         database, but a database-per-tenant store is a file per tenant — <c>StaticMultiple</c>
    ///         under <see cref="StoreOptions.MultiTenantedDatabases" />, <c>DynamicMultiple</c> under
    ///         <c>MultiTenantedDatabasesInDirectory</c> / <c>MultiTenantedDatabasesInRegistry</c> — and
    ///         <c>FisherServiceCollectionExtensions</c> has branched on <c>DynamicMultiple</c> since the
    ///         daemon learned to run per tenant. The expression was right all along; the comment was
    ///         written before fisher#47 and never revisited.
    ///     </para>
    ///     <para>
    ///         It is not decorative. Every database-scoped explorer read below reads this to decide
    ///         whether a store-global answer is honest, so a store that claimed <c>Single</c> while
    ///         spanning a hundred files would answer from one of them and read as complete.
    ///     </para>
    /// </remarks>
    DatabaseCardinality IEventStore.DatabaseCardinality => Tenancy.Cardinality;

    /// <summary>
    ///     Whether this store partitions data by tenant at all, either way it can.
    /// </summary>
    /// <remarks>
    ///     <b>This said <see langword="false" /> unconditionally, which was wrong in both directions a
    ///     Fisher store can be multi-tenanted</b> (fisher#240): conjoined tenancy puts a
    ///     <c>tenant_id</c> column on the event tables and on every <c>MultiTenanted()</c> document
    ///     table, and database-per-tenant puts each tenant in its own file. A console reading
    ///     <see langword="false" /> renders no tenant dimension at all, so the tenant-scoped overloads
    ///     beside it are reachable by an API caller and invisible to the tool they exist for.
    /// </remarks>
    bool IEventStore.HasMultipleTenants
        => Tenancy.Cardinality != DatabaseCardinality.Single
           || Options.Events.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined
           || Options.Schema.AllMappings().Any(x => x.IsConjoined);

    EventStoreIdentity IEventStore.Identity => new(Options.DatabaseSchemaName, "fisher");

    /// <summary>
    ///     jasperfx#420 — how many projection rebuild cells may run concurrently against this database.
    /// </summary>
    /// <remarks>
    ///     An explicit <see cref="DaemonSettings.MaxConcurrentRebuildsPerDatabase" /> wins; a
    ///     non-positive value disables the cap entirely (<see langword="null" /> means "unbounded" to
    ///     JasperFx). Otherwise it derives from <see cref="StoreOptions.MaxPoolSize" /> as
    ///     <c>max(1, poolSize / 8)</c>, the same formula Marten and Polecat use — see
    ///     <see cref="StoreOptions.MaxPoolSize" /> for why Fisher's ceiling is a store option rather
    ///     than a connection-string keyword.
    /// </remarks>
    int? IEventStore.MaxConcurrentRebuildsPerDatabase => ResolveMaxConcurrentRebuilds();

    private int? ResolveMaxConcurrentRebuilds()
    {
        var configured = Options.DaemonSettings.MaxConcurrentRebuildsPerDatabase;
        if (configured.HasValue)
        {
            return configured.Value > 0 ? configured.Value : null;
        }

        return Math.Max(1, Options.MaxPoolSize / 8);
    }

    // ---- explorer reads ----
    //
    // jasperfx#810 / fisher#240 — these reads have three scopes, and which one a caller reaches for is
    // not a preference.
    //
    //   * database-scoped  — the widest honest answer on a store with more than one file. A tool walks
    //                        AllDatabases() and attributes each answer to the database it came from.
    //   * tenant-scoped    — one tenant, wherever that tenant lives.
    //   * store-global     — every database, and only meaningful where one answer can stand for all of
    //                        them. A listing merges; a lookup that names ONE stream cannot, and refuses.
    //
    // The refusal is the point of the issue. Before this, every one of these read Database — the store's
    // default file — so a database-per-tenant store answered from one tenant's file and the result was
    // indistinguishable from the whole store's. Silent, and asymmetric in the usual way: right for
    // whichever tenant happened to own the default file, wrong for every other one.

    /// <summary>
    ///     Which database a tenant's rows are in, and whether the tenant is also a column value.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A tenant predicate in SQL is correct only under conjoined tenancy, and that is the
    ///         half the old tenant-scoped <c>ReadStreamAsync</c> had backwards.</b> Under
    ///         database-per-tenant the tenant <em>is</em> the file: <c>StreamsTable</c> gives
    ///         <c>tenant_id</c> a <c>DEFAULT '*DEFAULT*'</c> for a store that is not conjoined, so every
    ///         row in <c>north.db</c> reads <c>*DEFAULT*</c> and <c>and tenant_id = 'north'</c> matches
    ///         nothing at all. Resolving the database is the whole of the scoping there.
    ///     </para>
    ///     <para>
    ///         An unknown tenant throws out of <see cref="ITenancy.DatabaseFor" /> rather than falling
    ///         back to the default file, which is the rule every tenant-scoped member follows.
    ///     </para>
    /// </remarks>
    private (FisherDatabase Database, string? ColumnPredicate) ResolveTenantScope(string tenantId)
        => (Tenancy.DatabaseFor(tenantId), TenantColumnPredicate(tenantId));

    /// <summary>
    ///     The <c>tenant_id</c> predicate a tenant deserves in SQL, which is none unless the store is
    ///     conjoined.
    /// </summary>
    /// <remarks>
    ///     Split from <see cref="ResolveTenantScope" /> for the database-scoped overloads, where the
    ///     caller has already named the database: asking the tenancy to resolve the tenant as well
    ///     would be work whose result is discarded, and would refuse an unregistered tenant id on a
    ///     read that was never going to consult the tenancy.
    /// </remarks>
    private string? TenantColumnPredicate(string? tenantId)
        => tenantId is not null && EventGraph.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined
            ? tenantId
            : null;

    /// <summary>
    ///     The refusal a store-global single-stream read gives once the store spans more than one file.
    /// </summary>
    /// <remarks>
    ///     A stream id is unique within a database and not across them, so there is no merge that
    ///     produces the one answer these signatures return — which is exactly why answering from the
    ///     default file was wrong rather than merely partial. The message names both ways forward, and
    ///     they are both reachable: <c>GetRecentStreamsAsync</c> fans out and stamps each summary with
    ///     its database's tenant, so a console that found a stream has the tenant id this refusal asks
    ///     for.
    /// </remarks>
    private NotSupportedException streamLookupNeedsAScope(string member)
        => new(
            $"Store-global {member} cannot answer on this Fisher store, whose DatabaseCardinality is "
            + $"{Tenancy.Cardinality} — it spans {Tenancy.AllDatabases().Count} database files, and a stream id is "
            + "unique within one of them rather than across them, so there is no single answer to return. "
            + $"Name the scope: {member}(tenantId, ...) for one tenant's file, or {member}(database, ...) "
            + "for a database from AllDatabases(). GetRecentStreamsAsync fans out and stamps each stream "
            + "with its tenant, which is where that tenant id comes from. See fisher#240, jasperfx#810.");

    // ---- recent streams ----

    Task<IReadOnlyList<StreamSummary>> IEventStore.GetRecentStreamsAsync(int count, CancellationToken ct)
        => RecentStreamsAcrossDatabasesAsync(count, null, ct);

    Task<IReadOnlyList<StreamSummary>> IEventStore.GetRecentStreamsAsync(int count, string? tenantId,
        CancellationToken ct)
        => RecentStreamsAcrossDatabasesAsync(count, tenantId, ct);

    Task<IReadOnlyList<StreamSummary>> IEventStore.GetRecentStreamsAsync(IEventDatabase database, int count,
        string? tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(database);

        return GetRecentStreamsAsync(DatabaseFrom(database), count, TenantColumnPredicate(tenantId), ct);
    }

    /// <summary>
    ///     The store-global listing, which <b>fans out and merges</b> rather than refusing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A listing is the one shape of this family where merging is what the caller meant.</b>
    ///         "The ten most recently updated streams in this store" has an answer across a hundred
    ///         files, and the ordering key makes it computable: <c>fi_streams.timestamp</c> is
    ///         <see cref="SqliteTimestamp" />'s fixed-width UTC text, so it compares across databases
    ///         exactly as it does within one. Reading <paramref name="count" /> from each file and
    ///         keeping the newest <paramref name="count" /> is the whole merge, and it is exact — a
    ///         stream outside a file's own top <paramref name="count" /> cannot be in the store's.
    ///     </para>
    ///     <para>
    ///         The cost is one file open per database, which is what makes this defensible on Fisher and
    ///         would not be on a sibling: these are local files, not server round trips. A store with
    ///         several hundred tenants should page per database through the database-scoped overload.
    ///     </para>
    /// </remarks>
    private async Task<IReadOnlyList<StreamSummary>> RecentStreamsAcrossDatabasesAsync(int count, string? tenantId,
        CancellationToken ct)
    {
        if (count <= 0)
        {
            return [];
        }

        if (tenantId is not null)
        {
            var (database, predicate) = ResolveTenantScope(tenantId);
            return await GetRecentStreamsAsync(database, count, predicate, ct).ConfigureAwait(false);
        }

        await RefreshTenantsAsync(ct).ConfigureAwait(false);

        var databases = Tenancy.AllDatabases();

        if (databases.Count == 1)
        {
            return await GetRecentStreamsAsync(databases[0], count, null, ct).ConfigureAwait(false);
        }

        var merged = new List<StreamSummary>();

        foreach (var database in databases)
        {
            merged.AddRange(await GetRecentStreamsAsync(database, count, null, ct).ConfigureAwait(false));
        }

        return merged
            .OrderByDescending(x => x.LastUpdatedAt)
            .Take(count)
            .ToList();
    }

    private async Task<IReadOnlyList<StreamSummary>> GetRecentStreamsAsync(FisherDatabase database, int count,
        string? tenantPredicate, CancellationToken ct)
    {
        if (count <= 0)
        {
            return [];
        }

        // Ordering by the ISO-8601 TEXT timestamp is a string sort, and correct only because
        // SqliteTimestamp.Format is fixed-width, UTC-normalised and millisecond-precision. A format
        // with a variable-width offset or no sub-second component would silently mis-order streams
        // written within the same second — and it is the same property the cross-database merge above
        // leans on, one level up.
        var sql = $"""
                   select {FisherStreamsRowReader.SelectColumns}
                   from {EventGraph.StreamsTableName}
                   {WhereTenant(tenantPredicate)}order by timestamp desc
                   limit @count;
                   """;

        var rows = await ReadStreamsAsync(database, sql,
            command =>
            {
                command.Parameters.AddWithValue("@count", count);
                if (tenantPredicate is not null) command.Parameters.AddWithValue("@tenant_id", tenantPredicate);
            },
            FisherStreamsRowReader.ReadStreamSummary,
            ct).ConfigureAwait(false);

        return database.TenantId is null
            ? rows
            : rows.Select(x => x with { TenantId = database.TenantId }).ToList();
    }

    // ---- one stream's events ----

    IAsyncEnumerable<EventRecord> IEventStore.ReadStreamAsync(string streamId, CancellationToken ct)
        => ReadStreamAsync(streamId, null, ct);

    IAsyncEnumerable<EventRecord> IEventStore.ReadStreamAsync(string streamId, string? tenantId,
        CancellationToken ct)
        => ReadStreamAsync(streamId, tenantId, ct);

    IAsyncEnumerable<EventRecord> IEventStore.ReadStreamAsync(IEventDatabase database, string streamId,
        string? tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(database);

        return ReadStreamAsync(DatabaseFrom(database), streamId, TenantColumnPredicate(tenantId), ct);
    }

    private IAsyncEnumerable<EventRecord> ReadStreamAsync(string streamId, string? tenantId, CancellationToken ct)
    {
        if (tenantId is not null)
        {
            var (database, predicate) = ResolveTenantScope(tenantId);
            return ReadStreamAsync(database, streamId, predicate, ct);
        }

        if (Tenancy.Cardinality != DatabaseCardinality.Single)
        {
            throw streamLookupNeedsAScope(nameof(IEventStore.ReadStreamAsync));
        }

        return ReadStreamAsync(Database, streamId, null, ct);
    }

    /// <summary>
    ///     Every event of one stream, in version order, as wire <see cref="EventRecord" />s.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Materialised inside <see cref="StoreOptions.ResiliencePipeline" /> and then yielded,
    ///         rather than streamed out of it — the same reason <c>ReadStreamsAsync</c> gives below. A
    ///         retried <c>SQLITE_BUSY</c> re-executes the whole delegate, so handing a live reader to the
    ///         caller would let a retry resume against a connection the previous attempt had already
    ///         disposed. A single stream is a bounded read, so holding it in memory costs little.
    ///     </para>
    ///     <para>
    ///         Rows are returned whether or not this process can resolve their CLR event type — see
    ///         <see cref="FisherEventsRowReader.ReadEventRecord" />. That is what lets the
    ///         <c>projection-run</c> CLI and a monitoring console read a stream without the consumer's
    ///         event assemblies.
    ///     </para>
    /// </remarks>
    private async IAsyncEnumerable<EventRecord> ReadStreamAsync(FisherDatabase database, string streamId,
        string? tenantPredicate, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamId);

        // Same normalisation as GetStreamMetadataAsync: under Guid identity the parse validates the
        // input and lowercases it to the canonical form fi_events holds, because SQLite's default
        // collation is case-sensitive and an uppercase Guid would match nothing.
        var id = EventGraph.StreamIdentity == StreamIdentity.AsGuid
            ? Guid.Parse(streamId).ToString()
            : streamId;

        var tenantFilter = tenantPredicate is null
            ? ""
            : "and tenant_id = @tenant_id\n                   ";

        var sql = $"""
                   select {FisherEventsRowReader.ComposeSelectColumns(EventGraph.EventOptions)}
                   from {EventGraph.EventsTableName}
                   where stream_id = @stream_id
                   {tenantFilter}order by version;
                   """;

        var ctx = new EventHydrationContext(
            EventGraph,
            Options.Serializer,
            id,
            defaultTenantId: StorageConstants.DefaultTenantId);

        var slots = MetadataSlots.For(EventGraph.EventOptions);

        var records = await Options.ResiliencePipeline.ExecuteAsync(async token =>
        {
            await using var connection = await database.OpenConnectionAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@stream_id", id);
            if (tenantPredicate is not null) command.Parameters.AddWithValue("@tenant_id", tenantPredicate);

            var results = new List<EventRecord>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                results.Add(FisherEventsRowReader.ReadEventRecord(reader, ctx, slots));
            }

            return (IReadOnlyList<EventRecord>)results;
        }, ct).ConfigureAwait(false);

        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            yield return record;
        }
    }

    // ---- one stream's metadata ----

    Task<StreamMetadata?> IEventStore.GetStreamMetadataAsync(string streamId, CancellationToken ct)
        => GetStreamMetadataAsync(streamId, null, ct);

    Task<StreamMetadata?> IEventStore.GetStreamMetadataAsync(string streamId, string? tenantId,
        CancellationToken ct)
        => GetStreamMetadataAsync(streamId, tenantId, ct);

    Task<StreamMetadata?> IEventStore.GetStreamMetadataAsync(IEventDatabase database, string streamId,
        string? tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(database);

        return GetStreamMetadataAsync(DatabaseFrom(database), streamId, TenantColumnPredicate(tenantId), ct);
    }

    private Task<StreamMetadata?> GetStreamMetadataAsync(string streamId, string? tenantId, CancellationToken ct)
    {
        if (tenantId is not null)
        {
            var (database, predicate) = ResolveTenantScope(tenantId);
            return GetStreamMetadataAsync(database, streamId, predicate, ct);
        }

        if (Tenancy.Cardinality != DatabaseCardinality.Single)
        {
            throw streamLookupNeedsAScope(nameof(IEventStore.GetStreamMetadataAsync));
        }

        return GetStreamMetadataAsync(Database, streamId, null, ct);
    }

    private async Task<StreamMetadata?> GetStreamMetadataAsync(FisherDatabase database, string streamId,
        string? tenantPredicate, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamId);

        // Under Guid identity the parse both validates the input and normalises its casing to the
        // lowercase canonical form fi_streams holds — SQLite's default collation is case-sensitive, so
        // an uppercase Guid string would match nothing. See SqliteGuidIdentification.
        var id = EventGraph.StreamIdentity == StreamIdentity.AsGuid
            ? Guid.Parse(streamId).ToString()
            : streamId;

        var tenantFilter = tenantPredicate is null ? "" : "and tenant_id = @tenant_id\n                   ";

        var sql = $"""
                   select {FisherStreamsRowReader.SelectColumns}
                   from {EventGraph.StreamsTableName}
                   where id = @id
                   {tenantFilter};
                   """;

        var rows = await ReadStreamsAsync(database, sql,
            command =>
            {
                command.Parameters.AddWithValue("@id", id);
                if (tenantPredicate is not null) command.Parameters.AddWithValue("@tenant_id", tenantPredicate);
            },
            FisherStreamsRowReader.ReadStreamMetadata,
            ct).ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return null;
        }

        return database.TenantId is null ? rows[0] : rows[0] with { TenantId = database.TenantId };
    }

    // ---- the one read Fisher answers at no scope ----

    /// <summary>
    ///     Refused at every scope, so the database argument changes nothing — and says so.
    /// </summary>
    /// <remarks>
    ///     <b>Overridden only to keep the message honest</b> (fisher#240). Fisher deliberately does not
    ///     implement the dictionary <c>QueryByTagsAsync</c> at all — <c>EventQuery.TagValues</c> is the
    ///     better home for that capability here, being composable and paged where the overload is
    ///     neither, so there is no second code path to keep in step. Left to the interface default, a
    ///     multi-database store would answer this with jasperfx#810's refusal, which blames the database
    ///     dimension for a read that does not exist at any dimension. Forwarding reaches the accurate
    ///     "not implemented" instead.
    /// </remarks>
    IAsyncEnumerable<EventRecord> IEventStore.QueryByTagsAsync(IEventDatabase database,
        IReadOnlyDictionary<string, string> tags, string? tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(database);

        return ((IEventStore)this).QueryByTagsAsync(tags, tenantId, ct);
    }

    private static string WhereTenant(string? tenantPredicate)
        => tenantPredicate is null ? "" : "where tenant_id = @tenant_id\n                   ";

    /// <summary>
    ///     Run a <c>fi_streams</c> read against one database through the store's resilience pipeline,
    ///     hydrating each row with <paramref name="read" />.
    /// </summary>
    /// <remarks>
    ///     Rows are materialised inside the pipeline rather than streamed out of it. A retried
    ///     <c>SQLITE_BUSY</c> re-executes the whole delegate, so handing a live reader back to the caller
    ///     would let a retry resume against a connection the previous attempt had already disposed.
    /// </remarks>
    private async Task<IReadOnlyList<T>> ReadStreamsAsync<T>(
        FisherDatabase database,
        string sql,
        Action<Microsoft.Data.Sqlite.SqliteCommand> configure,
        Func<System.Data.Common.DbDataReader, T> read,
        CancellationToken ct)
    {
        return await Options.ResiliencePipeline.ExecuteAsync(async token =>
        {
            await using var connection = await database.OpenConnectionAsync(token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            configure(command);

            var results = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                results.Add(read(reader));
            }

            return (IReadOnlyList<T>)results;
        }, ct).ConfigureAwait(false);
    }

    // ---- diagnostics ----

    /// <summary>
    ///     Describe this store's configuration for monitoring tools.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Built by hand rather than through <see cref="EventStoreUsage" />'s reflective constructor,
    ///         which walks the subject's properties and would dump the store's runtime handles into the
    ///         descriptor as if they were configuration.
    ///     </para>
    ///     <para>
    ///         <b>Building it by hand is why fisher#120 happened, and the shape of that bug is the reason
    ///         to be exhaustive here rather than tidy.</b> An unfilled slot on this object is not read as
    ///         "this store does not describe that" — it is read as <em>the store has none</em>. The
    ///         missing <see cref="EventStoreUsage.Subscriptions" /> list made
    ///         <c>projections list</c> answer "No projections in this store" for a store with twenty of
    ///         them, and <c>projections rebuild</c> match none of them, with nothing anywhere reporting a
    ///         gap. Every list below is populated for that reason, not because a consumer was known to
    ///         want it.
    ///     </para>
    /// </remarks>
    async Task<EventStoreUsage?> IEventStore.TryCreateUsage(CancellationToken token)
    {
        var usage = new EventStoreUsage
        {
            Subject = "Fisher.DocumentStore",
            SubjectUri = Database.Describe().DatabaseUri(),
            Version = GetType().Assembly.GetName().Version?.ToString()!,
            Database = DescribeDatabases()
        };

        usage.AddValue(nameof(EventGraph.StreamIdentity), EventGraph.StreamIdentity);
        usage.AddValue(nameof(EventGraph.AppendMode), EventGraph.AppendMode);
        usage.AddValue(nameof(Options.DatabaseSchemaName), Options.DatabaseSchemaName);
        usage.AddValue(nameof(Options.AutoCreateSchemaObjects), Options.AutoCreateSchemaObjects);

        // jasperfx#434 — surface the effective cap so a rebuild orchestrator can size itself off the
        // wire rather than guessing.
        usage.MaxConcurrentRebuildsPerDatabase = ResolveMaxConcurrentRebuilds();

        // Both event-type collections, because EventStoreUsage carries the registry twice and a
        // consumer is entitled to read either. Filling one alone is what polecat#411 was.
        foreach (var eventType in EventGraph.AllKnownEventTypes())
        {
            usage.Events.Add(new EventDescriptor(eventType.EventTypeName, TypeDescriptor.For(eventType.EventType)));

            usage.RegisteredEventTypes.Add(new EventTypeDescriptor(
                EventType: TypeDescriptor.For(eventType.EventType),
                Alias: eventType.EventTypeName,
                Description: null!));
        }

        foreach (var registration in EventGraph.TagTypes)
        {
            usage.TagTypes.Add(new TagTypeDescriptor
            {
                TagType = registration.TagType.FullName ?? registration.TagType.Name,
                SimpleType = registration.SimpleType.FullName ?? registration.SimpleType.Name,
                TableSuffix = registration.TableSuffix,
                AggregateType = registration.AggregateType?.FullName
            });

            usage.DcbTagTypes.Add(new DcbTagDescriptor(
                Name: registration.TagType.Name,
                SimpleType: registration.SimpleType.FullName ?? registration.SimpleType.Name,
                TagType: TypeDescriptor.For(registration.TagType),
                Description: null!));
        }

        // JasperFx/ProductSupport#3 — the two policies are separate because they differ: a rebuild
        // stops on an error a normal run would skip, and a console showing "view related dead
        // letters" for a store that halts instead offers a button that never returns anything.
        usage.ProjectionErrors = ErrorPolicyFor(Options.Projections.Errors);
        usage.ProjectionRebuildErrors = ErrorPolicyFor(Options.Projections.RebuildErrors);

        // jasperfx#475 — the four event columns are opt-in and read straight off the options. Every
        // stream facet is universal in Fisher, so those keep EventMetadataCapabilities' defaults.
        usage.EventMetadata = new EventMetadataCapabilities
        {
            StoreType = "Fisher",
            CorrelationId = Options.Events.EnableCorrelationId,
            CausationId = Options.Events.EnableCausationId,
            Headers = Options.Events.EnableHeaders,
            UserName = Options.Events.EnableUserName
        };

        usage.MaxEventSequence = await TryReadMaxEventSequenceAsync(token).ConfigureAwait(false);

        // fisher#120 — the line whose absence was the issue. Everything it fills is already built:
        // ProjectionGraph.Describe walks the registered projections and subscriptions, and Fisher's
        // two source types of its own (CompositeIProjectionSource, FlatTableProjection) implement
        // Describe. Nothing here is Fisher-specific, which is exactly why it was easy to leave out.
        Options.Projections.Describe(usage, this);

        return usage;
    }

    /// <summary>
    ///     The store's databases, as a monitoring tool's <see cref="DatabaseUsage" />.
    /// </summary>
    /// <remarks>
    ///     <b>Both usage descriptors hardcoded <see cref="DatabaseCardinality.Single" /> here</b>
    ///     (fisher#240), which is the fisher#120 shape exactly: a console does not read that as "this
    ///     store does not describe its tenancy", it reads it as <em>the store has one database</em>, and
    ///     renders a hundred tenants' files as one. <see cref="DatabaseUsage.Databases" /> is filled for
    ///     the same reason — it is where a tool finds the tenants, and an empty list claims there are
    ///     none. A single-database store reports itself in <c>MainDatabase</c> alone, as before, because
    ///     there listing it twice would be the noise.
    /// </remarks>
    private DatabaseUsage DescribeDatabases()
    {
        var usage = new DatabaseUsage
        {
            Cardinality = Tenancy.Cardinality,
            MainDatabase = Database.Describe()
        };

        if (Tenancy.Cardinality != DatabaseCardinality.Single)
        {
            usage.Databases.AddRange(Tenancy.AllDatabases().Select(x => x.Describe()));
        }

        return usage;
    }

    private static ProjectionErrorHandlingDescriptor ErrorPolicyFor(ErrorHandlingOptions options)
        => new()
        {
            SkipApplyErrors = options.SkipApplyErrors,
            SkipUnknownEvents = options.SkipUnknownEvents,
            SkipSerializationErrors = options.SkipSerializationErrors
        };

    /// <summary>
    ///     The highest sequence physically present in <c>fi_events</c>, or null when it cannot be read.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>On Fisher this is always equal to the high-water mark, and that is worth stating
    ///         rather than leaving a consumer to infer it.</b> The gap between the two is what
    ///         CritterWatch#150's second signal renders — a sequence issued but not yet safe to read —
    ///         and on Marten and Polecat it is real, because a server-side sequence or IDENTITY hands
    ///         out numbers outside the transaction. SQLite allows one writer per file and
    ///         <c>BEGIN IMMEDIATE</c> commits a transaction's sequences before the next writer
    ///         allocates any, so committed sequences are contiguous and the signal cannot fire here.
    ///         Reporting the number anyway is what lets a console see that, where leaving it null
    ///         renders as "n/a" and says nothing.
    ///     </para>
    ///     <para>
    ///         A failure is swallowed rather than propagated: this is a diagnostics call, and the most
    ///         likely reason the read fails is that the schema has not been created yet — which is
    ///         precisely when a monitoring tool is most likely to be pointed at the store. Failing the
    ///         whole description over one optional number would answer nothing at all.
    ///     </para>
    /// </remarks>
    private async Task<long?> TryReadMaxEventSequenceAsync(CancellationToken token)
    {
        try
        {
            return await Database.FetchHighestEventSequenceNumber(token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    ///     The read-only event store slice, for monitoring tools.
    /// </summary>
    /// <remarks>
    ///     Returns a type that owns session lifetime rather than a captured session's
    ///     <c>Events</c>, which is what Polecat hands back — see
    ///     <see cref="Events.FisherReadOnlyEventStore" /> for why an embedded single-file store cannot
    ///     afford that shape.
    /// </remarks>
    IReadOnlyEventStore IEventStore.OpenReadOnlyEventStore() => new Events.FisherReadOnlyEventStore(this);

    /// <summary>
    ///     The tooling-facing compaction entry point, which has no aggregate type parameter and so has
    ///     to resolve one from <c>fi_streams</c>.
    /// </summary>
    /// <remarks>
    ///     <b>Polecat throws here even though it implements the generic overload.</b> Fisher does not,
    ///     because the type it needs is already on the row: a stream started with an aggregate type
    ///     records it, and <c>StreamState.AggregateType</c> resolves it. A stream with none cannot be
    ///     compacted through this door and says so, naming the generic overload — which is a real
    ///     answer, unlike declining for every stream.
    /// </remarks>
    Task IEventStore.CompactStreamAsync(Guid streamId, CancellationToken token)
        => CompactByStreamStateAsync(streamId, token);

    /// <inheritdoc cref="IEventStore.CompactStreamAsync(Guid, CancellationToken)" />
    Task IEventStore.CompactStreamAsync(string streamKey, CancellationToken token)
        => CompactByStreamStateAsync(streamKey, token);

    [UnconditionalSuppressMessage("Trimming", "IL2060:MakeGenericMethod",
        Justification =
            "Closes CompactStreamAsync over the aggregate type named by the stream row. Aggregate types are preserved by projection registration on the caller side per the AOT publishing guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "See the trimming justification above.")]
    private async Task CompactByStreamStateAsync(object streamIdentity, CancellationToken token)
    {
        await using var session = LightweightSession();

        var state = streamIdentity is Guid streamId
            ? await session.Events.FetchStreamStateAsync(streamId, token).ConfigureAwait(false)
            : await session.Events.FetchStreamStateAsync((string)streamIdentity, token).ConfigureAwait(false);

        if (state is null)
        {
            throw new InvalidOperationException($"Stream '{streamIdentity}' does not exist.");
        }

        if (state.AggregateType is null)
        {
            throw new InvalidOperationException(
                $"Stream '{streamIdentity}' records no aggregate type, so there is nothing to compact it "
                + "into. Either the stream was started without one, or this deployment cannot resolve the "
                + "type it names. Use the generic CompactStreamAsync<T> overload to say which aggregate "
                + "to compact into.");
        }

        var method = typeof(Events.EventOperations)
            .GetMethods()
            .Single(x => x.Name == nameof(Events.EventOperations.CompactStreamAsync)
                         && x.GetParameters()[0].ParameterType == streamIdentity.GetType())
            .MakeGenericMethod(state.AggregateType);

        await ((Task)method.Invoke(session.Events, [streamIdentity, null])!).ConfigureAwait(false);
    }
}
