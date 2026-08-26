using Fisher.Events.Daemon;
using Fisher.Storage;
using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fisher;

/// <summary>
///     The storage seam the async projection daemon runs on — <see cref="IEventStore{TOperations,TQuerySession}" />
///     closed over Fisher's session pair.
/// </summary>
/// <remarks>
///     <para>
///         The daemon itself is JasperFx's: the coordinator, the subscription agents, the shard tracker,
///         the throttled and resilient loaders. What a store supplies is this interface plus the three
///         types in <c>Events/Daemon</c> — the high-water detector, the event loader and the projection
///         batch. Everything here is either a projection of configuration the store already holds or a
///         short piece of SQL against <c>fi_event_progression</c>.
///     </para>
///     <para>
///         Implemented explicitly, as the rest of the <see cref="IEventStore" /> surface is, so none of
///         it lands on the store's own public API. Application code never calls these; the daemon does.
///     </para>
///     <para>
///         <b>Every member taking an <see cref="IEventDatabase" /> resolves against it</b> (fisher#57).
///         For a long time they all ignored it, on the true-enough grounds that a Fisher store was one
///         file; under database-per-tenant (fisher#47) it is not, and ignoring it would read one tenant's
///         events and write every tenant's documents from them. <see cref="DatabaseFrom" /> is the single
///         place that resolution happens.
///     </para>
///     <para>
///         <b>A daemon is per database, and shard names did not have to change.</b>
///         <c>fi_event_progression</c> lives in each tenant's own file, so two tenants running the same
///         projection are two daemons writing the same shard name to two different tables. Making shard
///         identity (projection, tenant) — which fisher#57 expected — would have been a second key for a
///         distinction the file boundary already draws.
///     </para>
/// </remarks>
public partial class DocumentStore : IEventStore<IDocumentSession, IQuerySession>,
    ISubscriptionRunner<Subscriptions.ISubscription>
{
    // ---- configuration the daemon reads ----

    IEventRegistry IEventStore<IDocumentSession, IQuerySession>.Registry => EventGraph;

    string IEventStore<IDocumentSession, IQuerySession>.DefaultDatabaseName => Database.Identifier;

    ErrorHandlingOptions IEventStore<IDocumentSession, IQuerySession>.ContinuousErrors
        => Options.Projections.Errors;

    ErrorHandlingOptions IEventStore<IDocumentSession, IQuerySession>.RebuildErrors
        => Options.Projections.RebuildErrors;

    ErrorHandlingOptions IEventStore<IDocumentSession, IQuerySession>.ErrorHandlingOptions(ShardExecutionMode mode)
        => mode == ShardExecutionMode.Rebuild ? Options.Projections.RebuildErrors : Options.Projections.Errors;

    IReadOnlyList<AsyncShard<IDocumentSession, IQuerySession>>
        IEventStore<IDocumentSession, IQuerySession>.AllShards() => Options.Projections.AllShards();

    TimeProvider IEventStore<IDocumentSession, IQuerySession>.TimeProvider => EventGraph.TimeProvider;

    AutoCreate IEventStore<IDocumentSession, IQuerySession>.AutoCreateSchemaObjects
        => Options.AutoCreateSchemaObjects;

    /// <summary>
    ///     The identity type the daemon addresses a projected document by.
    /// </summary>
    /// <remarks>
    ///     The stream identity primitive rather than the aggregate's own id type — this answers "what
    ///     does a shard slice by", not "what does the generated dispatcher key on". See
    ///     <see cref="Storage.AggregateIdentity" /> for the other question, which has a different answer
    ///     for a strong-typed id.
    /// </remarks>
    Type IEventStore<IDocumentSession, IQuerySession>.IdentityTypeForProjectedType(Type aggregateType)
        => EventGraph.StreamIdentity == StreamIdentity.AsGuid ? typeof(Guid) : typeof(string);

    /// <summary>
    ///     Every database this store owns, so store-agnostic tooling can reach the
    ///     <see cref="IEventDatabase" /> reads without knowing Fisher's types.
    /// </summary>
    /// <remarks>
    ///     One under every tenancy but database-per-tenant (fisher#47), where it is one per tenant —
    ///     which is what makes a monitoring console show a hundred tenants' progress rather than one.
    /// </remarks>
    ValueTask<IReadOnlyList<IEventDatabase>> IEventStore.AllDatabases()
        => ValueTask.FromResult<IReadOnlyList<IEventDatabase>>(Tenancy.AllDatabases().Cast<IEventDatabase>().ToList());

    // ---- sessions and loaders ----

    /// <summary>
    ///     A session on the database a shard is running against.
    /// </summary>
    /// <remarks>
    ///     <b>The <see cref="IEventDatabase" /> parameter carries the answer now (fisher#57), where for
    ///     a long time it was ignored on the grounds that a Fisher store was one file.</b> Under
    ///     database-per-tenant it is not, and ignoring it here is precisely what would have read one
    ///     tenant's events and written every tenant's documents from them. Every member of this
    ///     interface taking one now resolves through <see cref="DatabaseFrom" />.
    /// </remarks>
    IDocumentSession IEventStore<IDocumentSession, IQuerySession>.OpenSession(IEventDatabase database)
        => OpenSessionOn(DatabaseFrom(database));

    /// <inheritdoc cref="IEventStore{TOperations,TQuerySession}.OpenSession(IEventDatabase)" />
    IDocumentSession IEventStore<IDocumentSession, IQuerySession>.OpenSession(IEventDatabase database,
        string tenantId) => OpenSessionOn(DatabaseFrom(database), tenantId);

    /// <summary>
    ///     The <see cref="FisherDatabase" /> behind an <see cref="IEventDatabase" /> the daemon hands
    ///     back.
    /// </summary>
    /// <remarks>
    ///     Always one of this store's own — the daemon only ever passes back a database it was given by
    ///     <see cref="IEventStore.AllDatabases" /> or by the detector. A null falls back to
    ///     <see cref="Database" /> rather than throwing, because JasperFx has paths that pass none and
    ///     the default database is the right answer for every store that is not database-per-tenant.
    /// </remarks>
    private FisherDatabase DatabaseFrom(IEventDatabase? database)
        => database as FisherDatabase ?? Database;

    /// <summary>
    ///     Build the loader one shard pages its events through.
    /// </summary>
    /// <remarks>
    ///     The bare <see cref="FisherEventLoader" /> is wrapped in JasperFx's
    ///     <see cref="ResilientEventLoader" />, which supplies both the retry around
    ///     <see cref="StoreOptions.ResiliencePipeline" /> and the load metrics the daemon reports. That
    ///     is why the loader itself does neither.
    /// </remarks>
    IEventLoader IEventStore<IDocumentSession, IQuerySession>.BuildEventLoader(IEventDatabase database,
        ILogger loggerFactory, EventFilterable filtering, AsyncOptions shardOptions)
    {
        var target = DatabaseFrom(database);

        return new ResilientEventLoader(Options.ResiliencePipeline,
            new FisherEventLoader(target, Options, filtering), target);
    }

    /// <summary>
    ///     Open one transaction's worth of projection work.
    /// </summary>
    /// <remarks>
    ///     The range's progress is recorded into the batch immediately, so it commits in the same
    ///     transaction as whatever the projection goes on to write. See
    ///     <see cref="FisherProjectionBatch" /> for why splitting those is not an option.
    /// </remarks>
    async ValueTask<IProjectionBatch<IDocumentSession, IQuerySession>>
        IEventStore<IDocumentSession, IQuerySession>.StartProjectionBatchAsync(EventRange range,
            IEventDatabase database, ShardExecutionMode mode, AsyncOptions projectionOptions,
            CancellationToken token)
    {
        var batch = new FisherProjectionBatch(this, EventGraph, DatabaseFrom(database));
        await batch.RecordProgress(range).ConfigureAwait(false);

        return batch;
    }

    // ---- progression bookkeeping ----

    /// <summary>
    ///     Reset a subscription's progress to a floor, or clear it outright.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A null or zero floor deletes every progression row the subscription owns rather than
    ///         writing a zero, because a missing row and a row at zero mean the same thing to the daemon,
    ///         and the deletion also sweeps up shard rows the current configuration no longer names.
    ///     </para>
    ///     <para>
    ///         A subscription owns one row per shard (<c>Name:ShardKey</c>), so the delete matches on a
    ///         <c>like</c> prefix — the shape Marten and Polecat both use. Note that <c>_</c> is a
    ///         single-character wildcard to SQL's <c>LIKE</c>: a subscription named <c>tally</c> and one
    ///         named <c>tallyX</c> are distinct, but a projection name containing <c>_</c> would match
    ///         loosely. That is the same trap the document cleaner sidesteps by filtering in C#; here the
    ///         set being matched is one small table of the store's own shard names rather than every
    ///         table in the file.
    ///     </para>
    /// </remarks>
    Task IEventStore<IDocumentSession, IQuerySession>.RewindSubscriptionProgressAsync(IEventDatabase database,
        string subscriptionName, CancellationToken token, long? sequenceFloor)
        => sequenceFloor is null or 0
            ? DeleteProgressionRowsAsync(DatabaseFrom(database), subscriptionName, token)
            : WriteProgressionRowAsync(DatabaseFrom(database), subscriptionName, sequenceFloor.Value, token);

    Task IEventStore<IDocumentSession, IQuerySession>.RewindAgentProgressAsync(IEventDatabase database,
        string shardName, CancellationToken token, long sequenceFloor)
        => WriteProgressionRowAsync(DatabaseFrom(database), shardName, sequenceFloor, token);

    Task IEventStore<IDocumentSession, IQuerySession>.DeleteProjectionProgressAsync(IEventDatabase database,
        string subscriptionName, CancellationToken token)
        => DeleteProgressionRowsAsync(DatabaseFrom(database), subscriptionName, token);

    /// <summary>
    ///     Drop a projection's progress <em>and</em> the documents it published, which is what a rebuild
    ///     starts from.
    /// </summary>
    /// <remarks>
    ///     Both halves run in one transaction. A teardown that deleted the progression and then failed
    ///     before clearing the documents would leave a projection that replays from zero on top of rows
    ///     it already wrote — the exact double-application a rebuild exists to avoid.
    /// </remarks>
    async Task IEventStore<IDocumentSession, IQuerySession>.TeardownExistingProjectionStateAsync(
        IEventDatabase database, string subscriptionName, CancellationToken token)
    {
        var tables = PublishedTableNamesFor(subscriptionName);
        var target = DatabaseFrom(database);

        // Outside the resilience pipeline below, so a retried SQLITE_BUSY does not warn twice about
        // one rebuild -- the same reason every other once-per-unit-of-work step sits outside it.
        WarnAboutTablesSharedWithAnotherProjection(subscriptionName, tables);

        await Options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await target.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection
                .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"delete from {EventGraph.ProgressionTableName} where name like @name";
                command.Parameters.AddWithValue("@name", subscriptionName + "%");
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // A projection's document table is created on first write, so on a first-ever rebuild it may
            // not exist yet. The existence test has to happen here rather than as a predicate on the
            // delete: SQLite resolves table names when it prepares the statement, so a `where exists
            // (select ... from sqlite_master ...)` guard would still fail before it ever ran.
            var existing = await ReadExistingTableNamesAsync(connection, transaction, ct).ConfigureAwait(false);

            foreach (var table in tables.Where(existing.Contains))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"delete from {Weasel.Sqlite.SchemaUtils.QuoteName(table)}";
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            Diagnostics.DaemonTrace.Record("teardown.commit", subscriptionName, tables.Count);

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }, token).ConfigureAwait(false);

        Diagnostics.DaemonTrace.Record("teardown.done", subscriptionName);
    }

    /// <summary>
    ///     Warn when a rebuild is about to clear a table another registered projection also writes to
    ///     (fisher#122).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Teardown deletes <em>the whole of</em> every table the named projection publishes into,
    ///         and then the rebuild replays only that projection. So when two projections publish the
    ///         same document type they share one <c>fi_doc_*</c> table, and rebuilding either wipes
    ///         both — the rebuilt one is correct and the other's rows are simply gone until it too is
    ///         rebuilt. Nothing errors, the rebuild reports success, and the damage is to a read model
    ///         nobody is looking at.
    ///     </para>
    ///     <para>
    ///         <b>This is a warning and not a refusal, deliberately.</b> Sharing a published type is
    ///         legal and sometimes intended — it is only rebuilding that costs anything, and a store
    ///         that never rebuilds runs this configuration perfectly well. Refusing would break it;
    ///         saying nothing is what cost the reporter a debugging session.
    ///     </para>
    ///     <para>
    ///         <b>The message sends the operator to a rewind, not to a second rebuild, and that
    ///         correction came out of writing the test.</b> fisher#122 and its reporter both assumed
    ///         "rebuild them together" was the remedy. There is no such operation — every
    ///         <c>RebuildProjectionAsync</c> overload names one projection — so "together" means one
    ///         after the other, and the second teardown clears the shared table again and discards what
    ///         the first rebuild wrote. Only the projection rebuilt last keeps its rows.
    ///         <c>RewindSubscriptionAsync</c> is what actually works, because it replays onto the rows
    ///         that are there instead of clearing first.
    ///         <c>shared_published_table_rebuild.rebuilding_each_in_turn_still_leaves_one_of_them_empty</c>
    ///         pins the wrong advice as wrong.
    ///     </para>
    ///     <para>
    ///         <b>And it is at rebuild time rather than at registration.</b> A registration-time warning
    ///         fires on every boot about something that only matters when somebody rebuilds. Here the
    ///         operator is present, the information is actionable, and the other projections can be
    ///         named.
    ///     </para>
    ///     <para>
    ///         <b>Marten behaves identically</b>, so this is not a Fisher divergence and the semantics
    ///         are unchanged. What is added is that the store now says so.
    ///     </para>
    /// </remarks>
    private void WarnAboutTablesSharedWithAnotherProjection(string subscriptionName, IReadOnlyList<string> tables)
    {
        if (tables.Count == 0)
        {
            return;
        }

        var mine = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);

        // Grouped by table rather than by projection, because the table is the thing being cleared and
        // one projection may share more than one of them.
        var shared = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var other in Options.Projections.All)
        {
            if (other.Name == subscriptionName)
            {
                continue;
            }

            foreach (var table in PublishedTableNamesFor(other).Where(mine.Contains))
            {
                if (!shared.TryGetValue(table, out var names))
                {
                    shared[table] = names = new SortedSet<string>(StringComparer.Ordinal);
                }

                names.Add(other.Name);
            }
        }

        if (shared.Count == 0)
        {
            return;
        }

        var detail = string.Join("; ",
            shared.Select(pair => $"{pair.Key} is also published by {string.Join(", ", pair.Value)}"));

        _daemonLogger.LogWarning(
            "Rebuilding projection {Projection} will clear {TableCount} table(s) that another registered "
            + "projection also publishes into: {Detail}. Teardown deletes the whole table and the rebuild "
            + "replays only {Projection}, so the other projection(s) will be left with no rows. Rebuilding "
            + "them afterwards does NOT fix it -- each rebuild clears the shared table again, so only the "
            + "one rebuilt last keeps its rows. Rewind them instead: "
            + "daemon.RewindSubscriptionAsync(name, token, sequenceFloor: 0) replays a projection onto the "
            + "rows that are there rather than clearing first.",
            subscriptionName, shared.Count, detail, subscriptionName);
    }

    /// <summary>
    ///     The unquoted tables a projection publishes into.
    /// </summary>
    /// <remarks>
    ///     Normally that means one document table per published type the schema has mapped. A
    ///     flat-table projection publishes no types at all — its rows are not documents — so it names
    ///     its table directly through <c>IPublishesTables</c>; without that a rebuild would replay onto
    ///     the rows the previous run left behind.
    /// </remarks>
    private IReadOnlyList<string> PublishedTableNamesFor(string subscriptionName)
        => Options.Projections.TryFindProjection(subscriptionName, out var source)
            ? PublishedTableNamesFor(source!)
            : [];

    private IReadOnlyList<string> PublishedTableNamesFor(
        IProjectionSource<IDocumentSession, IQuerySession> source)
    {
        var tables = source.PublishedTypes()
            .Where(Options.Schema.HasMappingFor)
            .Select(x => Options.Schema.MappingFor(x).TableName.Name)
            .ToList();

        // A type stored outside Fisher (fisher#50) is deliberately not mapped, so the sweep above
        // cannot see it — the same gap IPublishesTables closes for a flat table, reached from the other
        // direction. Without this a rebuild replays onto the rows the previous run left behind.
        tables.AddRange(source.PublishedTypes()
            .Select(Options.Projections.StorageProviders.TableNameFor)
            .Where(x => x is not null)
            .Select(x => x!));

        if (source is Projections.Flattened.IPublishesTables publisher)
        {
            tables.AddRange(publisher.PublishedTableNames());
        }

        return tables;
    }

    private static async Task<HashSet<string>> ReadExistingTableNamesAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select name from sqlite_master where type = 'table'";

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private async Task DeleteProgressionRowsAsync(FisherDatabase database, string subscriptionName,
        CancellationToken token)
    {
        await Options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await database.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"delete from {EventGraph.ProgressionTableName} where name like @name";
            command.Parameters.AddWithValue("@name", subscriptionName + "%");
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }

    private async Task WriteProgressionRowAsync(FisherDatabase database, string shardIdentity, long sequence,
        CancellationToken token)
    {
        await Options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await database.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                                   insert into {EventGraph.ProgressionTableName} (name, last_seq_id, last_updated)
                                   values (@name, @seq, {SqliteTimestamp.NowExpression})
                                   on conflict (name) do update
                                     set last_seq_id = excluded.last_seq_id,
                                         last_updated = excluded.last_updated;
                                   """;
            command.Parameters.AddWithValue("@name", shardIdentity);
            command.Parameters.AddWithValue("@seq", sequence);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }

    // ---- building the daemon ----

    /// <summary>
    ///     Build a projection daemon over this store's database.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <paramref name="tenantIdOrDatabaseIdentifier" /> is accepted and ignored under every
    ///         tenancy but database-per-tenant, where there is one database and nothing to resolve
    ///         against. <b>Under database-per-tenant it names which tenant's file this daemon projects</b>
    ///         (fisher#57), and defaults to the default tenant's — so a store spanning several tenants
    ///         wants <see cref="BuildProjectionDaemonsAsync" />, or <c>AddAsyncDaemon</c>, which hosts one
    ///         per database.
    ///     </para>
    ///     <para>
    ///         <b>A daemon is per database, and shard names did not have to change</b>, which fisher#57
    ///         expected they would. <c>fi_event_progression</c> lives in each tenant's own file, so every
    ///         database already has its own high-water mark and its own progress row per shard — two
    ///         tenants running the same projection are two daemons writing the same shard name to two
    ///         different tables, and nothing collides. Making shard identity (projection, tenant) would
    ///         have been a second key for a distinction the file boundary already draws.
    ///     </para>
    ///     <para>
    ///         <strong>WAL is checked here rather than assumed.</strong> The daemon reads while
    ///         application sessions write, which under SQLite's default rollback journal serializes the
    ///         two — the daemon would block behind every writer and every writer behind the daemon. WAL
    ///         is on by default through <see cref="Weasel.Sqlite.SqlitePragmaSettings.Default" />, but a
    ///         consumer replacing <see cref="StoreOptions.PragmaSettings" /> can turn it off, and the
    ///         resulting stall looks like a slow projection rather than a misconfiguration. Warning at
    ///         start is where a human is actually looking.
    ///     </para>
    /// </remarks>
    public async ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(
        string? tenantIdOrDatabaseIdentifier = null, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        var target = tenantIdOrDatabaseIdentifier is null
            ? Database
            : Tenancy.DatabaseFor(tenantIdOrDatabaseIdentifier);

        return await BuildDaemonForAsync(target, logger).ConfigureAwait(false);
    }

    /// <summary>
    ///     One daemon per database this store spans (fisher#57).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         One under every tenancy but database-per-tenant, where it is one per tenant file. This is
    ///         what <c>AddAsyncDaemon</c> hosts, and what a consumer building a daemon by hand should
    ///         reach for if the store is database-per-tenant — the single-daemon overload projects one
    ///         file and says nothing about the others.
    ///     </para>
    ///     <para>
    ///         <b>N daemons over N files do not contend</b>, which is the same property that makes stage
    ///         1 a performance feature: each writes to its own file and therefore holds its own write
    ///         lock. Under conjoined tenancy the same N projections would queue behind one.
    ///     </para>
    /// </remarks>
    public async ValueTask<IReadOnlyList<IProjectionDaemon>> BuildProjectionDaemonsAsync(ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        // Tenants that appeared since the store was built are found here rather than assumed absent
        // (fisher#58); a no-op under every other tenancy.
        await RefreshTenantsAsync().ConfigureAwait(false);

        var daemons = new List<IProjectionDaemon>();

        foreach (var database in Tenancy.AllDatabases())
        {
            daemons.Add(await BuildDaemonForAsync(database, logger).ConfigureAwait(false));
        }

        return daemons;
    }

    /// <summary>
    ///     The logger the most recently built daemon was given, so a rebuild has somewhere to warn.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Stashed rather than passed, because the seam has no parameter for it.</b>
    ///         <c>TeardownExistingProjectionStateAsync</c> is on <see cref="IEventStore{T,TQ}" />, which
    ///         is JasperFx's interface and takes an <see cref="IEventDatabase" /> and a name. A rebuild
    ///         only ever reaches teardown through a daemon, and a daemon is only ever built through
    ///         <see cref="BuildDaemonForAsync" />, so this is set on every path that can reach the
    ///         warning.
    ///     </para>
    ///     <para>
    ///         Under database-per-tenant several daemons are built and the last logger wins. That is
    ///         harmless here: <c>AddAsyncDaemon</c> hands every one of them the same logger, and a
    ///         consumer building daemons by hand with different ones still gets the warning — on one of
    ///         its own loggers rather than on none.
    ///     </para>
    /// </remarks>
    private volatile ILogger _daemonLogger = NullLogger.Instance;

    private async ValueTask<IProjectionDaemon> BuildDaemonForAsync(FisherDatabase database, ILogger logger)
    {
        _daemonLogger = logger;

        // Per database, because the journal mode is a property of the file. A store with one tenant
        // configured without WAL is exactly the case the warning exists for, and warning once for the
        // store would miss it.
        WarnIfJournalModeIsNotWal(database, logger);

        await database.EnsureStorageExistsAsync(typeof(IEvent), CancellationToken.None).ConfigureAwait(false);

        return new FisherProjectionDaemon(this, database, logger,
            new FisherHighWaterDetector(database, EventGraph));
    }

    ValueTask<IProjectionDaemon> IEventStore.BuildProjectionDaemonAsync(DatabaseId id)
        => BuildProjectionDaemonAsync(id.Name);

    private void WarnIfJournalModeIsNotWal(FisherDatabase database, ILogger logger)
    {
        var journalMode = Options.PragmaSettings.JournalMode;

        if (journalMode != Weasel.Sqlite.JournalMode.WAL)
        {
            logger.LogWarning(
                "The Fisher async projection daemon is starting against database {Database} whose journal "
                + "mode is {JournalMode} rather than WAL. Without WAL, SQLite blocks readers while a writer "
                + "holds the database, so the daemon and every application session will serialize against "
                + "each other. Set StoreOptions.PragmaSettings.JournalMode to Wal.",
                database.Identifier, journalMode);
        }
    }

    /// <summary>
    ///     Drive one subscription over one range of events (fisher#21).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Resolved by <c>SubscriptionExecution&lt;T&gt;</c> through a soft <c>storage as
    ///         ISubscriptionRunner&lt;T&gt;</c> cast, which is why a store without this fails when a
    ///         subscription is registered rather than at compile time — and why subscriptions read as
    ///         absent rather than broken until now.
    ///     </para>
    ///     <para>
    ///         <b>The subscription's session is the batch's, so its writes commit in the same
    ///         transaction as the progression row.</b> That is what makes a subscription persisting
    ///         through Fisher exactly-once: it cannot advance past a range whose writes were rolled
    ///         back, and it cannot commit writes for a range it will replay. Work outside this
    ///         database is at-least-once and nothing can change that.
    ///     </para>
    ///     <para>
    ///         <b>The post-commit listener runs after <c>ExecuteAsync</c> returns, deliberately
    ///         outside the resilience pipeline.</b> A retried <c>SQLITE_BUSY</c> re-executes the whole
    ///         batch delegate, so a listener invoked inside it would fire twice for a transaction that
    ///         had already committed — the same property fisher#4 established for the message outbox
    ///         and fisher#12 for the batch's own input.
    ///     </para>
    /// </remarks>
    async Task ISubscriptionRunner<Subscriptions.ISubscription>.ExecuteAsync(
        Subscriptions.ISubscription subscription, IEventDatabase database, EventRange range,
        ShardExecutionMode mode, CancellationToken token)
    {
        await using var batch = new FisherProjectionBatch(this, EventGraph, DatabaseFrom(database));

        await batch.RecordProgress(range).ConfigureAwait(false);

        // SessionForTenant both opens the session and enrols it in the batch, so the subscription's
        // own writes flush into the batch's transaction alongside the progression row.
        var session = batch.SessionForTenant(JasperFx.StorageConstants.DefaultTenantId);

        var listener = await subscription
            .ProcessEventsAsync(range, range.Agent, session, token).ConfigureAwait(false);

        await batch.ExecuteAsync(token).ConfigureAwait(false);

        if (listener is not null and not NullDaemonChangeListener)
        {
            await listener.AfterCommitAsync(token).ConfigureAwait(false);
        }
    }
}
