using Fisher.Events;
using Fisher.Internal;
using Fisher.Storage;
using JasperFx;

namespace Fisher;

/// <summary>
///     The root of a Fisher store: owns configuration and the database, and creates sessions.
///     Singleton, thread-safe, and expensive to build — one per application, as in Marten and Polecat.
/// </summary>
/// <remarks>
///     Partial: the <see cref="JasperFx.Events.IEventStore" /> surface — the explorer and diagnostic
///     methods monitoring tools read — lives in <c>DocumentStore.EventStore.cs</c>, implemented
///     explicitly so it does not crowd the store's own API. The store's <em>own</em> API is
///     <see cref="IDocumentStore" />, which this class implements implicitly.
/// </remarks>
public partial class DocumentStore : IDocumentStore
{
    public DocumentStore(StoreOptions options)
    {
        options.AssertValid();

        Options = options;
        Tenancy = options switch
        {
            // Dynamic first: a source is a strictly later-binding answer than a fixed list, so a store
            // configured with both meant the one that can still change its mind.
            { TenantSource: { } source } => new DynamicTenancy(options, source),
            { TenantDatabases: { } configured } => new SeparateDatabaseTenancy(options, configured),
            _ => new DefaultTenancy(options)
        };

        Database = Tenancy.Default;
        options.StorageDatabase = Database;

        // Register the self-aggregating types whose evolvers the source generator emitted, so
        // Projections.AllAggregateTypes() reports an aggregate that was never registered by hand.
        // Discovery is by assembly-level [GeneratedEvolver] attribute, and the scan skips framework
        // assemblies, so this is cheap enough to do unconditionally at construction — which is the
        // only place it can happen, since the point is to know about types nobody mentioned.
        options.Projections.DiscoverGeneratedEvolvers(AppDomain.CurrentDomain.GetAssemblies());

        // A flat table's physical name folds in the store's logical schema, and this is the first
        // moment that schema is final — the projection and DatabaseSchemaName are usually set in the
        // same configuration lambda, in whichever order the caller wrote them.
        foreach (var flatTable in options.Projections.All.OfType<Projections.Flattened.FlatTableProjection>())
        {
            flatTable.ResolveTableName(options.DatabaseSchemaName);
        }

        // Builds the async shard registry and fails fast on duplicate projection names.
        options.Projections.AssertValidity(options);

        // Built once here rather than per session: BuildForInline compiles each projection.
        options.Projections.BuildInlineProjections();
    }

    /// <summary>
    ///     Build a store from an inline configuration, mirroring Marten's
    ///     <c>DocumentStore.For(opts =&gt; ...)</c>.
    /// </summary>
    public static DocumentStore For(Action<StoreOptions> configure)
    {
        var options = new StoreOptions();
        configure(options);
        return new DocumentStore(options);
    }

    /// <summary>
    ///     Build a store against a connection string with all defaults.
    /// </summary>
    public static DocumentStore For(string connectionString)
        => For(options => options.ConnectionString = connectionString);

    public StoreOptions Options { get; }

    /// <summary>
    ///     Which database a tenant's data lives in (fisher#47).
    /// </summary>
    /// <remarks>
    ///     <see cref="DefaultTenancy" /> unless the store called
    ///     <see cref="StoreOptions.MultiTenantedDatabases" /> — one file for every tenant, which is what
    ///     conjoined tenancy and single-tenant stores both want.
    /// </remarks>
    public ITenancy Tenancy { get; }

    /// <summary>
    ///     The database a store-level operation uses when no tenant is named.
    /// </summary>
    /// <remarks>
    ///     Under database-per-tenant this is the default tenant's file, and is <em>not</em> every
    ///     tenant's — reach the rest through <see cref="Tenancy" />. Kept as a property because it is
    ///     the answer for every store that is not database-per-tenant, which is nearly all of them.
    /// </remarks>
    public FisherDatabase Database { get; }

    /// <summary>
    ///     Cleaning, resetting, and the Hi-Lo knobs — everything outside the session API.
    /// </summary>
    public AdvancedOperations Advanced => _advanced ??= new AdvancedOperations(this);

    /// <summary>
    ///     The projection coordinator currently running this store's daemons, or null when none is.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Set by <c>FisherDaemonHostedService</c> when the host starts it and cleared when it
    ///         stops, so it is a fact about <em>right now</em> rather than about registration:
    ///         <c>AddAsyncDaemon(DaemonMode.ExternallyManaged)</c> leaves it null, and so does a store
    ///         built directly with <see cref="For(System.Action{StoreOptions})" />.
    ///     </para>
    ///     <para>
    ///         It exists because <see cref="AdvancedOperations.ResetAllDataAsync" /> has to pause the
    ///         daemon around the wipe (fisher#138) and reaches the store, not the container. Deliberately
    ///         <b>not</b> a general escape hatch onto the coordinator — application code resolves
    ///         <c>IProjectionCoordinator</c> from DI, which is the store-agnostic route and the one the
    ///         siblings offer.
    ///     </para>
    /// </remarks>
    internal JasperFx.Events.Daemon.IProjectionCoordinator? RunningDaemons { get; set; }

