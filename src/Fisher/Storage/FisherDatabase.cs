using System.Data.Common;
using Fisher.Events;
using Fisher.Events.Schema;
using Fisher.Storage.Sequences;
using Microsoft.Data.Sqlite;
using Weasel.Core.Migrations;
using Weasel.Sqlite;

namespace Fisher.Storage;

/// <summary>
///     Manages a Fisher store's SQLite database: schema lifecycle through Weasel, and the neutral
///     connection surface the shared closed-shape storage runtime reads through.
/// </summary>
/// <remarks>
///     <para>
///         Connections come from a single <see cref="SqliteDataSource" /> rather than being newed up
///         per call. That is not just pooling hygiene — the data source is what applies Fisher's
///         PRAGMA settings (WAL in particular) to every connection, and for an in-memory database it
///         also holds the keep-alive connection without which the database is destroyed the moment
///         the last connection closes.
///     </para>
/// </remarks>
public partial class FisherDatabase : SqliteDatabase, Weasel.Storage.IStorageDatabase, IAsyncDisposable, IDisposable
{
    private readonly SqliteDataSource _dataSource;
    private readonly EventGraph _events;
    private readonly StoreOptions _options;

    public FisherDatabase(StoreOptions options)
        : this(options, options.ConnectionString, "Fisher")
    {
    }

    internal FisherDatabase(StoreOptions options, string connectionString, string identifier,
        string? tenantId = null)
        : base(
            new DefaultMigrationLogger(),
            options.AutoCreateSchemaObjects,
            new SqliteMigrator(),
            identifier,
            connectionString)
    {
        _options = options;
        _events = options.EventGraph;
        _dataSource = new SqliteDataSource(connectionString, options.PragmaSettings);
        TenantId = tenantId;
    }

    /// <summary>
    ///     The tenant whose data this file holds, under database-per-tenant; null otherwise.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         What the daemon needs and could not previously ask for (fisher#57). A shard's work is
    ///         addressed by <em>database</em> — that is what the <c>IEventDatabase</c> parameters
    ///         throughout <c>DocumentStore.Daemon.cs</c> carry — but every session, document write and
    ///         event append is stamped with a <em>tenant</em>. This is the one place the two are tied
    ///         together, so a batch opened for a database writes as the tenant that database belongs to.
    ///     </para>
    ///     <para>
    ///         Null rather than the default tenant id under <see cref="DefaultTenancy" />, deliberately:
    ///         there the database says nothing about tenancy — a conjoined store's tenant is a column
    ///         value and a single-tenant store has none — so answering "default" would be a claim rather
    ///         than a fact, and the daemon would start overriding a session's tenant with it.
    ///     </para>
    /// </remarks>
    internal string? TenantId { get; }

    internal EventGraph Events => _events;

    /// <summary>
    ///     The PRAGMA-applying data source every connection to this database comes from.
    /// </summary>
    internal SqliteDataSource DataSource => _dataSource;

    public override IFeatureSchema[] BuildFeatureSchemas()
    {
        var schemas = new List<IFeatureSchema> { new EventStoreFeatureSchema(_events, _options.Projections.NaturalKeys) };

        var mappings = _options.Schema.AllMappings();

        // One feature per registered document type, so a migration touches only the tables whose
        // document types actually changed. Types that were never registered are absent by design —
        // nothing knows they exist until something asks to store one.
        schemas.AddRange(mappings.Select(mapping => new DocumentFeatureSchema(mapping)));

        // Only when something actually needs a Hi-Lo allocation. See HiloFeatureSchema for why the
        // sequence does not depend on this having run.
        if (mappings.Any(x => x.IdType == typeof(int) || x.IdType == typeof(long)))
        {
            schemas.Add(new HiloFeatureSchema(_options.DatabaseSchemaName));
        }

        // A flat table has no document mapping to be discovered through — the projection itself owns
        // the table definition, which is complete by the time it was registered.
        schemas.AddRange(_options.Projections.All
            .OfType<Projections.Flattened.FlatTableProjection>()
            .Select(x => new Projections.Flattened.FlatTableFeatureSchema(x)));

        return schemas.ToArray();
    }

