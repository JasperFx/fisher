using System.Data;
using System.Diagnostics;
using Fisher.Tests.Documents;
using JasperFx;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#454. A Hi-Lo identity reaches <c>fi_hilo</c> on a connection of its own when the current
///     block runs out. Under a session enlisted in a caller's transaction that holds the file's write
///     lock, that connection waits on the caller's lock, and the caller is waiting on it: storing an
///     int-keyed document used to block for minutes and then fail with <c>SQLITE_BUSY</c>.
/// </summary>
public class enlisted_session_hilo : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("enlisted-hilo");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Tally>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<(SqliteConnection, SqliteTransaction)> BeginCallerTransactionAsync()
    {
        var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        // Serializable is BEGIN IMMEDIATE: the caller holds the write lock from here on
        var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, Token);

        return (connection, transaction);
    }

    [Fact]
    public async Task an_identity_that_needs_a_new_block_is_refused_at_once()
    {
        var (connection, transaction) = await BeginCallerTransactionAsync();
        await using var _ = connection;
        await using var __ = transaction;

        await using var session = _store.OpenSession(SessionOptions.ForTransaction(transaction));

        var stopwatch = Stopwatch.StartNew();
        var failure = Should.Throw<InvalidOperationException>(() => session.Store(new Tally { Count = 1 }));
        stopwatch.Stop();

        failure.Message.ShouldContain(nameof(Tally));
        failure.Message.ShouldContain("SessionOptions.ForTransaction");

        // Refused, not waited out: the busy timeout alone is seconds
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }

    /// <summary>
    ///     An id the current block can still supply needs no I/O, so it is not refused.
    /// </summary>
    [Fact]
    public async Task an_identity_from_the_current_block_is_still_assigned_and_commits_with_the_caller()
    {
        await using (var warmUp = _store.LightweightSession())
        {
            warmUp.Store(new Tally { Count = 0 });
            await warmUp.SaveChangesAsync(Token);
        }

        var (connection, transaction) = await BeginCallerTransactionAsync();
        await using (connection)
        {
            var tally = new Tally { Count = 1 };

            await using (var session = _store.OpenSession(SessionOptions.ForTransaction(transaction)))
            {
                session.Store(tally);
                await session.SaveChangesAsync(Token);
            }

            tally.Id.ShouldBeGreaterThan(0);

            await transaction.CommitAsync(Token);
            await transaction.DisposeAsync();
        }

        await using var query = _store.QuerySession();
        (await query.LoadAsync<Tally>(2, Token)).ShouldNotBeNull().Count.ShouldBe(1);
    }

    /// <summary>
    ///     The refusal belongs to the enlisted session's flow, not to the sequence: an ordinary session
    ///     on the same store still reserves a block afterwards.
    /// </summary>
    [Fact]
    public async Task an_ordinary_session_still_reserves_a_block_afterwards()
    {
        var (connection, transaction) = await BeginCallerTransactionAsync();
        await using (connection)
        {
            await using (var session = _store.OpenSession(SessionOptions.ForTransaction(transaction)))
            {
                Should.Throw<InvalidOperationException>(() => session.Store(new Tally { Count = 1 }));
            }

            await transaction.RollbackAsync(Token);
            await transaction.DisposeAsync();
        }

        await using var ordinary = _store.LightweightSession();
        var tally = new Tally { Count = 2 };
        ordinary.Store(tally);
        await ordinary.SaveChangesAsync(Token);

        tally.Id.ShouldBe(1);
    }
}
