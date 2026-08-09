using System.Data;
using Fisher.Linq;
using Fisher.Services;
using Fisher.Tests.Events;
using JasperFx;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Documents;

/// <summary>
///     fisher#32 — <see cref="IDocumentSessionListener" />, <see cref="IChangeSet" />, and where in a
///     unit of work each hook fires.
/// </summary>
/// <remarks>
///     <para>
///         The hook <em>boundary</em> was already settled and already pinned by fisher#4: Fisher
///         brackets both commit paths so that <c>BeforeCommit</c> is the last thing inside the
///         transaction and <c>AfterCommit</c> the first thing outside it, verified by probing what
///         another connection can see at each. A session listener is a second client of that seam, so
///         these tests reuse the probe rather than settling the question again.
///     </para>
///     <para>
///         What is genuinely new here is the change set's contents and the three places a hook
///         deliberately does <em>not</em> fire: an empty unit of work, an enlisted session, and the
///         async daemon's projection batch.
///     </para>
/// </remarks>
public class session_listeners : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("listeners");
    private readonly RecordingListener _listener = new();
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<ListenerFly>();
            options.Schema.For<ListenerLure>().SoftDeleted();
            options.Listeners.Add(_listener);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- the hooks ----

    [Fact]
    public async Task both_hooks_fire_in_order()
    {
        await using var session = _store.LightweightSession();
        session.Store(new ListenerFly { Id = Guid.NewGuid(), Pattern = "Adams" });
        await session.SaveChangesAsync(Token);

        _listener.Hooks.ShouldBe(["before", "after"]);
    }

    /// <remarks>
    ///     Marten's rule. Without it every no-op save would run every registered listener, which for a
    ///     store-wide listener means every request that happens to save nothing.
    /// </remarks>
    [Fact]
    public async Task an_empty_unit_of_work_fires_nothing()
    {
        await using var session = _store.LightweightSession();
        await session.SaveChangesAsync(Token);

        _listener.Hooks.ShouldBeEmpty();
    }

    /// <remarks>
    ///     The before hook runs before the batch is taken, which is what makes a listener able to add
    ///     to the unit of work rather than only observe it.
    /// </remarks>
    [Fact]
    public async Task the_before_hook_can_queue_work_that_commits_in_the_same_transaction()
    {
        var added = Guid.NewGuid();
        _listener.OnBeforeSave = session => session.Store(new ListenerFly { Id = added, Pattern = "Added by a listener" });

        await using (var session = _store.LightweightSession())
        {
            session.Store(new ListenerFly { Id = Guid.NewGuid(), Pattern = "Adams" });
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<ListenerFly>(added, Token))!.Pattern.ShouldBe("Added by a listener");
    }

    /// <remarks>
    ///     <b>A small divergence from Marten, and deliberate.</b> Marten processes the unit of work's
    ///     events before calling this hook, so a listener that starts a stream there is appending to
    ///     the <em>next</em> unit of work. Fisher collects the pending streams after the hook returns,
    ///     which costs nothing and makes "queued work joins this transaction" true of events as well as
    ///     of documents.
    /// </remarks>
    [Fact]
    public async Task the_before_hook_can_append_events_that_commit_in_the_same_transaction()
    {
        var listenerStream = Guid.NewGuid();
        _listener.OnBeforeSave = session
            => session.Events.StartStream(listenerStream, new QuestStarted("Started by a listener"));

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(Guid.NewGuid(), new QuestStarted("Destroy the ring"));
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.Events.FetchStreamAsync(listenerStream, token: Token)).Count.ShouldBe(1);
    }

    /// <summary>
    ///     The after hook really is outside the transaction.
    /// </summary>
    /// <remarks>
    ///     Hook order alone does not prove it — the two would fire in that order even if both ran
    ///     before the commit. What separates them is what another connection can see at the moment each
    ///     runs, which is the probe fisher#4 established for the outbox.
    /// </remarks>
    [Fact]
    public async Task the_after_hook_runs_once_the_transaction_has_committed()
    {
        _listener.Probe = CountFliesAsync;

        await using var session = _store.LightweightSession();
        session.Store(new ListenerFly { Id = Guid.NewGuid(), Pattern = "Adams" });
        await session.SaveChangesAsync(Token);

        _listener.VisibleAtBeforeSave.ShouldBe(0);
        _listener.VisibleAtAfterCommit.ShouldBe(1);
    }

    [Fact]
    public async Task a_listener_that_throws_before_the_save_fails_the_unit_of_work()
    {
        _listener.OnBeforeSave = _ => throw new InvalidOperationException("no");

        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new ListenerFly { Id = id, Pattern = "Never written" });

            await Should.ThrowAsync<InvalidOperationException>(async () => await session.SaveChangesAsync(Token));
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<ListenerFly>(id, Token)).ShouldBeNull();
    }

    /// <remarks>
    ///     The other side of the boundary, stated because it is the part people get wrong: a post-commit
    ///     hook cannot un-commit anything. The exception reaches the caller and the data is there.
    /// </remarks>
    [Fact]
    public async Task a_listener_that_throws_after_the_commit_does_not_undo_it()
    {
        _listener.OnAfterCommit = _ => throw new InvalidOperationException("too late");

        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new ListenerFly { Id = id, Pattern = "Committed anyway" });

            await Should.ThrowAsync<InvalidOperationException>(async () => await session.SaveChangesAsync(Token));
        }

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<ListenerFly>(id, Token)).ShouldNotBeNull();
    }

    // ---- the change set ----

    [Fact]
    public async Task the_change_set_separates_inserts_from_stores()
    {
        var stored = new ListenerFly { Id = Guid.NewGuid(), Pattern = "Stored" };
        var inserted = new ListenerFly { Id = Guid.NewGuid(), Pattern = "Inserted" };

        await using var session = _store.LightweightSession();
        session.Store(stored);
        session.Insert(inserted);
        await session.SaveChangesAsync(Token);

        _listener.Commit!.Updated.ShouldBe([stored]);
        _listener.Commit.Inserted.ShouldBe([inserted]);
        _listener.Commit.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task the_change_set_reports_a_delete_by_id_with_its_identity()
    {
        var id = Guid.NewGuid();

        await using (var seed = _store.LightweightSession())
        {
            seed.Store(new ListenerFly { Id = id, Pattern = "Doomed" });
            await seed.SaveChangesAsync(Token);
        }

        await using var session = _store.LightweightSession();
        session.Delete<ListenerFly>(id);
        await session.SaveChangesAsync(Token);

        var deleted = _listener.Commit!.Deleted.Single();
        deleted.DocumentType.ShouldBe(typeof(ListenerFly));
        deleted.Id.ShouldBe(id);
    }

    /// <remarks>
    ///     A predicate delete never loaded a row and never named an id, so the type is all it can
    ///     report. Reporting it with a null id beats leaving <c>DeleteWhere</c> invisible to a listener
    ///     watching that type — which is the choice Polecat's interface documents and this follows.
    /// </remarks>
    [Fact]
    public async Task the_change_set_reports_a_delete_by_predicate_with_no_identity()
    {
        await using var session = _store.LightweightSession();
        session.DeleteWhere<ListenerFly>(x => x.Pattern == "Adams");
        await session.SaveChangesAsync(Token);

        var deleted = _listener.Commit!.Deleted.Single();
        deleted.DocumentType.ShouldBe(typeof(ListenerFly));
        deleted.Id.ShouldBeNull();
    }

    /// <remarks>
    ///     A soft delete's statement is an <c>update … set is_deleted = 1</c>, so "what SQL did this
    ///     run" is the wrong question to classify by. The operation is a deletion and is reported as
    ///     one.
    /// </remarks>
    [Fact]
    public async Task the_change_set_reports_a_soft_delete_as_a_deletion()
    {
        var id = Guid.NewGuid();

        await using (var seed = _store.LightweightSession())
        {
            seed.Store(new ListenerLure { Id = id, Name = "Doomed" });
            await seed.SaveChangesAsync(Token);
        }

        await using var session = _store.LightweightSession();
        session.Delete<ListenerLure>(id);
        await session.SaveChangesAsync(Token);

        _listener.Commit!.Updated.ShouldBeEmpty();

        var deleted = _listener.Commit.Deleted.Single();
        deleted.DocumentType.ShouldBe(typeof(ListenerLure));
        deleted.Id.ShouldBe(id);
    }

    [Fact]
    public async Task the_change_set_carries_the_events_and_their_streams()
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Events.StartStream(streamId, new QuestStarted("Destroy the ring"), new MemberJoined("Frodo"));
        await session.SaveChangesAsync(Token);

        _listener.Commit!.GetStreams().Single().Id.ShouldBe(streamId);
        _listener.Commit.GetEvents().Select(x => x.Data.GetType())
            .ShouldBe([typeof(QuestStarted), typeof(MemberJoined)]);
    }

    /// <remarks>
    ///     Fisher's change set is built from the operations snapshot the transaction wrote from, so it
    ///     is immutable by construction and <c>Clone</c> has nothing to copy. Pinned as a decision,
    ///     because Marten's returns a real copy and the difference should be a deliberate one.
    /// </remarks>
    [Fact]
    public async Task cloning_a_change_set_returns_the_same_immutable_snapshot()
    {
        await using var session = _store.LightweightSession();
        session.Store(new ListenerFly { Id = Guid.NewGuid(), Pattern = "Adams" });
        await session.SaveChangesAsync(Token);

        var commit = _listener.Commit!;
        commit.Clone().ShouldBeSameAs(commit);

        // And it survives the session moving on, which is what Marten's Clone exists to guarantee.
        session.Store(new ListenerFly { Id = Guid.NewGuid(), Pattern = "Later" });
        await session.SaveChangesAsync(Token);

        commit.Updated.Count().ShouldBe(1);
    }

    // ---- the document hooks ----

    [Fact]
    public async Task the_document_hooks_see_stores_and_loads()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new ListenerFly { Id = id, Pattern = "Adams" });
            await session.SaveChangesAsync(Token);
        }

        _listener.Added.ShouldBe([id]);

        await using (var session = _store.LightweightSession())
        {
            await session.LoadAsync<ListenerFly>(id, Token);
        }

        _listener.Loaded.ShouldBe([id]);
    }

    /// <remarks>
    ///     A query is a load as far as the hook is concerned, on every tracking mode, because every
    ///     writeable selector reports what it materialises.
    /// </remarks>
    [Fact]
    public async Task the_loaded_hook_fires_for_a_query_too()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new ListenerFly { Id = id, Pattern = "Adams" });
            await session.SaveChangesAsync(Token);
        }

        _listener.Loaded.Clear();

        await using (var session = _store.IdentitySession())
        {
            await session.Query<ListenerFly>().ToListAsync(Token);
        }

        _listener.Loaded.ShouldBe([id]);
    }

    // ---- registration ----

    [Fact]
    public async Task a_session_runs_the_stores_listeners_and_then_its_own()
    {
        var order = new List<string>();
        _listener.OnBeforeSave = _ => order.Add("store");

        var sessionListener = new RecordingListener { OnBeforeSave = _ => order.Add("session") };
        var options = new SessionOptions();
        options.Listeners.Add(sessionListener);

        await using var session = _store.OpenSession(options);
        session.Store(new ListenerFly { Id = Guid.NewGuid(), Pattern = "Adams" });
        await session.SaveChangesAsync(Token);

        order.ShouldBe(["store", "session"]);
        sessionListener.Hooks.ShouldBe(["before", "after"]);
    }

    // ---- where the hooks deliberately do not fire ----

    /// <remarks>
    ///     Same rule as the outbox's after-commit hook and the event store's append observer: Fisher is
    ///     not told when the caller commits, so it cannot claim "everyone can see this now". The before
    ///     hook still runs, because it makes the same claim in either case.
    /// </remarks>
    [Fact]
    public async Task an_enlisted_session_fires_the_before_hook_and_not_the_after_hook()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, Token);

        await using (var session = _store.OpenSession(SessionOptions.ForTransaction(transaction)))
        {
            session.Store(new ListenerFly { Id = Guid.NewGuid(), Pattern = "Enlisted" });
            await session.SaveChangesAsync(Token);
        }

        _listener.Hooks.ShouldBe(["before"]);

        await transaction.CommitAsync(Token);
    }

    /// <summary>
    ///     The async daemon's projection batch is not a user unit of work and does not run user
    ///     listeners.
    /// </summary>
    /// <remarks>
    ///     A decision, not an omission: an application's <c>AfterCommitAsync</c> running on the daemon's
    ///     threads for every batch of every shard is a surprise nobody asked for, and JasperFx's
    ///     <c>IDaemonChangeListener</c> is the hook for that side. The append that feeds the projection
    ///     is a user unit of work and does fire, which is why this counts rather than asserting empty.
    /// </remarks>
    [Fact]
    public async Task the_async_daemons_projection_batch_does_not_fire_session_listeners()
    {
        await using var database = TemporaryDatabase.Create("listeners-daemon");
        var listener = new RecordingListener();

        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Listeners.Add(listener);
            options.Projections.Snapshot<ListenerQuestTally>(SnapshotLifecycle.Async);
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<ListenerQuestTally>(Guid.NewGuid(), new MemberJoined("Frodo"));
            await session.SaveChangesAsync(Token);
        }

        listener.Hooks.ShouldBe(["before", "after"]);

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();
        await store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));
        await daemon.StopAllAsync();

        // The snapshot was written by the daemon, and the listener saw nothing of it.
        await using var query = store.LightweightSession();
        (await query.Query<ListenerQuestTally>().ToListAsync(Token)).Count.ShouldBe(1);

        listener.Hooks.ShouldBe(["before", "after"]);
    }

    /// <summary>How many flies are committed, read over a connection of its own.</summary>
    private async Task<long> CountFliesAsync()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from fi_doc_listenerfly";

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token));
    }
}

