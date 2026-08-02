using Fisher.Events;
using Fisher.Internal;
using Fisher.Storage;
using JasperFx;

namespace Fisher;

/// <summary>
///     The root of a Fisher store: owns configuration and the database, and creates sessions.
///     Singleton, thread-safe, and expensive to build — one per application, as in Marten and Polecat.
/// </summary>
public class DocumentStore : IAsyncDisposable
{
    public DocumentStore(StoreOptions options)
    {
        options.AssertValid();

        Options = options;
        Database = new FisherDatabase(options);
        options.StorageDatabase = Database;
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
