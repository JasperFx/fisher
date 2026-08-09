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

    internal FisherDatabase(StoreOptions options, string connectionString, string identifier)
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
    }

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
        => (SqliteConnection)await _dataSource.OpenConnectionAsync(token).ConfigureAwait(false);

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
    }
}
