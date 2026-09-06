using Fisher.Events;
using Fisher.Projections;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.ComplianceTests;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Tags;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fisher.Tests.Compliance;

/// <summary>
///     Fisher's implementation of the cross-store event sourcing compliance seam, closing it over
///     Fisher's <c>IDocumentSession</c> / <c>IQuerySession</c> pair.
/// </summary>
/// <remarks>
///     <para>
///         Members Fisher cannot honour yet throw rather than returning something plausible. The
///         fixture is abstract in every member, so a suite that never calls one is unaffected — which
///         is what lets Fisher enroll suite by suite instead of waiting for the whole surface.
///     </para>
///     <para>
///         Isolation is per fixture instance, and xUnit builds one per test: each gets its own
///         throwaway SQLite file. That is the SQLite analogue of Polecat's per-test schema name —
///         <c>ComplianceStoreConfig.SchemaName</c> still flows through to
///         <c>StoreOptions.DatabaseSchemaName</c>, where it folds into the table prefix.
///     </para>
/// </remarks>
public class FisherComplianceFixture : EventStoreComplianceFixture<IDocumentSession, IQuerySession>
{
    private TemporaryDatabase? _database;
    private DocumentStore? _store;

    private DocumentStore Store => _store
        ?? throw new InvalidOperationException("The compliance store has not been configured yet.");

