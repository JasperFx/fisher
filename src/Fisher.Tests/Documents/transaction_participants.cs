using JasperFx;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Documents;

/// <summary>
///     fisher#50, half one — <see cref="ITransactionParticipant" />: something else writing inside
///     Fisher's transaction and committing with it.
/// </summary>
/// <remarks>
///     <para>
///         <b>More important on SQLite than on either sibling, and structurally so.</b> One writer per
///         database file. An application using Fisher for events and something else for its relational
///         tables — in the same file, which is the natural thing to do with an embedded database —
///         cannot write both atomically without this, and cannot write both <em>at all</em> without
///         contending against itself: the two transactions are two writers on one file.
///     </para>
///     <para>
///         The inverse of <c>SessionOptions.ForTransaction</c>, and both are worth having: that one
///         lets a caller hand Fisher a transaction they own, this lets a participant join one Fisher
///         owns. Which fits depends on the participant.
///     </para>
/// </remarks>
public class transaction_participants : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("participants");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Invoice>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        // The application's own table, in the same file — which is the case this feature exists for.
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "create table if not exists ledger (id integer primary key, note text not null)";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task a_participant_writes_in_fishers_transaction()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Invoice { Id = id, Reference = "INV-1" });
            session.AddTransactionParticipant(new LedgerWriter("posted"));

            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<Invoice>(id, Token)).ShouldNotBeNull();

        (await LedgerNotesAsync()).ShouldBe(["posted"]);
    }

    /// <remarks>
    ///     The property the whole feature exists for. Two transactions on one file could not give this
    ///     — one of them would commit and the other would fail or wait.
    /// </remarks>
    [Fact]
    public async Task a_participant_that_throws_rolls_fishers_work_back()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Invoice { Id = id, Reference = "INV-2" });
            session.AddTransactionParticipant(new LedgerWriter("never", thenThrow: true));

            await Should.ThrowAsync<InvalidOperationException>(async () => await session.SaveChangesAsync(Token));
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<Invoice>(id, Token)).ShouldBeNull();

        (await LedgerNotesAsync()).ShouldBeEmpty();
    }

    /// <remarks>
    ///     A failure on Fisher's side has to take the participant's write with it too, or the
    ///     atomicity only runs one way.
    /// </remarks>
    [Fact]
    public async Task a_failure_on_fishers_side_rolls_the_participants_work_back()
    {
        var id = Guid.NewGuid();

        await using (var seed = _store.LightweightSession())
        {
            seed.Store(new Invoice { Id = id, Reference = "Already here" });
            await seed.SaveChangesAsync(Token);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Insert(new Invoice { Id = id, Reference = "Duplicate" });
            session.AddTransactionParticipant(new LedgerWriter("never"));

            await Should.ThrowAsync<Exception>(async () => await session.SaveChangesAsync(Token));
        }

        (await LedgerNotesAsync()).ShouldBeEmpty();
    }

    /// <remarks>
    ///     Nothing a participant writes is visible to anyone else until Fisher commits — the same
    ///     position and the same visibility semantics fisher#4 pinned for the outbox's before-commit
    ///     hook, probed the same way over a separate connection.
    /// </remarks>
    [Fact]
    public async Task a_participants_write_is_invisible_until_the_commit()
    {
        var participant = new LedgerWriter("posted") { Probe = LedgerCountAsync };

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Invoice { Id = Guid.NewGuid(), Reference = "INV-3" });
            session.AddTransactionParticipant(participant);

            await session.SaveChangesAsync(Token);
        }

        // Its own write, read back over another connection from inside the transaction.
        participant.VisibleWhenItWrote.ShouldBe(0);

        (await LedgerCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task participants_run_in_the_order_they_were_added()
    {
        await using var session = _store.LightweightSession();
        session.Store(new Invoice { Id = Guid.NewGuid(), Reference = "INV-4" });

        session.AddTransactionParticipant(new LedgerWriter("first"));
        session.AddTransactionParticipant(new LedgerWriter("second"));

        await session.SaveChangesAsync(Token);

        (await LedgerNotesAsync()).ShouldBe(["first", "second"]);
    }

    /// <remarks>
    ///     A tenant scope shares the parent's transaction, so a participant added through one joins the
    ///     transaction that exists — the same reasoning the scope's boundaries and metadata follow.
    /// </remarks>
    [Fact]
    public async Task a_participant_added_through_a_tenant_scope_joins_the_parents_transaction()
    {
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.None;
            options.Schema.For<Invoice>().MultiTenanted();
        });

        await using var session = store.LightweightSession("north");
        // AddTransactionParticipant is on IDocumentSession rather than IDocumentOperations — a
        // participant joins a unit of work's transaction, and a scope has none of its own — so this is
        // reachable through the cast, and the point is that it lands on the parent.
        ((IDocumentSession)session.ForTenant("south"))
            .AddTransactionParticipant(new LedgerWriter("through the scope"));
        session.QueueSqlCommand("insert into ledger (note) values ('the session')");

        await session.SaveChangesAsync(Token);

        (await LedgerNotesAsync()).ShouldBe(["the session", "through the scope"]);
    }

    private async Task<List<string>> LedgerNotesAsync()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select note from ledger order by id";

        var notes = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            notes.Add(reader.GetString(0));
        }

        return notes;
    }

    private async Task<long> LedgerCountAsync()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from ledger";

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token));
    }

    /// <summary>
    ///     A participant that writes on the connection it is handed — which is the whole contract, and
    ///     the thing a participant on a <em>second</em> connection would get wrong by self-deadlocking.
    /// </summary>
    private sealed class LedgerWriter : ITransactionParticipant
    {
        private readonly string _note;
        private readonly bool _thenThrow;

        internal LedgerWriter(string note, bool thenThrow = false)
        {
            _note = note;
            _thenThrow = thenThrow;
        }

        internal Func<Task<long>>? Probe { get; init; }

        internal long VisibleWhenItWrote { get; private set; } = -1;

        public async Task BeforeCommitAsync(SqliteConnection connection, SqliteTransaction transaction,
            CancellationToken token)
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "insert into ledger (note) values ($note)";
                command.Parameters.AddWithValue("$note", _note);

                await command.ExecuteNonQueryAsync(token);
            }

            if (Probe is not null)
            {
                VisibleWhenItWrote = await Probe();
            }

            if (_thenThrow)
            {
                throw new InvalidOperationException("the participant refused");
            }
        }
    }
}

public class Invoice
{
    public Guid Id { get; set; }
    public string Reference { get; set; } = string.Empty;
}
