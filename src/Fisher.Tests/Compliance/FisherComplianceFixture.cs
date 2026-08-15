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

        /// <inheritdoc cref="FisherComplianceFixture.SupportsLiveAggregationRegistration" />
        public void LiveAggregation<TDoc>() where TDoc : notnull
        {
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
            => _options.Projections.Subscribe(subscription,
                options => options.Name = ComplianceSubscription.SubscriptionName);
    }
}
