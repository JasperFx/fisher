using System.Data.Common;
using Fisher.Events;
using Fisher.Events.Schema;
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
public class FisherDatabase : SqliteDatabase, Weasel.Storage.IStorageDatabase, IAsyncDisposable
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
        var schemas = new List<IFeatureSchema> { new EventStoreFeatureSchema(_events) };

        return schemas.ToArray();
    }

    /// <summary>
    ///     Open and return a connection with this store's PRAGMAs already applied.
    /// </summary>
    internal async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken token = default)
        => (SqliteConnection)await _dataSource.OpenConnectionAsync(token).ConfigureAwait(false);

    // TODO(task 4 — document storage): both members below are the document-side half of
    // IStorageDatabase and have no event-store caller. They land with DocumentProviderRegistry and
    // the Hi-Lo sequence support.
    Weasel.Storage.IProviderGraph Weasel.Storage.IStorageDatabase.Providers
        => throw new NotImplementedException("Fisher document storage is not implemented yet.");

    public Weasel.Core.Sequences.ISequence SequenceFor(Type documentType)
        => throw new NotImplementedException("Fisher Hi-Lo sequences are not implemented yet.");

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
}
