using System.Data.Common;
using System.Diagnostics;
using Fisher.Storage;
using JasperFx;
using Microsoft.Data.Sqlite;
using Weasel.Sqlite;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#547 — every connection Fisher opens waits for the write lock through a 1 ms busy handler
///     instead of SQLite's default schedule, with the same <c>busy_timeout</c> deadline.
/// </summary>
/// <remarks>
///     The handler runs on the thread stepping the statement, so a synchronous <c>BEGIN IMMEDIATE</c>
///     counts its own waits in <see cref="SqliteBusyHandler.CallsOnThisThread" />. SQLite's default
///     handler does not touch that count, so a positive count is the handler being installed.
/// </remarks>
public class sqlite_busy_handler : IAsyncLifetime
{
    private const int ShortTimeoutMs = 200;

    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("busy-handler");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var pragmas = SqlitePragmaSettings.Default;
        pragmas.BusyTimeout = ShortTimeoutMs;

        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.PragmaSettings = pragmas;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public void the_handler_is_available_with_the_bundled_e_sqlite3()
    {
        SqliteBusyHandler.IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public async Task a_contended_write_waits_through_the_handler_and_still_gives_up_at_the_busy_timeout()
    {
        await using var holder = await _store.Database.OpenConnectionAsync(Token);
        await execute(holder, "BEGIN IMMEDIATE");

        await using var waiter = await _store.Database.OpenConnectionAsync(Token);

        var before = SqliteBusyHandler.CallsOnThisThread;
        var stopwatch = Stopwatch.StartNew();

        // Microsoft.Data.Sqlite retries SQLITE_BUSY itself until the command timeout, and every retry
        // is a new wait for the handler, so this gives up after about a second, never at 200 ms.
        var ex = Should.Throw<SqliteException>(() =>
        {
            using var command = waiter.CreateCommand();
            command.CommandText = "BEGIN IMMEDIATE";
            command.CommandTimeout = 1;
            command.ExecuteNonQuery();
        });

        stopwatch.Stop();

        ex.SqliteErrorCode.ShouldBe(5); // SQLITE_BUSY: the deadline still applies
        (SqliteBusyHandler.CallsOnThisThread - before).ShouldBeGreaterThan(0);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));

        await execute(holder, "ROLLBACK");
    }

    [Fact]
    public async Task a_contended_write_gets_the_lock_once_the_holder_commits()
    {
        var pragmas = SqlitePragmaSettings.Default;
        pragmas.BusyTimeout = 5000;

        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.PragmaSettings = pragmas;
        });

        await using var holder = await store.Database.OpenConnectionAsync(Token);
        await execute(holder, "BEGIN IMMEDIATE");

        await using var waiter = await store.Database.OpenConnectionAsync(Token);

        var release = Task.Run(async () =>
        {
            await Task.Delay(100, Token);
            await execute(holder, "COMMIT");
        }, Token);

        using (var command = waiter.CreateCommand())
        {
            command.CommandText = "BEGIN IMMEDIATE";
            command.ExecuteNonQuery();
        }

        await release;
        await execute(waiter, "COMMIT");
    }

    [Fact]
    public async Task a_busy_timeout_of_zero_keeps_sqlites_own_behaviour()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        SqliteBusyHandler.TryInstall(connection, 0).ShouldBeFalse();
    }

    private static async Task execute(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
    }
}
