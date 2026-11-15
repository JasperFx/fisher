using Fisher.Linq;
using Fisher.Tests.Events;
using Fisher.Tests.Projections;
using JasperFx;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#468: an Aspire AppHost using the Community Toolkit's <c>AddSqlite</c> hands a project a connection
///     string of its own shape, and the docs said <c>AddFisher(connectionString)</c> takes it without having checked.
/// </summary>
/// <remarks>
///     <para>
///         CommunityToolkit.Aspire.Hosting.Sqlite 13.6.1 (with Aspire.Hosting 13.6.4) produces
///         <c>Data Source={path};Cache=Shared;Mode=ReadWriteCreate</c>, measured from the resource's own
///         connection-string expression. The string is reproduced here rather than the package referenced,
///         because what Fisher has to cope with is the string, and a toolkit version should not be a test dependency.
///     </para>
///     <para>
///         <b><c>Cache=Shared</c> is the part worth testing.</b> SQLite discourages shared-cache mode beside WAL, and
///         in shared-cache mode connections in one process lock per table and report <c>SQLITE_LOCKED</c> rather than
///         waiting out <c>busy_timeout</c>. So the facts below are the ones that would break: the store keeps WAL, and
///         concurrent writers with the daemon reading beside them all commit.
///     </para>
/// </remarks>
public class community_toolkit_connection_string : IAsyncLifetime
{
    private const string ToolkitOptions = ";Cache=Shared;Mode=ReadWriteCreate";

    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("community-toolkit");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string ToolkitConnectionString => _database.ConnectionString + ToolkitOptions;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = ToolkitConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<AsyncQuestTally>(SnapshotLifecycle.Async);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();

        // The shared-cache string is its own pool; TemporaryDatabase only clears the plain one.
        using (var pooled = new SqliteConnection(ToolkitConnectionString))
        {
            SqliteConnection.ClearPool(pooled);
        }

        _database.Dispose();
    }

    [Fact]
    public void the_toolkit_string_is_one_microsoft_data_sqlite_reads_as_a_shared_cache_file()
    {
        var builder = new SqliteConnectionStringBuilder(ToolkitConnectionString);

        builder.DataSource.ShouldBe(new SqliteConnectionStringBuilder(_database.ConnectionString).DataSource);
        builder.Cache.ShouldBe(SqliteCacheMode.Shared);
        builder.Mode.ShouldBe(SqliteOpenMode.ReadWriteCreate);
    }

    /// <remarks>
    ///     WAL is applied by the store's data source on its first connection, so the file is checked after a write
    ///     through the store. Read straight after <c>ApplyAllConfiguredChangesToDatabaseAsync</c> it still said
    ///     <c>delete</c>.
    /// </remarks>
    [Fact]
    public async Task the_store_still_runs_the_file_in_wal()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream<AsyncQuestTally>(Guid.NewGuid(), new MemberJoined("Sam"));
            await session.SaveChangesAsync(Token);
        }

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "pragma journal_mode";

        ((string)(await command.ExecuteScalarAsync(Token))!).ShouldBe("wal");
    }

    [Fact]
    public async Task concurrent_writers_commit_while_the_daemon_reads()
    {
        using var daemon = await _store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        try
        {
            const int writers = 8;
            const int appendsEach = 25;

            var streams = await Task.WhenAll(Enumerable.Range(0, writers).Select(_ => Task.Run(async () =>
            {
                var ids = new List<Guid>();
                for (var i = 0; i < appendsEach; i++)
                {
                    var id = Guid.NewGuid();
                    await using var session = _store.LightweightSession();
                    session.Events.StartStream<AsyncQuestTally>(id, new MemberJoined("Frodo"), new MonsterSlain("Orc"));
                    await session.SaveChangesAsync(Token);
                    ids.Add(id);
                }

                return ids;
            }, Token)));

            await _store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

            await using var query = _store.LightweightSession();
            var tallies = await query.Query<AsyncQuestTally>().CountAsync(Token);

            tallies.ShouldBe(streams.Sum(x => x.Count));
            tallies.ShouldBe(writers * appendsEach);
        }
        finally
        {
            await daemon.StopAllAsync();
        }
    }
}
