using System.Data;
using Fisher.Linq;
using JasperFx;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Configuration;

/// <summary>
///     <see cref="SessionOptions" /> and the session factories on the store — fisher#30.
/// </summary>
/// <remarks>
///     <para>
///         Most of this file is about <b>enlistment</b>, which is worth more on SQLite than the same
///         feature is on either sibling: one writer per file, and an application's own tables live in
///         that file, so "my rows and Fisher's, or neither" is otherwise reachable only by taking the
///         write lock twice and contending with yourself.
///     </para>
///     <para>
///         Several of the assertions here are about the provider rather than about Fisher — which
///         isolation levels mean anything, what a command timeout bounds. Those are pinned because the
///         design rests on them and because a provider upgrade that changed one would otherwise
///         present as a concurrency guard that quietly stopped guarding.
///     </para>
/// </remarks>
public class session_options : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("session-options");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Angler>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await ExecuteAsync("create table ledger (id integer primary key, note text)");
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    // ---- the session factories ----

    [Fact]
    public async Task the_store_hands_out_a_query_session()
    {
        var id = Guid.NewGuid();

        await using (var writer = _store.LightweightSession())
        {
            writer.Store(new Angler { Id = id, Name = "Frodo" });
            await writer.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var session = _store.QuerySession();
        (await session.LoadAsync<Angler>(id, TestContext.Current.CancellationToken))
            .ShouldNotBeNull().Name.ShouldBe("Frodo");
    }

    /// <summary>
    ///     The narrowing is a convention rather than a guarantee, and the issue asked for that to be
    ///     decided rather than left implied. It is decided: this is documented behaviour, pinned here
    ///     so that "make the narrowing real" is a change somebody makes deliberately.
    /// </summary>
    [Fact]
    public void a_query_session_is_the_same_session_type_narrowed()
    {
        using var session = _store.QuerySession();

        session.ShouldBeAssignableTo<IDocumentSession>();
    }

    [Fact]
    public void session_options_carry_the_tenant()
    {
        using var session = _store.OpenSession(SessionOptions.ForTenant("blue"));

        session.TenantId.ShouldBe("blue");
    }

    // ---- validation ----

    /// <summary>
    ///     The one isolation level that would change anything, and the reason it is refused: it begins
    ///     a <em>deferred</em> transaction, which does not take the write lock — and the append path
    ///     reads a stream's version and writes version+1 on the strength of holding it. Nothing would
    ///     report the loss, because the transaction still describes itself as Serializable.
    /// </summary>
    [Fact]
    public void read_uncommitted_is_refused()
    {
        var options = new SessionOptions { IsolationLevel = IsolationLevel.ReadUncommitted };

        Should.Throw<ArgumentOutOfRangeException>(() => _store.OpenSession(options))
            .Message.ShouldContain("write lock");
    }

    /// <summary>
    ///     The levels Polecat code is likely to be carrying. All of them produce the same
    ///     <c>BEGIN IMMEDIATE</c> on Microsoft.Data.Sqlite, which is what makes carrying the property
    ///     for parity honest rather than decorative.
    /// </summary>
    [Theory]
    [InlineData(IsolationLevel.Unspecified)]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task the_levels_sqlite_treats_alike_are_all_accepted(IsolationLevel level)
    {
        await using var session = _store.OpenSession(new SessionOptions { IsolationLevel = level });

        session.Store(new Angler { Id = Guid.NewGuid(), Name = "Sam" });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_transaction_that_has_already_been_committed_is_refused()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);

        Should.Throw<ArgumentException>(() => _store.OpenSession(SessionOptions.ForTransaction(transaction)))
            .Message.ShouldContain("already been committed");
    }

    [Fact]
    public async Task a_connection_that_is_not_the_transactions_own_is_refused()
    {
        await using var one = new SqliteConnection(_database.ConnectionString);
        await one.OpenAsync(TestContext.Current.CancellationToken);
        await using var two = new SqliteConnection(_database.ConnectionString);
        await two.OpenAsync(TestContext.Current.CancellationToken);

        await using var transaction = (SqliteTransaction)await one.BeginTransactionAsync(
            IsolationLevel.Serializable, TestContext.Current.CancellationToken);

        var options = new SessionOptions { Connection = two, Transaction = transaction };

        Should.Throw<ArgumentException>(() => _store.OpenSession(options))
            .Message.ShouldContain("not the connection");
    }

    // ---- a borrowed connection ----

    [Fact]
    public async Task a_session_runs_on_a_connection_it_was_handed()
    {
        var id = Guid.NewGuid();

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using (var session = _store.OpenSession(SessionOptions.ForConnection(connection)))
        {
            session.Store(new Angler { Id = id, Name = "Merry" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // The session opened and committed its own transaction on the borrowed connection, so the row
        // is visible to everyone — and the connection is still usable, which is the other half.
        (await ScalarAsync("select count(*) from fi_doc_angler")).ShouldBe(1L);
        connection.State.ShouldBe(ConnectionState.Open);
    }

    /// <summary>
    ///     Disposing a connection the session was handed would close it out from under the caller, who
    ///     is usually about to go on using it. Verified by disposing the session first and then reading
    ///     through the same connection.
    /// </summary>
    [Fact]
    public async Task disposing_the_session_does_not_dispose_a_borrowed_connection()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using (var session = _store.OpenSession(SessionOptions.ForConnection(connection)))
        {
            session.Store(new Angler { Id = Guid.NewGuid(), Name = "Pippin" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from fi_doc_angler";
        (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(1L);
    }

    // ---- an enlisted transaction: the feature ----

    /// <summary>
    ///     The whole point of fisher#30 on SQLite. The application's own insert and Fisher's document
    ///     and event writes commit as one transaction over one file — with one acquisition of the
    ///     single write lock, rather than two that would contend with each other.
    /// </summary>
    [Fact]
    public async Task an_enlisted_session_commits_with_the_callers_own_writes()
    {
        var id = Guid.NewGuid();

        await using (var connection = new SqliteConnection(_database.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.Serializable, TestContext.Current.CancellationToken);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "insert into ledger (id, note) values (1, 'landed')";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await using (var session = _store.OpenSession(SessionOptions.ForTransaction(transaction)))
            {
                session.Store(new Angler { Id = id, Name = "Frodo" });
                session.Events.StartStream<Angler>(id, new AnglerLanded("Trout"));
                await session.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            // Nothing is visible to anybody else yet: SaveChangesAsync wrote, and the commit is ours.
            (await ScalarAsync("select count(*) from fi_doc_angler")).ShouldBe(0L);

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        (await ScalarAsync("select count(*) from ledger")).ShouldBe(1L);
        (await ScalarAsync("select count(*) from fi_doc_angler")).ShouldBe(1L);
        (await ScalarAsync("select count(*) from fi_events")).ShouldBe(1L);
    }

    /// <summary>
    ///     The other direction, and the one that makes the guarantee worth anything: the caller rolls
    ///     back, and Fisher's writes go with theirs.
    /// </summary>
    [Fact]
    public async Task an_enlisted_sessions_writes_roll_back_with_the_callers_transaction()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, TestContext.Current.CancellationToken);

        await using (var session = _store.OpenSession(SessionOptions.ForTransaction(transaction)))
        {
            session.Store(new Angler { Id = Guid.NewGuid(), Name = "Gollum" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);

        (await ScalarAsync("select count(*) from fi_doc_angler")).ShouldBe(0L);
    }

    /// <summary>
    ///     A read through an enlisted session joins the caller's transaction, so it sees what has been
    ///     written into it and nobody outside can.
    /// </summary>
    /// <remarks>
    ///     Removing <c>ConfigureCommandAsync</c>'s transaction assignment does not make this read
    ///     around the transaction — it makes it throw, which is a better failure than the one that was
    ///     expected. Microsoft.Data.Sqlite refuses a command with no <c>Transaction</c> on a connection
    ///     that has a pending local transaction, and every Fisher statement is a detached command from
    ///     Weasel's builder rather than one from <c>connection.CreateCommand()</c>, which would have
    ///     inherited it. Six tests in this class fail with that message without the line.
    /// </remarks>
    [Fact]
    public async Task an_enlisted_session_reads_the_callers_uncommitted_writes()
    {
        var id = Guid.NewGuid();

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, TestContext.Current.CancellationToken);

        await using var session = _store.OpenSession(SessionOptions.ForTransaction(transaction));
        session.Store(new Angler { Id = id, Name = "Bilbo" });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await session.LoadAsync<Angler>(id, TestContext.Current.CancellationToken))
            .ShouldNotBeNull().Name.ShouldBe("Bilbo");

        (await session.Query<Angler>().CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     A session that never commits must not dispose the transaction or the connection it borrowed
    ///     — the caller still has both to commit through.
    /// </summary>
    [Fact]
    public async Task disposing_an_enlisted_session_leaves_the_transaction_usable()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, TestContext.Current.CancellationToken);

        await using (var session = _store.OpenSession(SessionOptions.ForTransaction(transaction)))
        {
            session.Store(new Angler { Id = Guid.NewGuid(), Name = "Gimli" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "insert into ledger (id, note) values (2, 'after')";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await transaction.CommitAsync(TestContext.Current.CancellationToken);

        (await ScalarAsync("select count(*) from ledger")).ShouldBe(1L);
        (await ScalarAsync("select count(*) from fi_doc_angler")).ShouldBe(1L);
    }

    /// <summary>
    ///     On-demand table creation runs a migration on its own connection, which would block against
    ///     the write lock the caller's transaction is holding — a session deadlocking against itself,
    ///     presenting after thirty seconds as "database is locked". Naming the type and the fix
    ///     immediately is the whole of the divergence.
    /// </summary>
    [Fact]
    public async Task an_enlisted_session_will_not_create_a_missing_document_table()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, TestContext.Current.CancellationToken);

        await using var session = _store.OpenSession(SessionOptions.ForTransaction(transaction));
        session.Store(new Quarry { Id = Guid.NewGuid(), Species = "Pike" });

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => session.SaveChangesAsync(TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain(nameof(Quarry));
        thrown.Message.ShouldContain("ApplyAllConfiguredChangesToDatabaseAsync");

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     The check runs on the caller's own connection, so a table created inside the very same
    ///     transaction counts as existing. That is what makes "create your tables and Fisher's in one
    ///     transaction" work rather than being refused on a technicality.
    /// </summary>
    [Fact]
    public async Task a_table_created_inside_the_callers_transaction_counts_as_existing()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, TestContext.Current.CancellationToken);

        // The DDL Fisher would have run, inside the caller's transaction — SQLite's DDL is
        // transactional, so this is a real thing for an application to do.
        await using (var ddl = connection.CreateCommand())
        {
            ddl.Transaction = transaction;
            ddl.CommandText =
                "create table fi_doc_quarry (id text not null primary key, data text not null, "
                + "dotnet_type text, last_modified text)";
            await ddl.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = _store.OpenSession(SessionOptions.ForTransaction(transaction)))
        {
            session.Store(new Quarry { Id = Guid.NewGuid(), Species = "Pike" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await transaction.CommitAsync(TestContext.Current.CancellationToken);

        (await ScalarAsync("select count(*) from fi_doc_quarry")).ShouldBe(1L);
    }

    public record AnglerLanded(string Species);

    public class Angler
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    /// <summary>
    ///     Deliberately never registered with the store, so its table does not exist until something
    ///     creates it — which is the case the enlisted session has to refuse rather than deadlock on.
    /// </summary>
    public class Quarry
    {
        public Guid Id { get; set; }
        public string Species { get; set; } = string.Empty;
    }
}