    private AdvancedOperations? _advanced;

    internal EventGraph EventGraph => Options.EventGraph;

    /// <summary>
    ///     Open a lightweight session — no identity map, no dirty tracking.
    /// </summary>
    /// <param name="tenantId">
    ///     The tenant every operation in the session is scoped to. Defaults to the single-tenant
    ///     default.
    /// </param>
    public IDocumentSession LightweightSession(string? tenantId = null)
        => OpenSession(new SessionOptions { TenantId = tenantId ?? StorageConstants.DefaultTenantId });

    /// <summary>
    ///     Open a session with an identity map — a document loaded or stored under an identity is
    ///     handed back as the same instance for the rest of the session (fisher#31).
    /// </summary>
    /// <remarks>
    ///     The map covers loads by id and <c>Query&lt;T&gt;()</c> alike. See
    ///     <see cref="DocumentTracking.IdentityOnly" /> for what it buys and what it costs.
    /// </remarks>
    public IDocumentSession IdentitySession(string? tenantId = null)
        => OpenSession(new SessionOptions
        {
            TenantId = tenantId ?? StorageConstants.DefaultTenantId,
            Tracking = DocumentTracking.IdentityOnly
        });

    /// <summary>
    ///     Open a session that detects changes to the documents it loaded, so
    ///     <c>SaveChangesAsync</c> writes them without <c>Store</c> being called.
    /// </summary>
    /// <remarks>
    ///     Includes the identity map. See <see cref="DocumentTracking.DirtyTracking" /> for the cost —
    ///     a serialized snapshot per document read, and a re-serialization per document per commit.
    /// </remarks>
    public IDocumentSession DirtyTrackedSession(string? tenantId = null)
        => OpenSession(new SessionOptions
        {
            TenantId = tenantId ?? StorageConstants.DefaultTenantId,
            Tracking = DocumentTracking.DirtyTracking
        });

    /// <summary>
    ///     Open a session configured by <see cref="SessionOptions" /> — a tenant, a tracking mode, a
    ///     timeout, or a connection or transaction of your own to run inside.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>There is no <c>LightweightSession(SessionOptions)</c> and no <c>OpenSessionAsync</c>,
    ///         where Polecat has both.</b> Tracking is a property of the options rather than a choice of
    ///         constructor — <see cref="SessionOptions.Tracking" /> defaults to
    ///         <see cref="DocumentTracking.None" />, so this method already <em>is</em> the lightweight
    ///         one and the first would be a second name for it. And a session opens its connection
    ///         lazily on first use, so the second would be an asynchronous method with nothing to await;
    ///         Polecat needs the async form because its session may open a connection eagerly.
    ///     </para>
    /// </remarks>
    public IDocumentSession OpenSession(SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The database is resolved per session from the tenant, which is the whole of what
        // database-per-tenant changes about the session path — under DefaultTenancy every tenant
        // resolves the one database, so nothing moves for a store that did not ask for this.
        return new FisherSession(Options, Tenancy.DatabaseFor(options.TenantId), options);
    }

    /// <summary>
    ///     A session pinned to one database rather than resolved from a tenant id (fisher#57).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The daemon's direction of travel is the opposite of an application's. An application says
    ///         "this tenant" and the store finds the file; a shard already <em>is</em> a file — it read
    ///         its events from one and must write its documents to the same one — and needs a session
    ///         there. Resolving through <see cref="ITenancy.DatabaseFor" /> would round-trip through a
    ///         tenant id that, under <see cref="DefaultTenancy" />, does not identify a database at all.
    ///     </para>
    ///     <para>
    ///         The tenant is the database's own under database-per-tenant, so a projection's writes are
    ///         stamped for the tenant whose events produced them. Under every other tenancy the database
    ///         knows no tenant and the caller's answer stands — which is what keeps a conjoined store's
    ///         shard writing per tenant as it always did.
    ///     </para>
    /// </remarks>
    /// <summary>
    ///     Pull in tenants that have appeared since the store was built (fisher#58).
    /// </summary>
    /// <remarks>
    ///     A no-op under every tenancy but <see cref="DynamicTenancy" />, where the set of tenants is
    ///     asked for rather than declared. Sessions do not need it — a tenant resolves the moment it is
    ///     named — so this is for the callers that enumerate: the startup migration and the daemon.
    /// </remarks>
    internal ValueTask RefreshTenantsAsync(CancellationToken token = default)
        => Tenancy is DynamicTenancy dynamic ? dynamic.RefreshAsync(token) : ValueTask.CompletedTask;