    protected override async Task BuildStoreAsync(ComplianceStoreConfig config)
    {
        await DisposeStoreAsync().ConfigureAwait(false);

        _database = TemporaryDatabase.Create(config.SchemaName ?? "compliance");

        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.DatabaseSchemaName = (config.SchemaName ?? "compliance").ToLowerInvariant();

            if (config.StreamIdentity.HasValue)
            {
                options.Events.StreamIdentity = config.StreamIdentity.Value;
            }

            if (config.EnableCorrelationTracking)
            {
                options.Events.EnableCorrelationId = true;
                options.Events.EnableCausationId = true;
            }

            // Opt-in like correlation, and a schema decision the same way: user_name only exists on
            // fi_events when this was on when the schema was built (jasperfx#737 — the event query
            // suite filters on the column).
            if (config.EnableUserNameTracking)
            {
                options.Events.EnableUserName = true;
            }

            if (config.EnableHeaders)
            {
                options.Events.EnableHeaders = true;
            }

            // Conjoined event tenancy is a schema decision, not a runtime one — StreamsTable and
            // EventsTable read TenancyStyle when they build their columns and their primary key — so
            // it has to be set before ApplyAllConfiguredChangesToDatabaseAsync below.
            if (config.ConjoinedEventTenancy)
            {
                options.Events.TenancyStyle = JasperFx.MultiTenancy.TenancyStyle.Conjoined;
            }

            if (config.MaxConcurrentRebuildsPerDatabase.HasValue)
            {
                options.DaemonSettings.MaxConcurrentRebuildsPerDatabase =
                    config.MaxConcurrentRebuildsPerDatabase.Value;
            }

            // The suite describes this as "folded into the connection string by the fixture", which is
            // what Marten and Polecat do with Npgsql's / SqlClient's Max Pool Size keyword.
            // Microsoft.Data.Sqlite has no such keyword, so Fisher carries the ceiling as a store
            // option — see StoreOptions.MaxPoolSize.
            if (config.MaxPoolSize.HasValue)
            {
                options.MaxPoolSize = config.MaxPoolSize.Value;
            }

            config.ApplyTo(new FisherComplianceRegistrar(options));
        });

        // Fisher applies schema changes explicitly, as Polecat does, rather than lazily on first use.
        await Store.ApplyAllConfiguredChangesToDatabaseAsync(Cancellation).ConfigureAwait(false);
    }

    public override IDocumentSession OpenSession() => Store.LightweightSession();

    public override Task SaveChangesAsync(IDocumentSession session, CancellationToken token)
        => session.SaveChangesAsync(token);

    public override IEventStoreOperations EventsFor(IDocumentSession session) => session.Events;

    public override IEventRegistry Registry => Store.Options.EventGraph;

    public override IEventStore EventStore => Store;

    /// <summary>
    ///     Adapts Fisher's own <see cref="Fisher.Batching.IBatchedQuery" /> to the shared shape. The
    ///     methods already match one for one — only the accessor path differs between stores, which is
    ///     what <see cref="IComplianceBatch" /> exists to bridge.
    /// </summary>
    public override IComplianceBatch CreateBatch(IQuerySession session)
        => new FisherComplianceBatch(((IDocumentSession)session).Events.CreateBatchQuery());

    private sealed class FisherComplianceBatch : IComplianceBatch
    {
        private readonly Fisher.Batching.IBatchedQuery _batch;

        internal FisherComplianceBatch(Fisher.Batching.IBatchedQuery batch) => _batch = batch;

        public Task<bool> EventsExist(EventTagQuery query) => _batch.EventsExist(query);

        public Task<IEventBoundary<T>> FetchForWritingByTags<T>(EventTagQuery query) where T : class
            => _batch.FetchForWritingByTags<T>(query);

        public Task Execute(CancellationToken token = default) => _batch.Execute(token);
    }

    public override IEnumerable<Type> AllAggregateTypes() => Store.Options.Projections.AllAggregateTypes();

    public override Task<T?> LoadDocumentAsync<T>(IQuerySession session, object id, CancellationToken token)
        where T : class
        => id switch
        {
            Guid guid => session.LoadAsync<T>(guid, token),
            string key => session.LoadAsync<T>(key, token),
            int number => session.LoadAsync<T>(number, token),
            long number => session.LoadAsync<T>(number, token),
            // A strong-typed id wrapper: close the two-parameter overload over it. Reflection because
            // the suite hands the id over as object, and its runtime type is the only thing naming TId.
            _ => (Task<T?>)typeof(IQuerySession)
                .GetMethod(nameof(IQuerySession.LoadAsync), 2, [Type.MakeGenericMethodParameter(1), typeof(CancellationToken)])!
                .MakeGenericMethod(typeof(T), id.GetType())
                .Invoke(session, [id, token])!
        };

    public override void StoreDocument<T>(IDocumentSession session, T document) => session.Store(document);

    /// <summary>
    ///     Run a batch data-masking operation against events already stored.
    /// </summary>
    /// <remarks>
    ///     <see cref="JasperFx.Events.Protected.IEventDataMasking" /> is shared, but the entry point that
    ///     hands one out is not — every store spells it on its own <c>Advanced</c> surface, and those
    ///     share no interface. Fisher's is <see cref="AdvancedOperations.ApplyEventDataMaskingAsync" />,
    ///     and the signature already matches the seam member one for one.
    /// </remarks>
    public override Task ApplyEventDataMaskingAsync(
        Action<JasperFx.Events.Protected.IEventDataMasking> configure, CancellationToken token)
        => Store.Advanced.ApplyEventDataMaskingAsync(configure, token);

    public override string? CorrelationIdFor(IDocumentSession session) => AsFisherSession(session).CorrelationId;

    public override string? CausationIdFor(IDocumentSession session) => AsFisherSession(session).CausationId;

    public override void SetCorrelationId(IDocumentSession session, string? correlationId)
        => AsFisherSession(session).CorrelationId = correlationId;

    /// <summary>
    ///     Fisher spells the seam's "user name (last-modified-by)" as
    ///     <see cref="IDocumentOperations.CurrentUserName" /> — one value stamped onto appended events
    ///     when <c>EnableUserName</c> is on and onto documents whose type enabled the column, the same
    ///     one-source-two-destinations shape as the correlation pair above it.
    /// </summary>
    public override void SetUserName(IDocumentSession session, string? userName)
        => session.CurrentUserName = userName;

    /// <summary>
    ///     Wipe event data between tests.
    /// </summary>
    /// <remarks>
    ///     Called before every test by the suite base, so it cannot throw the way the unsupported
    ///     members below do — hence the null guard rather than the <see cref="Store" /> accessor.
    /// </remarks>
    public override async Task CleanEventDataAsync()
    {
        if (_store is null)
        {
            return;
        }

        await Store.Advanced.Clean.DeleteAllEventDataAsync(Cancellation).ConfigureAwait(false);
    }

    /// <summary>
    ///     Fisher derives live aggregators automatically from self-aggregating types, as Polecat does;
    ///     there is no explicit registration call to make.
    /// </summary>
    public override bool SupportsLiveAggregationRegistration => false;

    public override bool SupportsAsyncDaemon => true;

    private IProjectionDaemon? _daemon;

    /// <summary>
    ///     Start the async projection daemon for the suite's store.
    /// </summary>
    /// <remarks>
    ///     One daemon per fixture, kept so <see cref="DisposeAsync" /> can stop it — a suite that started
    ///     a second one would leave the first still polling the same file, which on SQLite means two
    ///     writers contending for one lock and a rebuild racing catch-up.
    /// </remarks>
    public override async Task<IProjectionDaemon> StartDaemonAsync()
    {
        if (_daemon is not null)
        {
            return _daemon;
        }

        _daemon = await Store.BuildProjectionDaemonAsync().ConfigureAwait(false);
        await _daemon.StartAllAsync().ConfigureAwait(false);

        return _daemon;
    }

    public override Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout)
        => Store.Database.WaitForNonStaleProjectionDataAsync(timeout);

    /// <summary>
    ///     Read every row of a flat-table projection's table.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The suite hands over the unqualified name the projection was declared with, so the
    ///         schema resolution is the same fold <see cref="Fisher.Storage.FisherTableNaming.UserTableName" />
    ///         applies when the projection creates the table — SQLite has no schemas, so "resolving the
    ///         schema" means prepending it to the name.
    ///     </para>
    ///     <para>
    ///         Values come back through <see cref="System.Data.Common.DbDataReader.GetValue" />, which
    ///         yields exactly the five SQLite storage classes — so an INTEGER column arrives as a
    ///         <c>long</c> and the suite's <c>Convert.ToInt32</c> handles the width, as it says it will.
    ///     </para>
    ///     <para>
    ///         <strong>The one conversion is Guid.</strong> SQL Server has <c>uniqueidentifier</c> and
    ///         PostgreSQL has <c>uuid</c>, so on both siblings the provider hands the suite a
    ///         <see cref="Guid" /> and its <c>Equals(row["id"], streamId)</c> matches. SQLite has no such
    ///         type: Fisher stores a Guid as lowercase canonical text everywhere (the
    ///         <c>SqliteGuidIdentification</c> rule), so something has to convert on the way out, and
    ///         doing it here is the same explicit <c>Guid.Parse</c> <c>FisherEventsRowReader</c> does.
    ///         Matching on the canonical rendering rather than <c>Guid.TryParse</c> alone is what keeps
    ///         it from claiming an ordinary string column that merely happens to hold Guid-shaped text
    ///         in some other casing or format.
    ///     </para>
    /// </remarks>
    public override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryTableAsync(
        string tableName, CancellationToken token)
    {
        var physical = Fisher.Storage.FisherTableNaming.UserTableName(
            Store.Options.DatabaseSchemaName, tableName);

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_database!.ConnectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"select * from {Weasel.Sqlite.SchemaUtils.QuoteName(physical)}";

        var rows = new List<IReadOnlyDictionary<string, object?>>();

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i, token).ConfigureAwait(false)
                    ? null
                    : AsClrValue(reader.GetValue(i));
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <inheritdoc cref="QueryTableAsync" />
    private static object AsClrValue(object raw)
        => raw is string text && Guid.TryParse(text, out var guid) && guid.ToString() == text
            ? guid
            : raw;

    // ------------------------------------------------------------------
    // 2.64.0 (wave 13) — fisher#184
    // ------------------------------------------------------------------

    /// <summary>
    ///     jasperfx#764 — Fisher maintains the natural key lookup and resolves the shared
    ///     <c>FetchForWriting</c> / <c>FetchForExclusiveWriting</c> / <c>FetchLatest</c> triple through
    ///     it (fisher#40).
    /// </summary>
    /// <remarks>
    ///     The gate guards no seam member: the whole natural key surface is already shared, and what
    ///     varies between stores is only whether the storage half exists. Fisher's does, so this is
    ///     true and the suite is a check on it rather than a specification to build against.
    /// </remarks>
    public override bool SupportsNaturalKeys => true;

    /// <summary>
    ///     Fisher has <c>UnArchiveStream</c> on its own event operations, so the archiving suite's
    ///     unarchive facts run rather than skipping.
    /// </summary>
    /// <remarks>
    ///     The operation is not on the shared <see cref="IEventStoreOperations" /> and cannot be:
    ///     Polecat declares it on its own surface and Marten has no equivalent at all, which is why
    ///     the suite reaches it through this pair rather than through the contract.
    /// </remarks>
    public override bool SupportsUnarchiveStream => true;

    /// <inheritdoc cref="SupportsUnarchiveStream" />
    public override void UnArchiveStream(IDocumentSession session, object streamIdentity)
    {
        switch (streamIdentity)
        {
            case Guid streamId:
                session.Events.UnArchiveStream(streamId);
                break;
            case string streamKey:
                session.Events.UnArchiveStream(streamKey);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(streamIdentity),
                    $"Fisher has no stream identity of type {streamIdentity.GetType().FullName}");
        }
    }

    /// <summary>
    ///     jasperfx#763 — Fisher routes a projection's published side effects through
    ///     <c>StoreOptions.Events.MessageOutbox</c>, whose default drops every message.
    /// </summary>
    /// <remarks>
    ///     <c>NulloMessageOutbox</c> being the intended end state rather than a placeholder (fisher#8,
    ///     closed wontfix) is exactly why the suite has to supply its own recorder: every behavioural
    ///     fact about side effects is vacuously true of a store that dropped them on the floor.
    /// </remarks>
    public override bool SupportsMessageOutbox => true;

    /// <summary>
    ///     The before-commit probe reads committed state over a second connection while the first
    ///     session's write transaction is still open, which on SQLite in WAL mode answers immediately
    ///     rather than blocking.
    /// </summary>
    /// <remarks>
    ///     Safe rather than merely believed safe: this is the same probe fisher#4 settled the commit
    ///     hooks' visibility semantics with, and Fisher's own outbox tests have run it over a separate
    ///     connection since. WAL is on by default through <c>SqlitePragmaSettings.Default</c>, which is
    ///     what lets a reader see the last committed snapshot while a writer holds the file's one write
    ///     lock. Without it a probe that blocked until the commit would deadlock against the hook
    ///     holding the commit open — the hazard the flag exists to let a store decline.
    /// </remarks>
    public override bool SupportsCommitVisibilityProbe => true;

    /// <summary>
    ///     jasperfx#768 — the registrar replays <c>ComplianceSubscription.IncludedEventTypes</c> onto
    ///     Fisher's own subscription options, so a declared allow list reaches the daemon.
    /// </summary>
    /// <remarks>
    ///     The filter has to survive a hop nothing shared can make for it: <c>Projections.Subscribe</c>
    ///     wraps a bare <c>ISubscription</c> in <c>SubscriptionWrapper</c> and the daemon reads filters
    ///     off the <em>wrapper</em>. See <see cref="FisherComplianceRegistrar.Subscribe" />.
    /// </remarks>
    public override bool SupportsSubscriptionEventFilters => true;

    /// <summary>
    ///     fisher#42 — Fisher subclasses JasperFx's projection scenario harness and exposes it on
    ///     <c>Advanced.EventProjectionScenarioAsync</c>.
    /// </summary>
    public override bool SupportsProjectionScenario => true;

    /// <summary>
    ///     Forwards to Fisher's own documented entry point rather than re-implementing the three lines
    ///     behind it.
    /// </summary>
    /// <remarks>
    ///     The suite's remarks make the reason explicit and it is worth repeating here: inlining
    ///     construct-configure-execute would pass the whole suite while the store's advertised entry
    ///     point was missing or wired to the wrong store. The route is under test as much as the
    ///     harness is.
    /// </remarks>
    public override Task RunProjectionScenarioAsync(
        Action<JasperFx.Events.TestSupport.ProjectionScenario<IDocumentSession, IQuerySession>> configure,
        CancellationToken token)
        => Store.Advanced.EventProjectionScenarioAsync(scenario => configure(scenario), token);

    /// <summary>
    ///     Constructs a scenario without running it, for the one fact the run entry point structurally
    ///     cannot reach — that a scenario's steps are consumed by its first run.
    /// </summary>
    /// <remarks>
    ///     <see cref="Fisher.Events.TestSupport.ProjectionScenario" />'s constructor is internal, since a
    ///     scenario only means anything against a store; <c>Fisher.Tests</c> has
    ///     <c>InternalsVisibleTo</c>, so the fixture can reach it where an application could not.
    /// </remarks>
    public override JasperFx.Events.TestSupport.ProjectionScenario<IDocumentSession, IQuerySession>
        CreateProjectionScenario() => new Fisher.Events.TestSupport.ProjectionScenario(Store);

    /// <summary>
    ///     fisher#37 / polecat#370 — Fisher ships <see cref="Fisher.Batching.FetchStreamStatePlan" /> and
    ///     <see cref="Fisher.Batching.FetchStreamPlan" />, each implementing both the standalone and the
    ///     batched plan interface.
    /// </summary>
    public override bool SupportsStreamQueryPlans => true;

    /// <inheritdoc cref="SupportsStreamQueryPlans" />
    public override Task<StreamState?> FetchStreamStateByPlanAsync(
        IQuerySession session, object streamIdentity, bool batched, CancellationToken token)
    {
        var plan = streamIdentity switch
        {
            Guid streamId => new Fisher.Batching.FetchStreamStatePlan(streamId),
            string streamKey => new Fisher.Batching.FetchStreamStatePlan(streamKey),
            _ => throw new ArgumentOutOfRangeException(nameof(streamIdentity),
                $"Fisher has no stream identity of type {streamIdentity.GetType().FullName}")
        };

        return batched
            ? RunBatchedAsync(session, plan.Fetch, token)
            : plan.Fetch(session, token);
    }

    /// <inheritdoc cref="SupportsStreamQueryPlans" />
    public override Task<IReadOnlyList<IEvent>> FetchStreamByPlanAsync(
        IQuerySession session, object streamIdentity, long version, bool batched, CancellationToken token)
    {
        var plan = streamIdentity switch
        {
            Guid streamId => new Fisher.Batching.FetchStreamPlan(streamId, version),
            string streamKey => new Fisher.Batching.FetchStreamPlan(streamKey, version),
            _ => throw new ArgumentOutOfRangeException(nameof(streamIdentity),
                $"Fisher has no stream identity of type {streamIdentity.GetType().FullName}")
        };

        return batched
            ? RunBatchedAsync(session, plan.Fetch, token)
            : plan.Fetch(session, token);
    }

    /// <summary>
    ///     Run one plan through <see cref="Fisher.Batching.IBatchedQuery" /> — enqueue it, execute the
    ///     batch, then await the item's own task.
    /// </summary>
    /// <remarks>
    ///     The two-step await is the batch's contract rather than ceremony: an item's task is completed
    ///     or faulted by <c>Execute</c>, so awaiting it before executing would hang.
    /// </remarks>
    private static async Task<T> RunBatchedAsync<T>(IQuerySession session,
        Func<Fisher.Batching.IBatchedQuery, Task<T>> enqueue, CancellationToken token)
    {
        var batch = ((IDocumentSession)session).Events.CreateBatchQuery();
        var item = enqueue(batch);

        await batch.Execute(token).ConfigureAwait(false);

        return await item.ConfigureAwait(false);
    }

    /// <summary>
    ///     Left false: Fisher has no <c>QueryAllRawEvents()</c> to hang a <c>HasTag</c> predicate off.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A legitimate divergence rather than a gap waiting to be filled.</b> Marten and Polecat
    ///         translate the marker by matching the extension method's declaring type inside a LINQ
    ///         provider that already serves an <c>IQueryable&lt;IEvent&gt;</c>. Fisher's LINQ provider is
    ///         built over <em>document</em> storage — its statements, selectors and member factories all
    ///         resolve against a <c>fi_doc_*</c> table — so an event queryable would need a parallel
    ///         provider built to serve one caller. That is why
    ///         <c>EventOperations.QueryEventsAsync</c> takes a predicate rather than returning a
    ///         queryable, and why <c>AssignTagWhere</c> reaches the same parser through
    ///         <c>EventMemberFactory</c> instead.
    ///     </para>
    ///     <para>
    ///         Nothing is declined behaviourally: DCB tag querying itself is enrolled and green through
    ///         <c>DcbTagQueryAndConsistencyCompliance</c> and <c>AssignTagWhereCompliance</c>, which is
    ///         the same capability reached by Fisher's own spelling. What is declined is the LINQ
    ///         surface, which is the thing the shared library keeps out of scope permanently everywhere
    ///         else.
    ///     </para>
    /// </remarks>
    public override bool SupportsHasTagLinqPredicates => false;

    /// <summary>
    ///     Left false for the same structural reason as <see cref="SupportsHasTagLinqPredicates" />:
    ///     <c>AggregateToAsync</c> and <c>AggregateToManyAsync</c> are terminators over
    ///     <c>QueryAllRawEvents()</c>, which Fisher does not have.
    /// </summary>
    /// <remarks>
    ///     Fisher answers the same questions through <c>AggregateStreamAsync</c> and
    ///     <c>AggregateByTagsAsync</c>; what it cannot offer is the cross-stream LINQ query those
    ///     operators terminate.
    /// </remarks>
    public override bool SupportsAggregateToLinqOperators => false;

    /// <summary>
    ///     Fisher has ancillary store registration (<c>AddFisherStore&lt;T&gt;</c>, fisher#46) and its
    ///     daemon registration produces an <see cref="IProjectionCoordinator{T}" /> keyed on the marker.
    /// </summary>
    public override bool SupportsAncillaryCoordinators => true;

    /// <inheritdoc cref="SupportsAncillaryCoordinators" />
    public override IProjectionCoordinator AncillaryCoordinatorFrom(IServiceProvider services)
        => services.GetRequiredService<IProjectionCoordinator<IComplianceAncillaryStore>>();

    /// <summary>
    ///     jasperfx#732 — build and start a host registering Fisher the documented way, so the suite can
    ///     observe whether that registration produces a reachable coordinator.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the suite fisher#138 is the reason for</b>, so the registration here has to be
    ///         exactly what the documentation tells an application to write — <c>AddFisher(...)</c> plus
    ///         <c>AddAsyncDaemon()</c> — rather than anything the fixture could arrange for itself.
    ///     </para>
    ///     <para>
    ///         The hosted store points at the fixture's own throwaway file and folds in the same
    ///         <c>DatabaseSchemaName</c>, which on SQLite <em>is</em> the isolation boundary between two
    ///         logical stores in one file — so the per-test <c>CleanEventDataAsync</c> reaches the hosted
    ///         store's rows too. The schema is applied already, by the fixture's own store; the
    ///         registration still asks for it, because the ancillary store below has a file of its own
    ///         that nothing else has migrated.
    ///     </para>
    ///     <para>
    ///         The ancillary store gets a <em>second</em> file rather than a second schema name in the
    ///         same one. That is the shape fisher#46 exists for and the one that gets two concurrent
    ///         writers out of SQLite, and it also keeps the two daemons off one write lock — two daemons
    ///         over one file is precisely what the same-instance fact next door is guarding against.
    ///     </para>
    /// </remarks>
    protected override async Task<IComplianceCoordinatorHost<IDocumentSession>> StartCoordinatorHostAsync(
        ComplianceStoreConfig config, bool includeAncillaryStore)
    {
        var schemaName = (config.SchemaName ?? "compliance").ToLowerInvariant();
        var connectionString = _database!.ConnectionString;

        TemporaryDatabase? ancillaryDatabase = includeAncillaryStore
            ? TemporaryDatabase.Create($"{schemaName}-ancillary")
            : null;

        var builder = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddFisher(options =>
                    {
                        options.ConnectionString = connectionString;
                        options.AutoCreateSchemaObjects = AutoCreate.All;
                        options.DatabaseSchemaName = schemaName;

                        config.ApplyTo(new FisherComplianceRegistrar(options));
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .AddAsyncDaemon();

                if (ancillaryDatabase is not null)
                {
                    services.AddFisherStore<IComplianceAncillaryStore>(options =>
                        {
                            options.ConnectionString = ancillaryDatabase.ConnectionString;
                            options.AutoCreateSchemaObjects = AutoCreate.All;

                            config.ApplyTo(new FisherComplianceRegistrar(options));
                        })
                        .ApplyAllDatabaseChangesOnStartup()
                        .AddAsyncDaemon();
                }
            });

        var host = await builder.StartAsync(Cancellation).ConfigureAwait(false);

        return new FisherCoordinatorHost(host, ancillaryDatabase, Cancellation);
    }

    /// <inheritdoc cref="StartCoordinatorHostAsync" />
    private sealed class FisherCoordinatorHost : IComplianceCoordinatorHost<IDocumentSession>
    {
        private readonly IHost _host;
        private readonly TemporaryDatabase? _ancillaryDatabase;
        private readonly CancellationToken _token;

        internal FisherCoordinatorHost(IHost host, TemporaryDatabase? ancillaryDatabase, CancellationToken token)
        {
            _host = host;
            _ancillaryDatabase = ancillaryDatabase;
            _token = token;
        }

        public IServiceProvider Services => _host.Services;

        public IDocumentSession OpenSession()
            => _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();

        /// <remarks>
        ///     <c>IHost.Dispose</c> alone does not call <c>StopAsync</c>, so an abandoned host would
        ///     leave its daemon polling the fixture's file into the next test — two writers on one file,
        ///     which on SQLite is the difference between a passing suite and an intermittent one.
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync(_token).ConfigureAwait(false);
            _host.Dispose();

            _ancillaryDatabase?.Dispose();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await DisposeStoreAsync().ConfigureAwait(false);
    }

    private async Task DisposeStoreAsync()
    {
        if (_daemon is not null)
        {
            // Stopped before the store, because the daemon's shards hold sessions against the database
            // the store is about to dispose.
            await _daemon.StopAllAsync().ConfigureAwait(false);
            _daemon.Dispose();
            _daemon = null;
        }

        if (_store is not null)
        {
            await _store.DisposeAsync().ConfigureAwait(false);
            _store = null;
        }

        _database?.Dispose();
        _database = null;
    }

    private static Fisher.Internal.FisherSession AsFisherSession(IDocumentSession session)
        => (Fisher.Internal.FisherSession)session;

    /// <summary>
    ///     Translates the store-neutral configuration into Fisher's own options.
    /// </summary>
    internal class FisherComplianceRegistrar : IComplianceStoreRegistrar
    {
        private readonly StoreOptions _options;

        public FisherComplianceRegistrar(StoreOptions options)
        {
            _options = options;
        }

        public void AddEventType(Type eventType) => _options.Events.AddEventType(eventType);

        public ITagTypeRegistration RegisterTagType<TTag>(string tableSuffix) where TTag : notnull
            => _options.Events.RegisterTagType<TTag>(tableSuffix);

        /// <summary>
        ///     fisher#93 — a binary serializer for one event type.
        /// </summary>
        public void UseBinarySerializer<TEvent>(JasperFx.Events.IEventBinarySerializer serializer)
            where TEvent : notnull
            => _options.Events.UseBinarySerializer<TEvent>(serializer);

        /// <inheritdoc cref="UseBinarySerializer{TEvent}" />
        public void SetDefaultBinarySerializer(JasperFx.Events.IEventBinarySerializer serializer)
            => _options.Events.DefaultBinarySerializer = serializer;

        /// <inheritdoc cref="FisherComplianceFixture.SupportsLiveAggregationRegistration" />
        public void LiveAggregation<TDoc>() where TDoc : notnull
        {
        }

        /// <summary>
        ///     fisher#97 — enroll an aggregate type in the second-level <c>FetchForWriting</c> snapshot
        ///     cache, using the cache instance the suite supplied so it can see what the store did with
        ///     it.
        /// </summary>
        /// <remarks>
        ///     Both halves are on JasperFx's own <c>EventRegistry</c>, which <c>EventGraph</c> derives
        ///     from, so this is the two lines the shared registrar's remarks predict. The suite supplies
        ///     the instance rather than reading the store's own because every behavioural fact about
        ///     caching is vacuously true of a store that ignored the opt-in — an uncached fetch being
        ///     correct by construction — so the hit count is the only thing that separates the two.
        /// </remarks>
        public void CacheAggregatesForWriting<TDoc>(JasperFx.Events.Fetching.IAggregateWriteCache cache)
            where TDoc : class
        {
            _options.Events.AggregateWriteCaching.Cache = cache;
            _options.Events.CacheAggregatesForWriting<TDoc>();
        }

        /// <summary>
        ///     Delegates to <c>StoreOptions.RegisterValueType&lt;T&gt;()</c> (fisher#75).
        /// </summary>
        /// <remarks>
        ///     Fisher still discovers strong-typed identifiers from their shape, so nothing depends on
        ///     the call — it was a no-op here until the store had one to delegate to. Delegating rather
        ///     than staying empty is what makes the seam honest: the suite now exercises the same
        ///     method a consumer would call, and a wrapper the store cannot actually resolve fails
        ///     inside the suite rather than passing on a stub.
        /// </remarks>
        /// <seealso cref="Fisher.Storage.StrongTypedId" />
        public void RegisterValueType<TValue>() where TValue : notnull
            => _options.RegisterValueType<TValue>();

        /// <summary>
        ///     Register a mutating masking rule — the in-place form, for an event whose protected
        ///     members are settable.
        /// </summary>
        /// <remarks>
        ///     Every store spells this on its own event options rather than on a shared interface,
        ///     which is why the seam carries it. Fisher's is
        ///     <see cref="Fisher.Events.EventGraph.AddMaskingRuleForProtectedInformation{T}(Action{T})" />,
        ///     and the two signatures match exactly — including the contravariant reach, which falls
        ///     out of <c>IEvent&lt;out T&gt;</c> being covariant.
        /// </remarks>
        public void AddMaskingRule<TEvent>(Action<TEvent> rule) where TEvent : notnull
            => _options.Events.AddMaskingRuleForProtectedInformation(rule);

        /// <summary>
        ///     Register a replacing masking rule — the functional form, which is what a <c>record</c>
        ///     with init-only members needs.
        /// </summary>
        /// <inheritdoc cref="AddMaskingRule{TEvent}(Action{TEvent})" path="/remarks" />
        public void AddMaskingRule<TEvent>(Func<TEvent, TEvent> rule) where TEvent : notnull
            => _options.Events.AddMaskingRuleForProtectedInformation(rule);

        public void Snapshot<TDoc>(SnapshotLifecycle lifecycle) where TDoc : notnull
            => _options.Projections.Snapshot<TDoc>(lifecycle);

        public void AddProjection(ProjectionBase projection, ProjectionLifecycle lifecycle)
            => _options.Projections.Add(projection, lifecycle);

        /// <summary>
        ///     jasperfx#725 — build a composite projection and populate its stages, enrolling
        ///     <c>CompositeProjectionCompliance</c>.
        /// </summary>
        /// <remarks>
        ///     The seam exists because a composite cannot be constructed by a suite at all:
        ///     <see cref="Fisher.Projections.FisherCompositeProjection" /> keeps its constructor
        ///     internal, needing the store's options, so one only comes into being through
        ///     <c>Projections.CompositeProjectionFor(name, configure)</c> — whose <c>configure</c> is
        ///     typed to Fisher's own subclass. This is the forward-plus-adapter the shared registrar's
        ///     remarks predict, and the adapter carries the one member the suite needs
        ///     (<c>Snapshot&lt;T&gt;(stageNumber)</c>), which all three stores spell identically.
        /// </remarks>
        public void AddCompositeProjection(string name, Action<IComplianceCompositeBuilder> configure)
            => _options.Projections.CompositeProjectionFor(name,
                composite => configure(new ComplianceCompositeBuilder(composite)));

        /// <inheritdoc cref="AddCompositeProjection" />
        private sealed class ComplianceCompositeBuilder : IComplianceCompositeBuilder
        {
            private readonly FisherCompositeProjection _composite;

            internal ComplianceCompositeBuilder(FisherCompositeProjection composite)
                => _composite = composite;

            public void Snapshot<TDoc>(int stageNumber) where TDoc : notnull
                => _composite.Snapshot<TDoc>(stageNumber);
        }

        /// <summary>
        ///     Register the shared compliance subscription with Fisher's async daemon.
        /// </summary>
        /// <remarks>
        ///     The name is pinned rather than left to default. Fisher's <c>SubscriptionWrapper</c>
        ///     happens to take the subscription's short type name, which is already
        ///     <see cref="ComplianceSubscription.SubscriptionName" /> — but progression is keyed on
        ///     that string, so a store must not have it depend on a naming convention it could
        ///     reasonably change.
        /// </remarks>
        public void Subscribe(ComplianceSubscription subscription)
            => _options.Projections.Subscribe(subscription, options =>
            {
                options.Name = ComplianceSubscription.SubscriptionName;

                // jasperfx#768 — the allow list has to be replayed onto the wrapper, because that is
                // what the daemon reads filters from. `Projections.Subscribe` wraps a bare
                // ISubscription in SubscriptionWrapper and the wrapper copies nothing across, so a
                // filter declared on the subscription object alone would be silently ignored — the
                // subscription would be handed every event and the suite's filter fact would fail
                // rather than the registration failing.
                foreach (var eventType in subscription.IncludedEventTypes)
                {
                    options.IncludeType(eventType);
                }
            });

        /// <summary>
        ///     jasperfx#763 — install the suite's recording outbox as the store's message outbox.
        /// </summary>
        /// <remarks>
        ///     One assignment, because <c>NulloMessageOutbox</c> is a default rather than a branch:
        ///     nothing in Fisher asks whether an outbox is "real", so replacing it is the whole of what
        ///     a bus integration does too.
        /// </remarks>
        public void UseMessageOutbox(RecordingMessageOutbox outbox)
            => _options.Events.MessageOutbox = outbox;
    }
}

/// <summary>
///     The marker for the ancillary store <c>ProjectionCoordinatorCompliance</c>'s one gated fact
///     registers (fisher#46).
/// </summary>
/// <remarks>
///     A marker type cannot be shared: <c>AddFisherStore&lt;T&gt;</c> constrains it to
///     <see cref="IDocumentStore" />, and every product constrains its own to its own store interface —
///     which is why the suite reaches the ancillary coordinator through
///     <see cref="FisherComplianceFixture.AncillaryCoordinatorFrom" /> rather than by naming a type.
/// </remarks>
public interface IComplianceAncillaryStore : IDocumentStore;
