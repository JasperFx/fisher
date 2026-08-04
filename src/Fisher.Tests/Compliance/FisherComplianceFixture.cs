using Fisher.Events;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.ComplianceTests;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Tags;
using Microsoft.Data.Sqlite;

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

    public override string? CorrelationIdFor(IDocumentSession session) => AsFisherSession(session).CorrelationId;

    public override string? CausationIdFor(IDocumentSession session) => AsFisherSession(session).CausationId;

    public override void SetCorrelationId(IDocumentSession session, string? correlationId)
        => AsFisherSession(session).CorrelationId = correlationId;

    /// <summary>
    ///     Wipe event data between tests.
    /// </summary>
    /// <remarks>
    ///     Called before every test by the suite base, so it cannot throw the way the unsupported
    ///     members below do. Deleting straight from the tables is a stand-in for <c>Advanced.Clean</c>,
    ///     which Fisher does not have yet — move this there when it lands rather than growing it here.
    /// </remarks>
    public override async Task CleanEventDataAsync()
    {
        if (_store is null)
        {
            return;
        }

        var events = Store.Options.EventGraph;

        await using var connection = new SqliteConnection(_database!.ConnectionString);
        await connection.OpenAsync(Cancellation).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            delete from {events.EventsTableName};
            delete from {events.StreamsTableName};
            delete from {events.ProgressionTableName};
            """;

        await command.ExecuteNonQueryAsync(Cancellation).ConfigureAwait(false);
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

    public override Task<T?> LoadDocumentAsync<T>(IQuerySession session, object id, CancellationToken token)
        where T : class
        => throw new NotSupportedException(
            "Fisher has no document storage yet, so a projected document cannot be loaded back.");

    public override void StoreDocument<T>(IDocumentSession session, T document)
        => throw new NotSupportedException("Fisher has no document storage yet.");

    public override IEventStore EventStore
        => throw new NotSupportedException(
            "Fisher's DocumentStore does not implement JasperFx's IEventStore yet.");

    public override IEnumerable<Type> AllAggregateTypes()
        => throw new NotSupportedException(
            "Fisher has no StoreOptions.Projections, and so no ProjectionGraph.AllAggregateTypes() and " +
            "no assembly scan for source-generated evolvers.");

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
            => throw new NotSupportedException(
                "Fisher cannot register a snapshot projection yet — that needs StoreOptions.Projections " +
                "and document storage to write the snapshot to.");

        public void AddProjection(ProjectionBase projection, ProjectionLifecycle lifecycle)
            => throw new NotSupportedException(
                "Fisher cannot register a projection yet — there is no StoreOptions.Projections.");
    }
}
