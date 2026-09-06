using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
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

    /// <summary>
    ///     A participant runs even when Fisher's own unit of work is <b>empty</b>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>SaveChangesAsync</c> short-circuits when there is nothing queued, so that a no-op save
    ///         does not fire every registered listener. A queued participant is not nothing: its entire
    ///         purpose is to write rows Fisher does not know about, so "no documents and no events" says
    ///         nothing about whether there is work to do.
    ///     </para>
    ///     <para>
    ///         Found building <c>Wolverine.Fisher</c> (wolverine#3907). A Wolverine handler that only
    ///         schedules or cascades a message writes no document and appends no event, and enlists a
    ///         participant to write its envelope row inside this transaction — so the early return
    ///         dropped the envelope <em>silently</em>: the send looked successful and the message never
    ///         existed. Polecat fixed the same defect in polecat#161.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_participant_runs_when_the_unit_of_work_is_otherwise_empty()
    {
        await using (var session = _store.LightweightSession())
        {
            // No Store(), no event append - the participant is the only work in this transaction
            session.AddTransactionParticipant(new LedgerWriter("empty unit of work"));

            await session.SaveChangesAsync(Token);
        }

        (await LedgerNotesAsync()).ShouldBe(["empty unit of work"]);
    }

    /// <summary>
    ///     fisher#50, step 2 — a participant enlisted from inside the async daemon writes in the
    ///     <em>batch's</em> transaction, alongside the projection's documents and the progression row.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Both of Fisher's commit paths bracket their transaction identically, and this is the half
    ///         that was missing: without it a projection or subscription enlisting a participant got
    ///         nothing at all, silently, because <c>FisherProjectionBatch</c> simply never looked. That
    ///         is the "absent rather than broken" shape the subscription runner had before fisher#21.
    ///     </para>
    ///     <para>
    ///         Reached through a subscription because that is the shortest route to the batch's own
    ///         session — <c>ISubscription.ProcessEventsAsync</c> is handed one, and a write through it
    ///         already commits with the progression row.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_participant_enlisted_in_a_projection_batch_writes_in_its_transaction()
    {
        var participant = new LedgerWriter("from the daemon");

        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Subscribe(new EnlistingSubscription(participant));
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(Guid.NewGuid(), new LedgerEntryPosted("one"));
            await session.SaveChangesAsync(Token);
        }

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();
        await store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));
        await daemon.StopAllAsync();

        (await LedgerNotesAsync()).ShouldBe(["from the daemon"]);

        // Once per committed transaction, not once per attempt — the after-commit hook runs outside
        // the resilience pipeline in the batch exactly as it does in the session.
        participant.Commits.ShouldBe(1);
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
    ///     weasel#561 — Fisher's <see cref="ITransactionParticipant" /> is the shared generic contract
    ///     closed over Microsoft.Data.Sqlite's pair, and declares no members of its own.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two things the alias buys, and both are the point of adopting it. A Fisher participant
    ///         <em>is</em> the shared contract, so store-agnostic infrastructure holding
    ///         <c>ITransactionParticipant&lt;,&gt;</c> takes one without knowing about Fisher; and
    ///         <c>AfterCommitAsync</c>'s default now comes from that contract rather than from a
    ///         Fisher-local copy. Fisher was the store that <em>had</em> that default, so the lift took
    ///         Fisher's semantic upstream — adopting the generic shape is a simplification, not a loss.
    ///     </para>
    ///     <para>
    ///         Note what contravariance does and does not give: a participant written against the base
    ///         <c>DbConnection</c>/<c>DbTransaction</c> pair converts <em>to</em> the closed generic
    ///         shape, but a class still has to declare Fisher's interface to be handed to
    ///         <see cref="IDocumentSession.AddTransactionParticipant" /> — porting one between the
    ///         stores is a change to its base declaration and nothing else, which is the shared
    ///         contract's own stated expectation.
    ///     </para>
    /// </remarks>
    [Fact]
    public void the_participant_contract_is_the_shared_one_closed_over_sqlite()
    {
        typeof(Weasel.Storage.ITransactionParticipant<SqliteConnection, SqliteTransaction>)
            .IsAssignableFrom(typeof(ITransactionParticipant)).ShouldBeTrue();

        // No members of its own: the alias closes the generics and adds nothing.
        typeof(ITransactionParticipant).GetMembers(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly)
            .ShouldBeEmpty();

        // AfterCommitAsync is answered by the shared contract's default implementation — a
        // non-abstract interface method — rather than by a Fisher-local declaration.
        var afterCommit = typeof(Weasel.Storage.ITransactionParticipant<SqliteConnection, SqliteTransaction>)
            .GetMethod("AfterCommitAsync")!;

        afterCommit.IsAbstract.ShouldBeFalse();
    }

    /// <summary>
    ///     A live Fisher participant satisfies the shared generic contract, which is what makes
    ///     participant code portable across the three stores.
    /// </summary>
    [Fact]
    public async Task a_fisher_participant_is_the_shared_contract()
    {
        var participant = new LedgerWriter("portable");

        Weasel.Storage.ITransactionParticipant<SqliteConnection, SqliteTransaction> shared = participant;
        shared.ShouldBeSameAs(participant);

        await using (var session = _store.LightweightSession())
        {
            session.AddTransactionParticipant(participant);
            session.Store(new Invoice { Id = Guid.NewGuid(), Reference = "shared" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await LedgerCountAsync()).ShouldBe(1);
        participant.Commits.ShouldBe(1);
    }

    /// <summary>
    ///     A participant that writes on the connection it is handed — which is the whole contract, and
    ///     the thing a participant on a <em>second</em> connection would get wrong by self-deadlocking.
    /// </summary>
    private sealed class LedgerWriter : ITransactionParticipant
    {
        private readonly string _note;
        private readonly bool _thenThrow;

        /// <summary>
        ///     How many times Fisher said the write was durable — once per committed transaction,
        ///     however many attempts it took.
        /// </summary>
        internal int Commits { get; private set; }

        public Task AfterCommitAsync(CancellationToken token)
        {
            Commits++;

            return Task.CompletedTask;
        }

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

public record LedgerEntryPosted(string Note);

/// <summary>
///     A subscription whose only job is to enlist a participant in the batch it is handed.
/// </summary>
internal sealed class EnlistingSubscription : Fisher.Subscriptions.SubscriptionBase
{
    private readonly ITransactionParticipant _participant;

    internal EnlistingSubscription(ITransactionParticipant participant) => _participant = participant;

    public override Task<IDaemonChangeListener> ProcessEventsAsync(
        EventRange page,
        ISubscriptionController controller,
        IDocumentSession operations,
        CancellationToken cancellationToken)
    {
        operations.AddTransactionParticipant(_participant);

        return Task.FromResult<IDaemonChangeListener>(NullDaemonChangeListener.Instance);
    }
}