    /// <summary>
    ///     Open and return a connection with this store's PRAGMAs already applied.
    /// </summary>
    internal async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken token = default)
    {
        if (MigratesOnFirstUse)
        {
            await EnsureMigratedAsync(token).ConfigureAwait(false);
        }

        return (SqliteConnection)await _dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Whether this database migrates itself the first time anything opens a connection to it
    ///     (fisher#58).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Set only for a tenant database that appeared at runtime, where there was no
    ///         <c>ApplyAllConfiguredChangesToDatabaseAsync</c> at startup to have created its tables — a
    ///         tenant that shows up after the store was built has, by definition, missed it.
    ///     </para>
    ///     <para>
    ///         <b>Hung off the connection rather than off tenant resolution, because resolution is
    ///         synchronous and a migration is not.</b> <see cref="ITenancy.DatabaseFor" /> is reached from
    ///         <c>OpenSession</c>, which has no <c>await</c> to offer; opening a connection is the first
    ///         genuinely asynchronous thing that happens to a new tenant's file, and it happens before
    ///         any statement can run against it. Blocking on the migration inside <c>DatabaseFor</c>
    ///         would have been sync-over-async on the session path.
    ///     </para>
    /// </remarks>
    internal bool MigratesOnFirstUse { get; init; }

    private Task? _firstUseMigration;
    private readonly SemaphoreSlim _firstUseGate = new(1, 1);

    /// <summary>
    ///     Whether the current asynchronous flow is already inside a first-use migration (fisher#69).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The guard is about re-entrancy within one logical call — the migration opens connections
    ///         of its own, and without this it would recurse into itself — rather than about two callers
    ///         racing, which is what <c>_firstUseGate</c> below handles.
    ///     </para>
    ///     <para>
    ///         <b><see cref="AsyncLocal{T}" /> rather than <c>[ThreadStatic]</c>, because a thread-static
    ///         stops modelling a call stack the moment there is an <c>await</c> in it.</b> The flag is
    ///         set on whichever thread resumed after the semaphore wait and cleared on whichever thread
    ///         completed the migration, and those need not be the same one — which leaves the setting
    ///         thread permanently "migrating" (and since it is static, later first-use opens scheduled
    ///         onto it would skip the migration entirely and reach an empty file, surfacing as
    ///         <c>no such table</c> a long way from the cause), while continuation threads never see the
    ///         guard at all and a re-entrant open on one would block on a semaphore its own logical call
    ///         is holding — a hang rather than an error. An <see cref="AsyncLocal{T}" /> flows into the
    ///         migration's own asynchronous call graph, which is exactly the re-entrancy being guarded,
    ///         and is restored when the call returns, so it cannot poison a pooled thread.
    ///     </para>
    ///     <para>
    ///         <b>Neither failure was ever reproduced</b> — 2880 first-use migrations across concurrent
    ///         waves of fresh tenants produced none — so this is a flag made to mean what it says rather
    ///         than a fix for an observed defect. Recorded that way on purpose: the next reader should
    ///         not go looking for the bug report.
    ///     </para>
    ///     <para>
    ///         Left <c>static</c>, as the thread-static was. The recursion being guarded is this
    ///         database's migration opening connections to this database, so an instance field would be
    ///         marginally more precise; static additionally suppresses a migration for a second database
    ///         reached from inside the first, which nothing does.
    ///     </para>
    /// </remarks>
    private static readonly AsyncLocal<bool> _migrating = new();

    private async Task EnsureMigratedAsync(CancellationToken token)
    {
        if (_migrating.Value || _firstUseMigration?.IsCompletedSuccessfully == true)
        {
            return;
        }

        await _firstUseGate.WaitAsync(token).ConfigureAwait(false);

        try
        {
            if (_firstUseMigration?.IsCompletedSuccessfully == true)
            {
                return;
            }

            _migrating.Value = true;

            // Not cached until it succeeds: a migration that failed must be retried by the next caller
            // rather than remembered as done, or one transient failure leaves the tenant permanently
            // unusable with nothing to say why.
            _firstUseMigration = ApplyAllConfiguredChangesToDatabaseAsync(ct: token);

            await _firstUseMigration.ConfigureAwait(false);
        }
        finally
        {
            _migrating.Value = false;
            _firstUseGate.Release();
        }
    }

    private readonly HashSet<Type> _ensuredDocumentTables = new();
    private ClosedShape.DocumentProviderRegistry? _providers;
    private SequenceFactory? _sequences;

    /// <summary>
    ///     Create this document type's table if it is not known to exist yet.
    /// </summary>
    /// <remarks>
    ///     Snapshot types are registered by projection configuration, which can run after the schema
    ///     was last applied, so the inline projection path cannot assume its table is already there.
    ///     The set of types already handled is cached because the check would otherwise run on every
    ///     commit.
    /// </remarks>
    internal async Task EnsureDocumentTableAsync(Type documentType, CancellationToken token)
    {
        lock (_ensuredDocumentTables)
        {
            if (!_ensuredDocumentTables.Add(documentType))
            {
                return;
            }
        }

        // Registering the mapping is what puts the table into BuildFeatureSchemas; applying the whole
        // configuration then creates it. Heavier than emitting one CREATE TABLE, but it goes through
        // the same migration path as every other Fisher table instead of beside it.
        _options.Schema.MappingFor(documentType);

        await ApplyAllConfiguredChangesToDatabaseAsync(ct: token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Forget which document tables are known to exist, so the next write re-checks.
    /// </summary>
    /// <remarks>
    ///     Only <c>Advanced.Clean.CompletelyRemoveAllAsync</c> needs this: dropping the tables makes
    ///     the cache above wrong in the one direction that matters — it would let a write skip the
    ///     migration and target a table that is no longer there.
    /// </remarks>
    internal void ForgetEnsuredTables()
    {
        lock (_ensuredDocumentTables)
        {
            _ensuredDocumentTables.Clear();
        }
    }

    internal Weasel.Storage.IProviderGraph Providers
        => _providers ??= new ClosedShape.DocumentProviderRegistry(_options);

    Weasel.Storage.IProviderGraph Weasel.Storage.IStorageDatabase.Providers => Providers;

    /// <summary>
    ///     The Hi-Lo sequence backing this document type's numeric identity — the assignment strategy
    ///     Weasel offers for int and long ids.
    /// </summary>
    public Weasel.Core.Sequences.ISequence SequenceFor(Type documentType)
        => SequenceSource.SequenceFor(documentType);

    /// <summary>
    ///     This store's Hi-Lo sequences, one per logical sequence name.
    /// </summary>
    internal SequenceFactory SequenceSource => _sequences ??= new SequenceFactory(_options, _dataSource);

    DbConnection Weasel.Storage.IStorageDatabase.CreateStorageConnection() => _dataSource.CreateConnection();

    async Task Weasel.Storage.IStorageDatabase.RunSqlAsync(string sql, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = conn.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _dataSource.DisposeAsync().ConfigureAwait(false);
        ReleasePooledConnections();
    }

    /// <summary>
    ///     Return this database's pooled connections to the operating system (fisher#59).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Disposing the data source does not do this, and that was a real leak.</b>
    ///         <c>SqliteDataSource</c> is a factory rather than a pool — it builds a fresh
    ///         <see cref="SqliteConnection" /> on every open — so the pooling is
    ///         Microsoft.Data.Sqlite's own, held in a <em>process-wide</em> registry keyed by connection
    ///         string and untouched by anything Fisher disposes. Measured: 200 tenant databases resolved
    ///         but never used cost no memory and no file handles at all, while 50 that had been used
    ///         once held <b>3.4 file handles each</b> — the <c>.db</c>, <c>-wal</c> and <c>-shm</c> of one
    ///         pooled connection — and still held them after the store had been disposed.
    ///     </para>
    ///     <para>
    ///         <b>This is <c>ClearPool</c>, not <c>ClearAllPools</c>, and the distinction is the whole
    ///         reason it is safe.</b> The banned one disposes every pooled connection in the process, so
    ///         one store's cleanup takes out another's — which is why the conventions forbid it. This one
    ///         names a connection string and touches only that pool. Verified against
    ///         Microsoft.Data.Sqlite 10.0.9 that a connection <em>currently checked out</em> is unharmed:
    ///         it goes on reading and writing, and is discarded rather than re-pooled when it closes. So
    ///         a session still in flight when its store is disposed keeps working, which is the property
    ///         that would otherwise make this an <c>ObjectDisposedException</c> generator.
    ///     </para>
    ///     <para>
    ///         Two stores over one file share that pool, so disposing one releases the other's
    ///         <em>idle</em> connections too. Harmless — they are reopened on demand — and it is what
    ///         building two stores over one file already means.
    ///     </para>
    /// </remarks>
    private void ReleasePooledConnections()
    {
        try
        {
            using var pooled = new SqliteConnection(ConnectionString);
            SqliteConnection.ClearPool(pooled);
        }
        catch (Exception)
        {
            // A connection string the provider will not even parse cannot have a pool to clear, and
            // failing to tidy up must never be what makes disposal throw.
        }
    }

    /// <summary>
    ///     Synchronous disposal, for a container that disposes synchronously.
    /// </summary>
    /// <remarks>
    ///     <see cref="DisposeAsync" /> is the one to prefer, but it cannot be the only one: a
    ///     <c>ServiceProvider</c> disposed through <c>IDisposable</c> refuses outright to dispose a
    ///     service that offers only <see cref="IAsyncDisposable" />, with "type only implements
    ///     IAsyncDisposable". <c>DbDataSource</c> supplies both, so there is nothing to block on here.
    ///     Marten's <c>IDocumentStore</c> declares both for the same reason.
    /// </remarks>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _dataSource.Dispose();
        ReleasePooledConnections();
    }
}