    internal IDocumentSession OpenSessionOn(FisherDatabase database, string? tenantId = null)
    {
        var options = new SessionOptions
        {
            TenantId = database.TenantId ?? tenantId ?? StorageConstants.DefaultTenantId
        };

        return new FisherSession(Options, database, options);
    }

    /// <summary>
    ///     Open a read-only session.
    /// </summary>
    /// <remarks>
    ///     <b>The narrowing is a convention, not a guarantee.</b> Fisher has no query-only session
    ///     type — <see cref="IQuerySession" /> is the read half of <see cref="IDocumentSession" /> —
    ///     so this is the same session as <see cref="LightweightSession" />, and casting it back to
    ///     <see cref="IDocumentSession" /> yields a working write handle. That is deliberate: a second
    ///     session type would cost a connection per scope to express a distinction the store does not
    ///     make. Use it to say what a piece of code intends, not to stop it doing otherwise.
    /// </remarks>
    public IQuerySession QuerySession(string? tenantId = null) => LightweightSession(tenantId);

    /// <inheritdoc cref="QuerySession(string)" />
    public IQuerySession QuerySession(SessionOptions options) => OpenSession(options);

    /// <summary>
    ///     Apply every configured schema change to every database this store spans.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Fisher applies schema changes explicitly rather than lazily on first use. SQLite
    ///         serializes writers at the file level, so a lazy first-use migration would have every
    ///         session racing to take the write lock for DDL on startup.
    ///     </para>
    ///     <para>
    ///         <b>Under database-per-tenant this migrates N files, and a partial failure is reported per
    ///         database rather than swallowed</b> (fisher#47). Migrating a hundred tenants and stopping
    ///         at the fortieth leaves a store in mixed versions either way; what
    ///         <see cref="TenantMigrationException" /> adds is that the caller is told which tenants are
    ///         migrated and which are not, instead of one exception naming whichever failed first.
    ///     </para>
    /// </remarks>
    public async Task ApplyAllConfiguredChangesToDatabaseAsync(CancellationToken token = default)
    {
        // Under dynamic tenancy the store has resolved nothing yet, so without this a startup migration
        // would find no databases and report success. A tenant that appears later migrates itself on
        // first connection, which is what makes this a starting point rather than the whole story.
        await RefreshTenantsAsync(token).ConfigureAwait(false);

        var databases = Tenancy.AllDatabases();

        if (databases.Count == 1)
        {
            await databases[0].ApplyAllConfiguredChangesToDatabaseAsync(ct: token).ConfigureAwait(false);
            return;
        }

        var migrated = new List<string>();
        var failures = new Dictionary<string, Exception>();

        // Sequentially, and that is not laziness. Every tenant's migration takes its own file's write
        // lock, so running them in parallel would win nothing on the DDL itself and would hold N
        // connections open at once against a pool ceiling that sizes one file.
        foreach (var database in databases)
        {
            try
            {
                await database.ApplyAllConfiguredChangesToDatabaseAsync(ct: token).ConfigureAwait(false);
                migrated.Add(database.Identifier);
            }
            catch (Exception e)
            {
                failures[database.Identifier] = e;
            }
        }

        if (failures.Count > 0)
        {
            throw new TenantMigrationException(migrated, failures);
        }
    }

    /// <summary>
    ///     Throw if any database this store spans does not already match the configured schema, without
    ///     changing anything (fisher#172).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The read half of <see cref="ApplyAllConfiguredChangesToDatabaseAsync" />, and the same
    ///         question <c>db-assert</c> asks — so a deployment that applies its schema out of band can
    ///         verify it, and a host can refuse to start against a database it would silently misread.
    ///     </para>
    ///     <para>
    ///         <b>Every database, not just the default</b>, and reported per database for the same
    ///         reason the migration is: under database-per-tenant, asserting one file and calling the
    ///         store verified is the answer most likely to be wrong. Unlike the migration this stops at
    ///         the first mismatch — there is nothing partial to report, and the caller's next act is to
    ///         look at the one that failed.
    ///     </para>
    /// </remarks>
    public async Task AssertDatabaseMatchesConfigurationAsync(CancellationToken token = default)
    {
        await RefreshTenantsAsync(token).ConfigureAwait(false);

        foreach (var database in Tenancy.AllDatabases())
        {
            await database.AssertDatabaseMatchesConfigurationAsync(token).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await Tenancy.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc cref="Storage.FisherDatabase.Dispose" />
    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Tenancy.Dispose();
    }
}
