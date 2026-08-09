using JasperFx;
using JasperFx.Descriptors;

namespace Fisher.Storage;

/// <summary>
///     Which database a tenant's data lives in (fisher#47, stage 1).
/// </summary>
/// <remarks>
///     <para>
///         <b>Database-per-tenant is arguably SQLite's best tenancy story rather than its worst.</b>
///         The usual objection — provisioning a database per tenant is heavyweight — inverts here: a
///         tenant is a <em>file</em>. Creating one is a file plus a migration, deleting a tenant is
///         deleting a file, backing one up is copying it, and one tenant's data cannot leak into
///         another's because there is no shared table to leak through.
///     </para>
///     <para>
///         And it answers Fisher's sharpest structural constraint. Under conjoined tenancy every tenant
///         contends for one write lock, because there is one file. Under file-per-tenant they write
///         concurrently — which is the only way a multi-tenant Fisher application scales its write
///         throughput at all. That makes this a performance feature as much as an isolation one, which
///         is not true on either sibling.
///     </para>
///     <para>
///         <b>Conjoined tenancy stays, and stays the default.</b> The two are alternatives, as they are
///         on Marten and Polecat, and a store picks one.
///     </para>
///     <para>
///         <b><c>ATTACH</c> is deliberately not part of this.</b> Attaching would let one connection see
///         several tenants, but an attachment has per-connection lifecycle that must be re-established
///         on every pooled checkout — which is exactly what <see cref="FisherTableNaming" /> exists to
///         avoid.
///     </para>
/// </remarks>
public interface ITenancy : IAsyncDisposable, IDisposable
{
    /// <summary>How many databases this store spans, for monitoring tools.</summary>
    DatabaseCardinality Cardinality { get; }

    /// <summary>
    ///     The database a store-level operation uses when no tenant is named.
    /// </summary>
    FisherDatabase Default { get; }

    /// <summary>The database holding one tenant's data.</summary>
    FisherDatabase DatabaseFor(string tenantId);

    /// <summary>Every database this store spans.</summary>
    IReadOnlyList<FisherDatabase> AllDatabases();
}

/// <summary>
///     One database for every tenant — today's behaviour, and what a store gets unless it says
///     otherwise.
/// </summary>
/// <remarks>
///     Under this tenancy a tenant id is a <em>column value</em> (conjoined) or nothing at all
///     (single-tenant); either way there is one file, and <see cref="DatabaseFor" /> ignores its
///     argument. That is why the daemon's <c>IEventDatabase</c> parameters have always been ignorable.
/// </remarks>
public sealed class DefaultTenancy : ITenancy
{
    private readonly FisherDatabase _database;

    internal DefaultTenancy(StoreOptions options) => _database = new FisherDatabase(options);

    public DatabaseCardinality Cardinality => DatabaseCardinality.Single;

    public FisherDatabase Default => _database;

    public FisherDatabase DatabaseFor(string tenantId) => _database;

    public IReadOnlyList<FisherDatabase> AllDatabases() => [_database];

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    public void Dispose() => _database.Dispose();
}

/// <summary>
///     A database file per tenant, named at configuration time (fisher#47, stage 1).
/// </summary>
/// <remarks>
///     <para>
///         Tenants are fixed when the store is built. Adding, enabling and disabling them at runtime —
///         and the daemon picking a new one up without a restart — is stage 3
///         (<see href="https://github.com/JasperFx/fisher/issues/58">fisher#58</see>); running the
///         async daemon across several databases is stage 2
///         (<see href="https://github.com/JasperFx/fisher/issues/57">fisher#57</see>). <b>A store
///         configured this way and running the daemon projects the default database only</b>, and says
///         so rather than silently projecting one tenant's events into every tenant's documents.
///     </para>
///     <para>
///         <b>Each tenant's file has its own connection pool and its own PRAGMA application</b>, which
///         comes for free from <c>SqliteDataSource</c> — so WAL, busy timeout and foreign-key
///         enforcement are per file with nothing said. The cost is that
///         <see cref="StoreOptions.MaxPoolSize" /> sizes each tenant's pool rather than the store's, and
///         a store with a hundred tenants can hold a hundred pools' worth of connections.
///     </para>
/// </remarks>
public sealed class SeparateDatabaseTenancy : ITenancy
{
    private readonly Dictionary<string, FisherDatabase> _databases;
    private readonly FisherDatabase _default;

    internal SeparateDatabaseTenancy(StoreOptions options, TenantDatabases configured)
    {
        _databases = new Dictionary<string, FisherDatabase>(StringComparer.OrdinalIgnoreCase);

        foreach (var (tenantId, connectionString) in configured.Resolve(options))
        {
            _databases[tenantId] = new FisherDatabase(options, connectionString, tenantId);
        }

        // The store still needs a database for operations that name no tenant — applying the schema
        // reaches every one of them, but building a store must not fail because nothing has been
        // stored yet. The default tenant's file is it, created like any other.
        _default = _databases.TryGetValue(StorageConstants.DefaultTenantId, out var main)
            ? main
            : _databases.Values.FirstOrDefault()
              ?? throw new InvalidOperationException(
                  "MultiTenantedDatabases was configured with no tenants. Add at least one with "
                  + "AddTenant(tenantId, connectionString) or AddTenants(...), or drop the call and use "
                  + "conjoined tenancy.");
    }

