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
///     explicitly so it does not crowd the store's own API.
/// </remarks>
public partial class DocumentStore : IAsyncDisposable
{
    public DocumentStore(StoreOptions options)
    {
        options.AssertValid();

        Options = options;
        Database = new FisherDatabase(options);
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
    ///     The database this store reads and writes, and whose schema it manages.
    /// </summary>
    public FisherDatabase Database { get; }

    /// <summary>
    ///     Cleaning, resetting, and the Hi-Lo knobs — everything outside the session API.
    /// </summary>
    public AdvancedOperations Advanced => _advanced ??= new AdvancedOperations(this);

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
        => new FisherSession(Options, Database, tenantId ?? StorageConstants.DefaultTenantId);

    /// <summary>
    ///     Apply every configured schema change to the database.
    /// </summary>
    /// <remarks>
    ///     Fisher applies schema changes explicitly rather than lazily on first use. SQLite serializes
    ///     writers at the file level, so a lazy first-use migration would have every session racing to
    ///     take the write lock for DDL on startup.
    /// </remarks>
    public Task ApplyAllConfiguredChangesToDatabaseAsync(CancellationToken token = default)
        => Database.ApplyAllConfiguredChangesToDatabaseAsync(ct: token);

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await Database.DisposeAsync().ConfigureAwait(false);
    }
}
