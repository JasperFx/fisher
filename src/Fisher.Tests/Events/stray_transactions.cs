using Fisher.Linq;
using JasperFx;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Fisher.Tests.Events;

/// <summary>
///     A native SQLite transaction that <c>SqliteTransaction</c> has lost track of does not escape as
///     <c>SQLite Error 1: 'cannot start a transaction within a transaction'</c> (fisher#311).
/// </summary>
/// <remarks>
///     <para>
///         <b>fisher#311 was an unreproduced intermittent — one full-suite net9.0 run — and asked for
///         the mechanism to be confirmed before anything was fixed, because its two candidate
///         mechanisms wanted different fixes.</b> The first test here is that confirmation, and it is
///         deterministic: the pool path reproduces the reported exception exactly, with no contention
///         and no timing.
///     </para>
///     <para>
///         <b>Why it matters rather than being a curiosity.</b> <c>SQLITE_ERROR</c> (1) is not
///         transient, so <c>FisherResilienceDefaults.IsTransient</c> declines it — correctly — and it
///         escapes <c>SaveChangesAsync</c> raw. A caller catching <c>DcbConcurrencyException</c> or
///         <c>StreamLockedException</c> and retrying, which is the shape
///         <c>concurrent_boundary_appends</c> exists to certify, gets an unhandled exception instead of
///         the failure they wrote the retry loop against. fisher#306's translation is gated on
///         transience and deliberately does not reach this.
///     </para>
///     <para>
///         <b>The state is planted with raw SQL rather than raced into existence</b>, which is the only
///         honest way to test it: 12 sequential runs of <c>concurrent_boundary_appends</c> and 24
///         concurrent ones never reproduced it, so a test that waits for the race would be green
///         whether the fix worked or not.
///     </para>
/// </remarks>
public class stray_transactions : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("stray-transactions");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Ledger>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    /// <summary>
    ///     The premise, stated as a fact about Microsoft.Data.Sqlite rather than about Fisher.
    /// </summary>
    /// <remarks>
    ///     A connection carrying a native transaction nobody tracks goes back to the pool and is handed
    ///     to the next caller as-is. Without this, the two tests below would be asserting against a
    ///     hazard that might not exist, and the fix would be superstition.
    /// </remarks>
    [Fact]
    public async Task a_pooled_connection_carries_an_untracked_transaction_forward()
    {
        await using (var first = new SqliteConnection(_database.ConnectionString))
        {
            await first.OpenAsync(Token);

            await using var command = first.CreateCommand();
            command.CommandText = "begin immediate";
            await command.ExecuteNonQueryAsync(Token);
        }

        await using var second = new SqliteConnection(_database.ConnectionString);
        await second.OpenAsync(Token);

        var refusal = await Should.ThrowAsync<SqliteException>(
            async () => await second.BeginTransactionAsync(Token));

        refusal.Message.ShouldContain("cannot start a transaction within a transaction");
    }

    /// <summary>
    ///     The fix at the source: a session never returns a dirty connection to the pool.
    /// </summary>
    /// <remarks>
    ///     Fails against the previous build with the exact exception fisher#311 reported — which is what
    ///     makes this the regression guard rather than
    ///     <c>barrier_synced_racers_on_one_boundary_serialize_to_one_winner</c>, whose failure needed
    ///     whole-suite conditions nobody could reproduce.
    /// </remarks>
    [Fact]
    public async Task a_session_does_not_return_a_dirty_connection_to_the_pool()
    {
        await using (var dirty = _store.LightweightSession())
        {
            // Reach the session's own connection and leave a transaction on it that SqliteTransaction
            // knows nothing about — the state a rollback losing the write lock produces.
            await using var command = (await ConnectionOf(dirty)).CreateCommand();
            command.CommandText = "begin immediate";
            await command.ExecuteNonQueryAsync(Token);
        }

        // The next session out of the pool commits normally.
        await using var session = _store.LightweightSession();
        session.Store(new Ledger { Id = Guid.NewGuid(), Note = "after" });
        await session.SaveChangesAsync(Token);

        await using var query = _store.QuerySession();
        (await query.Query<Ledger>().ToListAsync(Token)).ShouldHaveSingleItem().Note.ShouldBe("after");
    }

    /// <summary>
    ///     Defence in depth: a connection that arrives dirty anyway is recovered rather than refused.
    /// </summary>
    /// <remarks>
    ///     This is the retry path fisher#311 could not tell apart from the pool one — attempt N's
    ///     rollback losing the write lock, leaving attempt N+1's <c>BEGIN</c> to fail. It cannot be
    ///     raced into existence either, so the dirty state is planted on the session's own connection
    ///     between the read and the commit.
    /// </remarks>
    [Fact]
    public async Task a_commit_recovers_from_a_connection_that_is_already_in_a_transaction()
    {
        await using var session = _store.LightweightSession();

        await using (var command = (await ConnectionOf(session)).CreateCommand())
        {
            command.CommandText = "begin immediate";
            await command.ExecuteNonQueryAsync(Token);
        }

        session.Store(new Ledger { Id = Guid.NewGuid(), Note = "recovered" });
        await session.SaveChangesAsync(Token);

        await using var query = _store.QuerySession();
        (await query.Query<Ledger>().ToListAsync(Token)).ShouldHaveSingleItem().Note.ShouldBe("recovered");
    }

    /// <summary>
    ///     An ordinary commit is unaffected — the clearing probe's refusal is the common case and is
    ///     swallowed.
    /// </summary>
    [Fact]
    public async Task an_ordinary_unit_of_work_is_untouched()
    {
        for (var i = 0; i < 3; i++)
        {
            await using var session = _store.LightweightSession();
            session.Store(new Ledger { Id = Guid.NewGuid(), Note = $"row-{i}" });
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.QuerySession();
        (await query.Query<Ledger>().ToListAsync(Token)).Count.ShouldBe(3);
    }

    /// <summary>
    ///     The session's own <c>SqliteConnection</c>, which is not otherwise reachable.
    /// </summary>
    private static async Task<SqliteConnection> ConnectionOf(IQuerySession session)
    {
        var fisherSession = (Fisher.Internal.FisherSession)session;
        return await fisherSession.ConnectionAsync(Token);
    }
}

public class Ledger
{
    public Guid Id { get; set; }
    public string Note { get; set; } = string.Empty;
}
