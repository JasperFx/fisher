using JasperFx;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fisher.EntityFrameworkCore.Tests;

/// <summary>
///     fisher#50 — an EF Core <c>DbContext</c> saving inside Fisher's transaction.
/// </summary>
/// <remarks>
///     <para>
///         <b>Structurally more important on SQLite than on either sibling.</b> One writer per file:
///         an application keeping its relational tables alongside Fisher's, in the same file, cannot
///         write both atomically without this and cannot write both at all without contending against
///         itself.
///     </para>
///     <para>
///         Verified against EF Core 9.0.14 and Microsoft.Data.Sqlite 10.0.9 before the participant was
///         written — <c>UseTransaction</c> enlists, the write is invisible to another connection, and
///         a rollback takes it with it. All four properties are re-asserted here so a provider upgrade
///         that changes one fails against this rather than against a customer.
///     </para>
/// </remarks>
public class ef_core_transactions : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("ef-core");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Shipment>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        // EF's own table, in the same file — the case the whole feature exists for. Created by hand
        // rather than by an EF migration, because what is under test is the transaction and not the
        // migration story.
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "create table if not exists Ledgers (Id integer primary key autoincrement, Note text not null)";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private static LedgerContext ContextFor(SqliteConnection connection)
        => new(new DbContextOptionsBuilder<LedgerContext>().UseSqlite(connection).Options);

    [Fact]
    public async Task fisher_and_ef_commit_together()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Shipment { Id = id, Reference = "SHP-1" });

            session.UseDbContext(connection =>
            {
                var context = ContextFor(connection);
                context.Ledgers.Add(new Ledger { Note = "posted" });

                return context;
            });

            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<Shipment>(id, Token)).ShouldNotBeNull();

        (await LedgerNotesAsync()).ShouldBe(["posted"]);
    }

    /// <remarks>
    ///     The property the feature exists for, in the direction EF controls. Two transactions on one
    ///     file could not give this: one would commit and the other would fail or wait.
    /// </remarks>
    [Fact]
    public async Task a_failure_in_ef_rolls_fishers_work_back()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Shipment { Id = id, Reference = "SHP-2" });

            session.UseDbContext(connection =>
            {
                var context = ContextFor(connection);

                // A NOT NULL violation on EF's side, raised when it saves.
                context.Ledgers.Add(new Ledger { Note = null! });

                return context;
            });

            await Should.ThrowAsync<DbUpdateException>(async () => await session.SaveChangesAsync(Token));
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<Shipment>(id, Token)).ShouldBeNull();

        (await LedgerNotesAsync()).ShouldBeEmpty();
    }

    /// <remarks>
    ///     And the other direction: a failure on Fisher's side has to take EF's write with it, or the
    ///     atomicity only runs one way.
    /// </remarks>
    [Fact]
    public async Task a_failure_in_fisher_rolls_efs_work_back()
    {
        var id = Guid.NewGuid();

        await using (var seed = _store.LightweightSession())
        {
            seed.Store(new Shipment { Id = id, Reference = "Already here" });
            await seed.SaveChangesAsync(Token);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Insert(new Shipment { Id = id, Reference = "Duplicate" });

            session.UseDbContext(connection =>
            {
                var context = ContextFor(connection);
                context.Ledgers.Add(new Ledger { Note = "never" });

                return context;
            });

            await Should.ThrowAsync<Exception>(async () => await session.SaveChangesAsync(Token));
        }

        (await LedgerNotesAsync()).ShouldBeEmpty();
    }

    /// <summary>
    ///     A <c>DbContext</c> on a second connection is detected rather than left to hang.
    /// </summary>
    /// <remarks>
    ///     The single most likely way to build this wrong, and the failure mode is the reason it is
    ///     checked: two connections to one file are two writers, and EF's would block on Fisher's from
    ///     <em>inside</em> Fisher's transaction. That is a self-deadlock — it hangs rather than
    ///     failing, so nothing would ever report it. A timeout with a clear message beats a deadlock,
    ///     and a refusal before the write beats both.
    /// </remarks>
    [Fact]
    public async Task a_context_on_a_second_connection_is_refused_by_name()
    {
        await using var elsewhere = new SqliteConnection(_database.ConnectionString);
        await elsewhere.OpenAsync(Token);

        await using var context = ContextFor(elsewhere);
        context.Ledgers.Add(new Ledger { Note = "wrong connection" });

        await using var session = _store.LightweightSession();
        session.Store(new Shipment { Id = Guid.NewGuid(), Reference = "SHP-3" });
        session.UseDbContext(context);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async ()
            => await session.SaveChangesAsync(Token));

        ex.Message.ShouldContain("different connection");
        ex.Message.ShouldContain("hangs rather than failing");

        (await LedgerNotesAsync()).ShouldBeEmpty();
    }

    /// <remarks>
    ///     The DI shape: a context the application built on Fisher's connection. It is accepted, and
    ///     Fisher does not dispose it — it did not create it, so the scope that did still can.
    /// </remarks>
    [Fact]
    public async Task an_existing_context_on_fishers_connection_is_accepted()
    {
        await using var session = _store.LightweightSession();

        // The application's own connection, which it then also gives to Fisher — the enlistment half
        // of fisher#30 meeting the participant half of fisher#50.
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var owned = _store.OpenSession(SessionOptions.ForConnection(connection));
        await using var context = ContextFor(connection);

        context.Ledgers.Add(new Ledger { Note = "from di" });

        owned.Store(new Shipment { Id = Guid.NewGuid(), Reference = "SHP-4" });
        owned.UseDbContext(context);

        await owned.SaveChangesAsync(Token);

        (await LedgerNotesAsync()).ShouldBe(["from di"]);

        // Still usable, because Fisher disposed nothing it did not create.
        context.Ledgers.Local.Count.ShouldBe(1);
    }

    /// <remarks>
    ///     <c>CompletelyRemoveAllAsync</c> filters by the <c>fi_</c> prefix and leaves EF's tables
    ///     alone. That is correct — Fisher owning the file does not make it Fisher's to clear — and it
    ///     is pinned so nobody "fixes" it.
    /// </remarks>
    [Fact]
    public async Task removing_fishers_tables_leaves_efs_alone()
    {
        await using (var session = _store.LightweightSession())
        {
            session.UseDbContext(connection =>
            {
                var context = ContextFor(connection);
                context.Ledgers.Add(new Ledger { Note = "survives" });

                return context;
            });

            session.Store(new Shipment { Id = Guid.NewGuid(), Reference = "SHP-5" });
            await session.SaveChangesAsync(Token);
        }

        await _store.Advanced.Clean.CompletelyRemoveAllAsync(Token);

        (await LedgerNotesAsync()).ShouldBe(["survives"]);
    }

    /// <summary>
    ///     A retried <c>SQLITE_BUSY</c> re-executes the write delegate, so the second attempt has to
    ///     write EF's rows too.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This was a real defect, and a silent one.</b> EF's <c>SaveChangesAsync</c> accepts its
    ///         changes when its own command succeeds, not when the enclosing transaction commits —
    ///         probed directly: an entity goes <c>Added</c> to <c>Unchanged</c> at the save and stays
    ///         <c>Unchanged</c> through a rollback. So under the default, attempt two found a context
    ///         that believed it had already saved, wrote nothing, and let Fisher commit without EF's
    ///         rows. Fisher's own work committed either way, which is what made it invisible.
    ///     </para>
    ///     <para>
    ///         The busy is planted by a second participant rather than by contending two real writers,
    ///         for the reason <c>tracing.a_busy_retry_is_recorded_on_the_span_it_contended</c> records:
    ///         real contention waits on the connection string's <c>Default Timeout</c> and never
    ///         reaches the retry. Registration order is the point — the EF participant runs first, so
    ///         it has already saved when the throw rolls the attempt back.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_retried_write_still_writes_efs_rows()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var context = ContextFor(connection);
        context.Ledgers.Add(new Ledger { Note = "survives the retry" });

        var thrower = new BusyOnce();

        await using (var session = _store.OpenSession(SessionOptions.ForConnection(connection)))
        {
            session.Store(new Shipment { Id = Guid.NewGuid(), Reference = "SHP-6" });
            session.UseDbContext(context);
            session.AddTransactionParticipant(thrower);

            await session.SaveChangesAsync(Token);
        }

        thrower.Attempts.ShouldBe(2);

        // Exactly once: written on the second attempt, and the first attempt's write rolled back with
        // its transaction rather than surviving as a duplicate.
        (await LedgerNotesAsync()).ShouldBe(["survives the retry"]);
    }

    /// <remarks>
    ///     The other half of the retry rule. The changes are held pending across attempts, so once the
    ///     commit is durable something has to say so — otherwise a context reused by its DI scope would
    ///     re-insert every row on its own next save.
    /// </remarks>
    [Fact]
    public async Task a_committed_context_has_its_changes_accepted()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var context = ContextFor(connection);
        context.Ledgers.Add(new Ledger { Note = "accepted" });

        await using (var session = _store.OpenSession(SessionOptions.ForConnection(connection)))
        {
            session.UseDbContext(context);
            session.Store(new Shipment { Id = Guid.NewGuid(), Reference = "SHP-7" });

            await session.SaveChangesAsync(Token);
        }

        context.ChangeTracker.Entries().ShouldAllBe(x => x.State == EntityState.Unchanged);

        // And saving the same context again is a no-op rather than a second insert.
        await context.SaveChangesAsync(Token);

        (await LedgerNotesAsync()).ShouldBe(["accepted"]);
    }

    private async Task<List<string>> LedgerNotesAsync()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select Note from Ledgers order by Id";

        var notes = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            notes.Add(reader.GetString(0));
        }

        return notes;
    }
}

/// <summary>
///     A participant that fails its first invocation with a transient busy, so the store's own
///     resilience pipeline retries the whole unit of work.
/// </summary>
public class BusyOnce : ITransactionParticipant
{
    public int Attempts { get; private set; }

    public Task BeforeCommitAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken token)
    {
        if (++Attempts == 1)
        {
            throw new SqliteException("database is locked", 5, 5);
        }

        return Task.CompletedTask;
    }
}

public class LedgerContext : DbContext
{
    public LedgerContext(DbContextOptions<LedgerContext> options) : base(options)
    {
    }

    public DbSet<Ledger> Ledgers => Set<Ledger>();
}

public class Ledger
{
    public int Id { get; set; }
    public string Note { get; set; } = string.Empty;
}

public class Shipment
{
    public Guid Id { get; set; }
    public string Reference { get; set; } = string.Empty;
}
