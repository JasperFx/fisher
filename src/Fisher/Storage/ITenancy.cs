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
            _databases[tenantId] = new FisherDatabase(options, connectionString, tenantId, tenantId);
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
///     Tenants that come and go while the store is running (fisher#58, stage 3).
/// </summary>
/// <remarks>
///     <para>
///         <b>The shape stage 1 was built towards.</b> <see cref="SeparateDatabaseTenancy" /> takes its
///         tenants at configuration time and refuses everything else; this asks an
///         <see cref="ITenantSource" /> instead, so a tenant that did not exist when the store was built
///         resolves anyway — and its file and schema are created the first time anything connects to it.
///     </para>
///     <para>
///         <b>Viable here in a way it is not on either sibling.</b> Provisioning is a file plus a
///         migration; on PostgreSQL or SQL Server the same act is a CREATE DATABASE, which is why
///         Marten's and Polecat's equivalents lean on a master table an operator populates deliberately.
///     </para>
///     <para>
///         <b>Databases are cached and never evicted, and that is a known bound rather than an
///         oversight.</b> <see cref="StoreOptions.MaxPoolSize" /> sizes <em>each</em> tenant's pool, so a
///         process that has served a thousand tenants holds a thousand pools. Evicting an idle one means
///         disposing a <see cref="FisherDatabase" /> whose connections may still be in use, which is a
///         worse failure than the memory it saves — so it is measured and filed
///         (<see href="https://github.com/JasperFx/fisher/issues/59">fisher#59</see>) rather than
///         guessed at.
///     </para>
/// </remarks>
public sealed class DynamicTenancy : ITenancy
{
    private readonly StoreOptions _options;
    private readonly ITenantSource _source;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FisherDatabase> _databases =
        new(StringComparer.OrdinalIgnoreCase);

    internal DynamicTenancy(StoreOptions options, ITenantSource source)
    {
        _options = options;
        _source = source;

        // Without this a suspension only takes effect for a tenant nothing has resolved yet, because
        // DatabaseFor answers from the cache below and never asks the source again — see
        // ITenantSource.OnTenantRevoked. Disposed synchronously rather than through
        // ForgetTenantAsync's async path, because a source revokes from a synchronous method; the
        // difference is only that a pooled connection's close is not awaited, and clearing the pool
        // leaves a checked-out connection working either way.
        source.OnTenantRevoked = tenantId =>
        {
            if (_databases.TryRemove(tenantId, out var database))
            {
                database.Dispose();
            }
        };
    }

    /// <summary>
    ///     The source this tenancy asks. Exposed so an application can reach a source it did not keep a
    ///     reference to — <see cref="MasterTableTenantSource" />'s runtime lifecycle in particular.
    /// </summary>
    public ITenantSource Source => _source;

    public DatabaseCardinality Cardinality => DatabaseCardinality.DynamicMultiple;

    /// <summary>
    ///     The database a store-level operation uses when no tenant is named.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The default tenant's, resolved like any other — there is no store-level file under this
    ///         tenancy, so a store-level connection string would name a database nothing writes to.
    ///     </para>
    ///     <para>
    ///         <b>A source therefore has to be able to answer for the default tenant</b>, and this is
    ///         read while the store is being built rather than lazily, so a source that cannot says so
    ///         at construction instead of at the first store-level operation.
    ///         <see cref="DirectoryTenantSource" /> answers for any tenant id and needs nothing;
    ///         <see cref="InMemoryTenantSource" /> has to have it registered.
    ///     </para>
    /// </remarks>
    public FisherDatabase Default
    {
        get
        {
            if (_source.TryFind(StorageConstants.DefaultTenantId, out var registration))
            {
                return Register(registration);
            }

            throw new InvalidOperationException(
                $"This store's ITenantSource does not know the default tenant '{StorageConstants.DefaultTenantId}', "
                + "and a database-per-tenant store has no store-level file to fall back on — so there is "
                + "nothing for an operation that names no tenant to run against. Register it "
                + $"(source.Add(\"{StorageConstants.DefaultTenantId}\", connectionString)), seed it "
                + "under MultiTenantedDatabasesInRegistry(...) with "
                + "SeedDatabases.RegisterDefault(connectionString), or use "
                + "MultiTenantedDatabasesInDirectory(...), whose convention answers for any tenant id.");
        }
    }

