using Fisher.Events;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.ComplianceTests;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Tags;

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

            if (config.EnableHeaders)
            {
                options.Events.EnableHeaders = true;
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

    public override IEnumerable<Type> AllAggregateTypes() => Store.Options.Projections.AllAggregateTypes();

    public override Task<T?> LoadDocumentAsync<T>(IQuerySession session, object id, CancellationToken token)
        where T : class
        => id switch
        {
            Guid guid => session.LoadAsync<T>(guid, token),
            string key => session.LoadAsync<T>(key, token),
            int number => session.LoadAsync<T>(number, token),
            long number => session.LoadAsync<T>(number, token),
            _ => throw new NotSupportedException(
                $"Fisher cannot load a document by an identity of type {id.GetType().FullName}. " +
                "Strongly typed ids are not supported anywhere in Fisher yet.")
        };

    public override void StoreDocument<T>(IDocumentSession session, T document) => session.Store(document);

    public override string? CorrelationIdFor(IDocumentSession session) => AsFisherSession(session).CorrelationId;

    public override string? CausationIdFor(IDocumentSession session) => AsFisherSession(session).CausationId;

    public override void SetCorrelationId(IDocumentSession session, string? correlationId)
        => AsFisherSession(session).CorrelationId = correlationId;

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

    public override bool SupportsAsyncDaemon => false;

    // ---- not supported yet ----
    //
    // Each of these names the milestone it waits on. A suite that touches one is a suite Fisher is
    // not ready to enroll; see Compliance/fisher_event_store_compliance.cs for what is enrolled.

    public override IComplianceBatch CreateBatch(IQuerySession session)
        => throw new NotSupportedException("Fisher has no batched query support yet.");

    public override Task<IProjectionDaemon> StartDaemonAsync()
        => throw new NotSupportedException("Fisher has no async projection daemon yet.");

    public override Task WaitForNonStaleProjectionDataAsync(TimeSpan timeout)
        => throw new NotSupportedException("Fisher has no async projection daemon yet.");

    public override async ValueTask DisposeAsync()
    {
        await DisposeStoreAsync().ConfigureAwait(false);
    }

    private async Task DisposeStoreAsync()
    {
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

        /// <inheritdoc cref="FisherComplianceFixture.SupportsLiveAggregationRegistration" />
        public void LiveAggregation<TDoc>() where TDoc : notnull
        {
        }

        public void Snapshot<TDoc>(SnapshotLifecycle lifecycle) where TDoc : notnull
            => _options.Projections.Snapshot<TDoc>(lifecycle);

        public void AddProjection(ProjectionBase projection, ProjectionLifecycle lifecycle)
            => _options.Projections.Add(projection, lifecycle);
    }
}
