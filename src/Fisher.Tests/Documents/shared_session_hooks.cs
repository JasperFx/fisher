using System.Data;
using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Documents;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Documents;

/// <summary>
///     fisher#581: the three members of JasperFx's store-agnostic document contract a message bus needs,
///     <c>StoreObjects</c>, <c>AddTransactionParticipant(IDbTransactionParticipant)</c> and
///     <c>AddSaveListener</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every test holds the session as the JasperFx contract, never as a Fisher type</b>, because
///         that is the only route that reaches the contract's default implementations. Each default
///         throws <see cref="NotSupportedException" />, and before fisher#581 every test here failed with
///         it: Fisher compiled cleanly and only a store-agnostic caller found out.
///     </para>
///     <para>
///         The participant and listener tests reuse <c>transaction_participants</c>' shape: an application
///         table in the same file, written on the connection Fisher hands over.
///     </para>
/// </remarks>
public class shared_session_hooks : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("shared-hooks");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = BuildStore();

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "create table if not exists hook_ledger (id integer primary key, note text not null)";
        await command.ExecuteNonQueryAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private DocumentStore BuildStore(Action<StoreOptions>? configure = null) => DocumentStore.For(options =>
    {
        options.ConnectionString = _database.ConnectionString;
        options.AutoCreateSchemaObjects = AutoCreate.All;
        options.Schema.For<HookInvoice>().MultiTenanted();
        options.Schema.For<HookVessel>().AddSubClass<HookTrawler>();
        options.Schema.For<HookCrew>();
        configure?.Invoke(options);
    });

    // ---- StoreObjects ----

    [Fact]
    public async Task store_objects_stores_each_document_as_its_runtime_type()
    {
        var invoice = new HookInvoice { Id = Guid.NewGuid(), Reference = "INV-1" };
        var vessel = new HookVessel { Id = Guid.NewGuid(), Name = "Pequod" };
        var trawler = new HookTrawler { Id = Guid.NewGuid(), Name = "Andrea Gail", NetLength = 40 };
        var crew = new HookCrew { Id = "ishmael", Role = "narrator" };

        await using (var rawSession = _store.LightweightSession())
        {
            IDocumentSessionOperations session = rawSession;
            // Held as object, so nothing but the runtime type can say where each one goes
            session.StoreObjects(new object[] { invoice, vessel, trawler, crew });
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.QuerySession();

        (await query.LoadAsync<HookInvoice>(invoice.Id, Token))!.Reference.ShouldBe("INV-1");
        (await query.LoadAsync<HookCrew>("ishmael", Token))!.Role.ShouldBe("narrator");
        (await query.LoadAsync<HookVessel>(vessel.Id, Token)).ShouldBeOfType<HookVessel>();

        // The sub-class lands in its base's table with its own discriminator, exactly as Store(trawler)
        // would: it loads through the base as itself, and through its own type.
        (await query.LoadAsync<HookVessel>(trawler.Id, Token)).ShouldBeOfType<HookTrawler>().NetLength.ShouldBe(40);
        (await query.LoadAsync<HookTrawler>(trawler.Id, Token)).ShouldNotBeNull();
        (await query.Query<HookTrawler>().ToListAsync(Token)).Select(x => x.Id).ShouldBe([trawler.Id]);
    }

    [Fact]
    public async Task store_objects_skips_null_elements_and_accepts_an_empty_collection()
    {
        var crew = new HookCrew { Id = "queequeg", Role = "harpooner" };

        await using (var rawSession = _store.LightweightSession())
        {
            IDocumentSessionOperations session = rawSession;
            session.StoreObjects([]);
            session.StoreObjects(new object[] { null!, crew, null! });
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.QuerySession();
        (await query.LoadAsync<HookCrew>("queequeg", Token)).ShouldNotBeNull();
    }

    [Fact]
    public async Task store_objects_refuses_a_null_collection()
    {
        await using var session = _store.LightweightSession();
        IDocumentWriteOperations contract = session;

        Should.Throw<ArgumentNullException>(() => contract.StoreObjects(null!));
    }

    // ---- AddTransactionParticipant(IDbTransactionParticipant) ----

    [Fact]
    public async Task a_shared_participant_commits_with_fishers_work()
    {
        var id = Guid.NewGuid();
        var participant = new LedgerParticipant("shared");

        await using (var rawSession = _store.LightweightSession())
        {
            IDocumentSessionOperations session = rawSession;
            session.Store(new HookInvoice { Id = id, Reference = "INV-2" });
            session.AddTransactionParticipant(participant);
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.QuerySession();
        (await query.LoadAsync<HookInvoice>(id, Token)).ShouldNotBeNull();
        (await LedgerNotesAsync()).ShouldBe(["shared"]);
    }

    [Fact]
    public async Task a_shared_participant_rolls_back_with_fishers_work()
    {
        var id = Guid.NewGuid();

        await using (var rawSeed = _store.LightweightSession())
        {
            IDocumentSessionOperations seed = rawSeed;
            seed.Store(new HookInvoice { Id = id, Reference = "existing" });
            await seed.SaveChangesAsync(Token);
        }

        await using (var rawSession = _store.LightweightSession())
        {
            IDocumentSessionOperations session = rawSession;
            // Insert of an id that exists fails Fisher's half inside the transaction
            session.Insert(new HookInvoice { Id = id, Reference = "duplicate" });
            session.AddTransactionParticipant(new LedgerParticipant("rolled back"));

            await Should.ThrowAsync<Exception>(() => session.SaveChangesAsync(Token));
        }

        (await LedgerNotesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_participant_that_throws_rolls_fishers_work_back()
    {
        var id = Guid.NewGuid();

        await using (var rawSession = _store.LightweightSession())
        {
            IDocumentSessionOperations session = rawSession;
            session.Store(new HookInvoice { Id = id, Reference = "never" });
            session.AddTransactionParticipant(new LedgerParticipant("refuses") { ThenThrow = true });

            await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync(Token));
        }

        await using var query = _store.QuerySession();
        (await query.LoadAsync<HookInvoice>(id, Token)).ShouldBeNull();
        (await LedgerNotesAsync()).ShouldBeEmpty();
    }

    /// <remarks>
    ///     A participant writes rows Fisher knows nothing about, so "no documents and no events" is not an
    ///     empty unit of work: the contract's <c>HasPendingParticipants</c> rule, and wolverine#3907's shape.
    /// </remarks>
    [Fact]
    public async Task a_shared_participant_runs_on_an_otherwise_empty_unit_of_work()
    {
        await using (var rawSession = _store.LightweightSession())
        {
            IDocumentSessionOperations session = rawSession;
            session.AddTransactionParticipant(new LedgerParticipant("envelope only"));
            await session.SaveChangesAsync(Token);
        }

        (await LedgerNotesAsync()).ShouldBe(["envelope only"]);
    }

    /// <remarks>
    ///     The contract's rule, and different from Fisher's own participant, which stays enlisted until the
    ///     session is disposed: a session saved twice must not write an envelope row twice.
    /// </remarks>
    [Fact]
    public async Task a_shared_participant_is_released_when_its_commit_lands()
    {
        var participant = new LedgerParticipant("once");

        await using (var rawSession = _store.LightweightSession())
        {
            IDocumentSessionOperations session = rawSession;
            session.AddTransactionParticipant(participant);
            await session.SaveChangesAsync(Token);

            session.Store(new HookCrew { Id = "starbuck", Role = "mate" });
            await session.SaveChangesAsync(Token);
        }

        participant.Calls.ShouldBe(1);
        (await LedgerNotesAsync()).ShouldBe(["once"]);
    }

    /// <summary>
    ///     A retried <c>SQLITE_BUSY</c> replays the participant on its fresh transaction, once per attempt,
    ///     and what the failed attempt wrote rolled back with it. The save listener's hooks fire once each.
    /// </summary>
    [Fact]
    public async Task a_busy_retry_replays_the_participant_and_fires_each_listener_hook_once()
    {
        var listener = new RecordingSaveListener();
        var participant = new LedgerParticipant("retried") { BusyOnFirstAttempt = true };

        await using (var rawSession = _store.LightweightSession())
        {
            IDocumentSessionOperations session = rawSession;
            session.AddSaveListener(listener);
            session.Store(new HookCrew { Id = "stubb", Role = "second mate" });
            session.AddTransactionParticipant(participant);

            await session.SaveChangesAsync(Token);
        }

        participant.Calls.ShouldBe(2, "once per attempt");
        (await LedgerNotesAsync()).ShouldBe(["retried"], "the first attempt's row rolled back");

        listener.Hooks.ShouldBe(["before", "after"]);
    }

    [Fact]
    public async Task a_shared_participant_added_through_a_tenant_scope_joins_the_parents_transaction()
    {
        await using (var session = _store.LightweightSession("north"))
        {
            // ITenantOperations is the contract's write tier, so the shared overload is reachable as is
            session.ForTenant("south").AddTransactionParticipant(new LedgerParticipant("through the scope"));
            session.ForTenant("south").Store(new HookInvoice { Id = Guid.NewGuid(), Reference = "south" });

            await session.SaveChangesAsync(Token);
        }

        (await LedgerNotesAsync()).ShouldBe(["through the scope"]);
    }

    /// <remarks>
    ///     The caller commits an enlisted session, so Fisher never learns the commit landed and could never
    ///     release the participant. The contract names that shape as one a store must refuse.
    /// </remarks>
    [Fact]
    public async Task a_shared_participant_is_refused_on_an_enlisted_session()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, Token);

        await using (var session = _store.OpenSession(SessionOptions.ForTransaction(transaction)))
        {
            IDocumentWriteOperations contract = session;

            Should.Throw<NotSupportedException>(() => contract.AddTransactionParticipant(new LedgerParticipant("no")))
                .Message.ShouldContain("enlisted");
        }

        await transaction.RollbackAsync(Token);
    }

    /// <summary>
    ///     jasperfx#1052's case: a subscription's outbox enlisting in the daemon's transaction through the
    ///     write tier it is handed.
    /// </summary>
    [Fact]
    public async Task a_shared_participant_enlisted_in_a_projection_batch_writes_in_its_transaction()
    {
        var subscription = new SharedHookSubscription(new LedgerParticipant("from the daemon"));

        await using var store = BuildStore(options => options.Projections.Subscribe(subscription));
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(Guid.NewGuid(), new LedgerEntryPosted("one"));
            await session.SaveChangesAsync(Token);
        }

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();
        await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);
        await daemon.StopAllAsync();

        (await LedgerNotesAsync()).ShouldBe(["from the daemon"]);

        // The daemon owns that commit and never runs a session's save hooks, so a listener is refused
        // rather than accepted and never fired.
        subscription.ListenerRefusal.ShouldBeOfType<NotSupportedException>();
    }

    /// <summary>
    ///     The message batch is the last thing inside the transaction, after every participant: the
    ///     promise <c>IMessageBatch.BeforeCommitAsync</c> makes, and the order the projection batch already
    ///     had. The session path used to run the batch first.
    /// </summary>
    [Fact]
    public async Task the_message_batch_runs_after_every_participant()
    {
        var order = new List<string>();

        await using var store = BuildStore(options => options.Events.MessageOutbox = new OrderOutbox(order));

        await using (var session = store.LightweightSession())
        {
            var sink = await ((JasperFx.Events.IStorageOperations)session).GetOrStartMessageSink();
            await sink.PublishAsync(new LedgerEntryPosted("message"), StorageConstants.DefaultTenantId);

            session.AddTransactionParticipant(new OrderParticipant(order, "fisher participant"));
            ((IDocumentWriteOperations)session).AddTransactionParticipant(new OrderParticipant(order, "shared participant"));

            await session.SaveChangesAsync(Token);
        }

        order.ShouldBe(["fisher participant", "shared participant", "message batch", "message batch after commit"]);
    }

    // ---- AddSaveListener ----

    [Fact]
    public async Task a_save_listener_brackets_the_commit()
    {
        var listener = new RecordingSaveListener { Probe = () => CrewCountOnAnotherConnectionAsync() };

        await using (var rawSession = _store.LightweightSession())
        {
            IDocumentSessionOperations session = rawSession;
            session.AddSaveListener(listener);
            session.Store(new HookCrew { Id = "flask", Role = "third mate" });
            await session.SaveChangesAsync(Token);
        }

        listener.Hooks.ShouldBe(["before", "after"]);

        // Nothing written when the before hook ran; committed and visible when the after hook ran
        listener.Seen.ShouldBe([0L, 1L]);
        listener.Commit!.Updated.Count.ShouldBe(1, "a Store is an upsert");
    }

    /// <summary>
    ///     The outbox shape the contract exists for: the before hook enlists a participant, which joins
    ///     this commit, and the after hook is where the outbox would flush.
    /// </summary>
    [Fact]
    public async Task a_participant_enlisted_from_the_before_hook_joins_the_same_commit()
    {
        var listener = new RecordingSaveListener
        {
            OnBefore = session => session.AddTransactionParticipant(new LedgerParticipant("envelope"))
        };

        await using (var rawSession = _store.LightweightSession())
        {
            IDocumentSessionOperations session = rawSession;
            session.AddSaveListener(listener);
            session.Store(new HookCrew { Id = "pip", Role = "cabin boy" });
            await session.SaveChangesAsync(Token);
        }

        (await LedgerNotesAsync()).ShouldBe(["envelope"]);
        listener.Hooks.ShouldBe(["before", "after"]);
    }

    [Fact]
    public async Task a_save_listener_runs_on_every_save_of_the_session()
    {
        var listener = new RecordingSaveListener();

        await using (var rawSession = _store.LightweightSession())
        {
            IDocumentSessionOperations session = rawSession;
            session.AddSaveListener(listener);

            session.Store(new HookCrew { Id = "ahab", Role = "captain" });
            await session.SaveChangesAsync(Token);

            session.Store(new HookCrew { Id = "fedallah", Role = "harpooner" });
            await session.SaveChangesAsync(Token);
        }

        listener.Hooks.ShouldBe(["before", "after", "before", "after"]);
    }

    [Fact]
    public async Task an_empty_unit_of_work_fires_no_save_listener()
    {
        var listener = new RecordingSaveListener();

        await using (var rawSession = _store.LightweightSession())
        {
            IDocumentSessionOperations session = rawSession;
            session.AddSaveListener(listener);
            await session.SaveChangesAsync(Token);
        }

        listener.Hooks.ShouldBeEmpty();
    }

    /// <remarks>
    ///     Fisher's existing rule for its own listeners: the caller commits, so "everyone can see this now"
    ///     is a claim Fisher cannot make.
    /// </remarks>
    [Fact]
    public async Task an_enlisted_session_fires_only_the_before_hook()
    {
        var listener = new RecordingSaveListener();

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, Token);

        await using (var session = _store.OpenSession(SessionOptions.ForTransaction(transaction)))
        {
            IDocumentSessionOperations contract = session;
            contract.AddSaveListener(listener);

            contract.Store(new HookCrew { Id = "daggoo", Role = "harpooner" });
            await contract.SaveChangesAsync(Token);
        }

        listener.Hooks.ShouldBe(["before"]);

        await transaction.CommitAsync(Token);
    }

    [Fact]
    public async Task a_save_listener_added_through_a_tenant_scope_lands_on_the_parent()
    {
        var listener = new RecordingSaveListener();

        IDocumentSession? parent;

        await using (var session = _store.LightweightSession("north"))
        {
            parent = session;

            // A scope is a FisherSession too, so the cast reaches the session tier; the point is where the
            // listener lands
            var scope = (IDocumentSessionOperations)session.ForTenant("south");
            scope.AddSaveListener(listener);

            session.Store(new HookInvoice { Id = Guid.NewGuid(), Reference = "north" });
            await session.SaveChangesAsync(Token);
        }

        listener.Hooks.ShouldBe(["before", "after"]);
        listener.SessionsSeen.ShouldAllBe(x => ReferenceEquals(x, parent));
    }

    // ---- helpers ----

    private async Task<List<string>> LedgerNotesAsync()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select note from hook_ledger order by id";

        var notes = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            notes.Add(reader.GetString(0));
        }

        return notes;
    }

    private async Task<long> CrewCountOnAnotherConnectionAsync()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from fi_doc_hookcrew";

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token));
    }

    /// <summary>
    ///     Writes on the connection it is handed, which is the whole contract.
    /// </summary>
    private sealed class LedgerParticipant(string note) : IDbTransactionParticipant
    {
        public bool ThenThrow { get; init; }

        public bool BusyOnFirstAttempt { get; init; }

        public int Calls { get; private set; }

        public async Task BeforeCommitAsync(System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction, CancellationToken token)
        {
            Calls++;

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "insert into hook_ledger (note) values ($note)";

                var parameter = command.CreateParameter();
                parameter.ParameterName = "$note";
                parameter.Value = note;
                command.Parameters.Add(parameter);

                await command.ExecuteNonQueryAsync(token);
            }

            if (BusyOnFirstAttempt && Calls == 1)
            {
                // After its write, so the retry proves the first attempt's row rolled back
                throw new SqliteException("database is locked", 5);
            }

            if (ThenThrow)
            {
                throw new InvalidOperationException("the participant refused");
            }
        }
    }

    private sealed class RecordingSaveListener : IDocumentSaveListener
    {
        public List<string> Hooks { get; } = [];

        public List<long> Seen { get; } = [];

        public List<IDocumentSessionOperations> SessionsSeen { get; } = [];

        public IDocumentChangeSet? Commit { get; private set; }

        public Func<Task<long>>? Probe { get; init; }

        public Action<IDocumentSessionOperations>? OnBefore { get; init; }

        public async Task BeforeSaveChangesAsync(IDocumentSessionOperations session, CancellationToken token)
        {
            Hooks.Add("before");
            SessionsSeen.Add(session);
            OnBefore?.Invoke(session);

            if (Probe is not null)
            {
                Seen.Add(await Probe());
            }
        }

        public async Task AfterCommitAsync(IDocumentSessionOperations session, IDocumentChangeSet commit,
            CancellationToken token)
        {
            Hooks.Add("after");
            SessionsSeen.Add(session);
            Commit = commit;

            if (Probe is not null)
            {
                Seen.Add(await Probe());
            }
        }
    }

    private sealed class OrderParticipant(List<string> order, string name)
        : ITransactionParticipant, IDbTransactionParticipant
    {
        public Task BeforeCommitAsync(SqliteConnection connection, SqliteTransaction transaction,
            CancellationToken token)
        {
            order.Add(name);
            return Task.CompletedTask;
        }

        public Task BeforeCommitAsync(System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction, CancellationToken token)
        {
            order.Add(name);
            return Task.CompletedTask;
        }
    }

    private sealed class OrderOutbox(List<string> order) : Fisher.Events.Messaging.IMessageOutbox
    {
        public ValueTask<Fisher.Events.Messaging.IMessageBatch> CreateBatch(IDocumentSession session)
            => new(new OrderBatch(order));

        private sealed class OrderBatch(List<string> order) : Fisher.Events.Messaging.IMessageBatch
        {
            public ValueTask PublishAsync<T>(T message, string tenantId) => ValueTask.CompletedTask;

            public Task BeforeCommitAsync(CancellationToken token)
            {
                order.Add("message batch");
                return Task.CompletedTask;
            }

            public Task AfterCommitAsync(CancellationToken token)
            {
                order.Add("message batch after commit");
                return Task.CompletedTask;
            }
        }
    }

    /// <summary>
    ///     Enlists a shared participant through the write tier it is handed, and records what happens when
    ///     it asks for a save listener on the batch's session.
    /// </summary>
    private sealed class SharedHookSubscription(IDbTransactionParticipant participant)
        : Fisher.Subscriptions.SubscriptionBase
    {
        public Exception? ListenerRefusal { get; private set; }

        public override Task<IDaemonChangeListener> ProcessEventsAsync(
            EventRange page,
            ISubscriptionController controller,
            IDocumentSession operations,
            CancellationToken cancellationToken)
        {
            ((IDocumentWriteOperations)operations).AddTransactionParticipant(participant);

            try
            {
                ((IDocumentSessionOperations)operations).AddSaveListener(new RecordingSaveListener());
            }
            catch (Exception e)
            {
                ListenerRefusal = e;
            }

            return Task.FromResult<IDaemonChangeListener>(NullDaemonChangeListener.Instance);
        }
    }
}

public class HookInvoice
{
    public Guid Id { get; set; }
    public string Reference { get; set; } = string.Empty;
}

public class HookVessel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class HookTrawler : HookVessel
{
    public int NetLength { get; set; }
}

public class HookCrew
{
    public string Id { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}