    public DatabaseCardinality Cardinality => DatabaseCardinality.StaticMultiple;

    public FisherDatabase Default => _default;

    /// <remarks>
    ///     <b>An unknown tenant throws rather than falling back to the default.</b> Falling back would
    ///     write one tenant's data into another's file, which is the one failure database-per-tenant
    ///     exists to make impossible — and it would be silent. Stage 3 is what makes a tenant appear
    ///     without a restart; until then the set is what was configured.
    /// </remarks>
    public FisherDatabase DatabaseFor(string tenantId)
        => _databases.TryGetValue(tenantId, out var database)
            ? database
            : throw new UnknownTenantException(tenantId, _databases.Keys);

    public IReadOnlyList<FisherDatabase> AllDatabases() => _databases.Values.ToList();

    public async ValueTask DisposeAsync()
    {
        foreach (var database in _databases.Values)
        {
            await database.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        foreach (var database in _databases.Values)
        {
            database.Dispose();
        }
    }
}

/// <summary>
///     A tenant this store has no database for.
/// </summary>
public class UnknownTenantException : Exception
{
    internal UnknownTenantException(string tenantId, IEnumerable<string> known)
        : base($"This store has no database for tenant '{tenantId}'. It knows: "
               + $"{string.Join(", ", known.OrderBy(x => x, StringComparer.Ordinal))}. A tenant's database "
               + "is named at configuration time under MultiTenantedDatabases; falling back to another "
               + "tenant's file would be the one failure database-per-tenant exists to make impossible.")
    {
        TenantId = tenantId;
    }

    public string TenantId { get; }
}

/// <summary>
///     The tenants a <see cref="SeparateDatabaseTenancy" /> spans, and where their files live.
/// </summary>
/// <remarks>
///     <b>Both shapes the issue asked for, with the convention as the default.</b> An explicit
///     connection string per tenant is the flexible form; a directory plus a naming convention is what
///     makes a hundred tenants one line — and is the shape stage 3's dynamic tenants will need, since a
///     tenant that appears at runtime has no configuration line to carry a path.
/// </remarks>
public sealed class TenantDatabases
{
    private readonly Dictionary<string, string> _explicitConnections = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _byConvention = [];

    private string? _directory;

    /// <summary>
    ///     Name a tenant and the SQLite connection string its data lives in.
    /// </summary>
    public TenantDatabases AddTenant(string tenantId, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _explicitConnections[tenantId] = connectionString;
        return this;
    }

    /// <summary>
    ///     Put every tenant's file in one directory, named <c>&lt;tenantId&gt;.db</c>.
    /// </summary>
    /// <remarks>
    ///     The directory is created if it is not there, because a tenancy that required the operator to
    ///     have made a directory would fail at the first write with a message about a file.
    /// </remarks>
    public TenantDatabases InDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _directory = directory;
        return this;
    }

    /// <summary>
    ///     Name tenants whose files follow <see cref="InDirectory" />'s convention.
    /// </summary>
    public TenantDatabases AddTenants(params string[] tenantIds)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);

        _byConvention.AddRange(tenantIds);
        return this;
    }

    internal IEnumerable<(string TenantId, string ConnectionString)> Resolve(StoreOptions options)
    {
        foreach (var pair in _explicitConnections)
        {
            yield return (pair.Key, pair.Value);
        }

        if (_byConvention.Count == 0)
        {
            yield break;
        }

        if (_directory is null)
        {
            throw new InvalidOperationException(
                "AddTenants(...) names tenants whose files follow a directory convention, so "
                + "InDirectory(...) has to say which directory. Use AddTenant(tenantId, connectionString) "
                + "to name a file per tenant instead.");
        }

        Directory.CreateDirectory(_directory);

        foreach (var tenantId in _byConvention.Where(x => !_explicitConnections.ContainsKey(x)))
        {
            yield return (tenantId, new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_directory, $"{tenantId}.db")
            }.ToString());
        }
    }
}

/// <summary>
///     A schema migration that succeeded for some tenants and failed for others (fisher#47).
/// </summary>
/// <remarks>
///     <b>Reported rather than swallowed, because a store in mixed versions is the honest outcome and
///     hiding it is not.</b> Migrating a hundred tenant files and stopping at the fortieth leaves sixty
///     unmigrated whatever the exception says; what this adds is that the caller is told which are
///     which, instead of one exception naming whichever tenant happened to fail first.
/// </remarks>
public class TenantMigrationException : AggregateException
{
    internal TenantMigrationException(IReadOnlyList<string> migrated, IReadOnlyDictionary<string, Exception> failures)
        : base(
            $"Schema changes were applied to {migrated.Count} database(s) and failed for "
            + $"{failures.Count}: {string.Join(", ", failures.Keys.OrderBy(x => x, StringComparer.Ordinal))}. "
            + "The store is in mixed versions; the databases listed in Migrated are current.",
            failures.Values)
    {
        Migrated = migrated;
        Failures = failures;
    }

    /// <summary>The databases the migration completed for.</summary>
    public IReadOnlyList<string> Migrated { get; }

    /// <summary>The databases it failed for, and why.</summary>
    public IReadOnlyDictionary<string, Exception> Failures { get; }
}
