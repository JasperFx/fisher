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