    /// <remarks>
    ///     An unknown tenant still throws, and a suspended one throws something different. Falling back
    ///     to another tenant's file is the one failure this tenancy exists to make impossible, and the
    ///     two failures are told apart because "switched off" and "never heard of it" are different
    ///     operational situations.
    /// </remarks>
    public FisherDatabase DatabaseFor(string tenantId)
    {
        if (_databases.TryGetValue(tenantId, out var cached))
        {
            return cached;
        }

        if (!_source.TryFind(tenantId, out var registration))
        {
            throw new UnknownTenantException(tenantId, _databases.Keys);
        }

        if (!registration.IsActive)
        {
            throw new DisabledTenantException(tenantId);
        }

        return Register(registration);
    }

    /// <summary>
    ///     Pull every tenant the source knows about into this store, creating a database for each.
    /// </summary>
    /// <remarks>
    ///     What the daemon calls to find tenants that have appeared since it started, and what
    ///     <c>AllDatabases</c> reports from. Sessions do not need it — <see cref="DatabaseFor" /> resolves
    ///     a tenant the moment it is asked for.
    /// </remarks>
    public async ValueTask RefreshAsync(CancellationToken token = default)
    {
        foreach (var registration in await _source.AllAsync(token).ConfigureAwait(false))
        {
            if (registration.IsActive)
            {
                Register(registration);
            }
        }
    }

    private FisherDatabase Register(TenantRegistration registration)
        => _databases.GetOrAdd(registration.TenantId, _ => new FisherDatabase(_options,
            registration.ConnectionString, registration.TenantId, registration.TenantId)
        {
            // Nothing applied this file's schema at startup, because it may not have existed then.
            MigratesOnFirstUse = true
        });

    /// <remarks>
    ///     Only the tenants this store has actually resolved. <see cref="RefreshAsync" /> is what makes
    ///     that the whole set the source knows about.
    /// </remarks>
    public IReadOnlyList<FisherDatabase> AllDatabases() => _databases.Values.ToList();

    /// <summary>
    ///     Release a tenant this process is finished with, returning its pooled connections to the
    ///     operating system (fisher#59).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Explicit rather than automatic, and the measurement is why.</b> A tenant resolved but
    ///         never used costs no memory and no file handles — so evicting on idleness would buy
    ///         nothing for the case it was imagined for. What does cost is a tenant that has been
    ///         <em>used</em>: Microsoft.Data.Sqlite keeps a pooled connection per connection string, and
    ///         that is three file handles apiece (<c>.db</c>, <c>-wal</c>, <c>-shm</c>). A process
    ///         serving thousands of tenants over its lifetime accumulates them.
    ///     </para>
    ///     <para>
    ///         A timer cannot know whether a tenant is finished or merely quiet, and re-resolving one is
    ///         nearly free, so the judgement is left with the caller who actually knows — the shape
    ///         fisher#59 called option two. Nothing breaks if it is never called: this is a way to
    ///         return resources early, not a requirement.
    ///     </para>
    ///     <para>
    ///         <b>Safe against a session still using the tenant.</b> Disposal clears that connection
    ///         string's pool, which leaves a checked-out connection working and merely stops it being
    ///         re-pooled — see <see cref="FisherDatabase.Dispose" />. The tenant resolves again on the
    ///         next request; its file and data are untouched, as they are everywhere else here.
    ///     </para>
    /// </remarks>
    /// <returns>False when this store had not resolved that tenant.</returns>
    public async ValueTask<bool> ForgetTenantAsync(string tenantId)
    {
        if (!_databases.TryRemove(tenantId, out var database))
        {
            return false;
        }

        await database.DisposeAsync().ConfigureAwait(false);

        return true;
    }

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