/// <summary>
///     Records what fired and, optionally, what the rest of the database could see when it did.
/// </summary>
public class RecordingListener : IDocumentSessionListener
{
    public List<string> Hooks { get; } = [];
    public List<object> Added { get; } = [];
    public List<object> Loaded { get; } = [];
    public IChangeSet? Commit { get; private set; }

    public Action<IDocumentSession>? OnBeforeSave { get; set; }
    public Action<IChangeSet>? OnAfterCommit { get; set; }
    public Func<Task<long>>? Probe { get; set; }

    public long VisibleAtBeforeSave { get; private set; } = -1;
    public long VisibleAtAfterCommit { get; private set; } = -1;

    public async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        Hooks.Add("before");

        if (Probe is not null)
        {
            VisibleAtBeforeSave = await Probe();
        }

        OnBeforeSave?.Invoke(session);
    }

    public async Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        Hooks.Add("after");
        Commit = commit;

        if (Probe is not null)
        {
            VisibleAtAfterCommit = await Probe();
        }

        OnAfterCommit?.Invoke(commit);
    }

    public void DocumentAddedForStorage(object id, object document) => Added.Add(id);

    public void DocumentLoaded(object id, object document) => Loaded.Add(id);
}

/// <summary>
///     A listener with only the two commit hooks, which is Polecat's whole interface — it compiles
///     because the two synchronous members are default-implemented.
/// </summary>
public class CommitOnlyListener : IDocumentSessionListener
{
    public Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token) => Task.CompletedTask;

    public Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
        => Task.CompletedTask;
}

public class ListenerFly
{
    public Guid Id { get; set; }
    public string Pattern { get; set; } = string.Empty;
}

public class ListenerLure
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class ListenerQuestTally
{
    public Guid Id { get; set; }
    public int Members { get; set; }

    public void Apply(MemberJoined joined) => Members++;
}
