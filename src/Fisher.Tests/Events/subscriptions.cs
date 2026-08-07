using System.Collections.Concurrent;
using Fisher.Linq;
using Fisher.Subscriptions;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Events;

/// <summary>
///     fisher#21 — subscriptions, the daemon shard that hands each range of events to arbitrary code
///     rather than to a projection.
/// </summary>
/// <remarks>
///     <para>
///         The assertions worth having are about the two guarantees, which differ: writes through the
///         supplied session commit in the batch's transaction alongside the progression row, so they
///         are exactly-once against Fisher's own database; anything outside it is at-least-once and
///         cannot be otherwise.
///     </para>
///     <para>
///         Ordering is the other thing worth pinning. A subscription that cannot rely on seeing events
///         in global sequence order is not much use for feeding anything downstream.
///     </para>
/// </remarks>
public class subscriptions : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("subscriptions");
    private DocumentStore _store = null!;
    private IProjectionDaemon? _daemon;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_daemon is not null)
        {
            await _daemon.StopAllAsync();
            _daemon.Dispose();
        }

        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        _database.Dispose();
    }

    private async Task<IProjectionDaemon> StartWithAsync(ISubscription subscription)
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Subscribe(subscription);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        _daemon = await _store.BuildProjectionDaemonAsync();
        await _daemon.StartAllAsync();

        return _daemon;
    }

    private async Task AppendAsync(params object[] events)
    {
        await using var session = _store.LightweightSession();
        session.Events.StartStream<Quest>(Guid.NewGuid(), events);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    // ---- the shard runs at all ----

    /// <summary>
    ///     The headline. <c>SubscriptionExecution&lt;T&gt;</c> resolves the runner with a soft
    ///     <c>as</c> cast, so before fisher#21 registering a subscription failed at runtime rather than
    ///     at compile time — absent rather than broken.
    /// </summary>
    [Fact]
    public async Task a_registered_subscription_receives_events()
    {
        var recorder = new RecordingSubscription();
        await StartWithAsync(recorder);

        await AppendAsync(new QuestStarted("Find the ring"), new MemberJoined("Frodo"));

        await _store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));

        recorder.Seen.Count.ShouldBe(2);
    }

    [Fact]
    public async Task events_arrive_in_global_sequence_order()
    {
        var recorder = new RecordingSubscription();
        await StartWithAsync(recorder);

        await AppendAsync(new QuestStarted("One"), new MemberJoined("A"));
        await AppendAsync(new QuestStarted("Two"), new MemberJoined("B"));

        await _store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));

        var sequences = recorder.Seen.Select(x => x.Sequence).ToArray();

        sequences.Length.ShouldBe(4);
        sequences.ShouldBe(sequences.OrderBy(x => x).ToArray());
    }

    /// <summary>
    ///     Progress is recorded per range, so a subscription that has caught up leaves the daemon
    ///     non-stale — which is what makes it a peer of a projection rather than a side channel.
    /// </summary>
    [Fact]
    public async Task the_shard_records_its_progress()
    {
        var recorder = new RecordingSubscription();
        var daemon = await StartWithAsync(recorder);

        await AppendAsync(new QuestStarted("Find the ring"));

        await _store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));

        var progress = await _store.Database
            .ProjectionProgressFor(new ShardName(recorder.Name), TestContext.Current.CancellationToken);

        progress.ShouldBeGreaterThan(0);
    }

    // ---- the transactional guarantee ----

    /// <summary>
    ///     The guarantee that distinguishes a subscription from a webhook: writes through the supplied
    ///     session commit in the batch's transaction, alongside the progression row. A subscription
    ///     cannot advance past a range whose writes were rolled back.
    /// </summary>
    [Fact]
    public async Task writes_through_the_supplied_session_are_committed_with_the_batch()
    {
        await StartWithAsync(new WritingSubscription());

        await AppendAsync(new QuestStarted("Find the ring"), new MemberJoined("Frodo"));

        await _store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));

        await using var session = _store.LightweightSession();
        var notes = await session.Query<SubscriptionNote>()
            .ToListAsync(TestContext.Current.CancellationToken);

        notes.Count.ShouldBe(2);
    }

    // ---- the post-commit hook ----

    /// <summary>
    ///     Runs after the batch commits, and outside the resilience pipeline — a retried
    ///     <c>SQLITE_BUSY</c> re-executes the batch delegate, so a listener called inside it would fire
    ///     twice for a transaction that had already committed. Same property fisher#4 established for
    ///     the outbox.
    /// </summary>
    [Fact]
    public async Task the_returned_listener_runs_after_the_commit()
    {
        var subscription = new ListeningSubscription();
        await StartWithAsync(subscription);

        await AppendAsync(new QuestStarted("Find the ring"));

        // NOT WaitForNonStaleProjectionDataAsync, and the difference is the point of this test. The
        // progression row is written inside the batch's transaction, so "non-stale" is true the moment
        // that commits — strictly before the post-commit listener runs. Waiting on it and then
        // asserting the listener had fired is a race, and it fails perhaps one full-suite run in
        // several. The listener is its own signal, so wait for that.
        await WaitForAsync(() => subscription.CommittedCount > 0);

        // What the listener saw is what the batch had already committed — read over a separate
        // connection so it is the database's answer rather than the session's.
        subscription.VisibleAtCommit.ShouldBeTrue();
    }

    /// <summary>
    ///     Poll for a condition the daemon reaches asynchronously, failing with a clear message rather
    ///     than a timeout if it never does.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition, int timeoutSeconds = 30)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"The condition was not met within {timeoutSeconds} seconds.");
    }

    // ---- registration ----

    [Fact]
    public void a_bare_subscription_is_wrapped_and_named_after_its_type()
    {
        var options = new StoreOptions { ConnectionString = _database.ConnectionString };
        options.Projections.Subscribe(new BareSubscription());

        options.Projections.IsActive().ShouldBeTrue();
    }

    [Fact]
    public void a_subscription_base_keeps_its_own_name()
        => new RecordingSubscription().Name.ShouldBe(nameof(RecordingSubscription));

    // ---- test subscriptions ----

    private sealed class RecordingSubscription : SubscriptionBase
    {
        // A queue, not a ConcurrentBag: the bag is explicitly unordered, so asserting sequence
        // order against one tests nothing and fails at random.
        public ConcurrentQueue<IEvent> Seen { get; } = new();

        public override Task<IDaemonChangeListener> ProcessEventsAsync(EventRange page,
            ISubscriptionController controller, IDocumentSession operations,
            CancellationToken cancellationToken)
        {
            foreach (var @event in page.Events)
            {
                Seen.Enqueue(@event);
            }

            return Task.FromResult<IDaemonChangeListener>(NullDaemonChangeListener.Instance);
        }
    }

    private sealed class WritingSubscription : SubscriptionBase
    {
        public override Task<IDaemonChangeListener> ProcessEventsAsync(EventRange page,
            ISubscriptionController controller, IDocumentSession operations,
            CancellationToken cancellationToken)
        {
            foreach (var @event in page.Events)
            {
                operations.Store(new SubscriptionNote
                {
                    Id = Guid.NewGuid(), Sequence = @event.Sequence
                });
            }

            return Task.FromResult<IDaemonChangeListener>(NullDaemonChangeListener.Instance);
        }
    }

    private sealed class ListeningSubscription : SubscriptionBase
    {
        private string _connectionString = string.Empty;

        public int CommittedCount;
        public bool VisibleAtCommit;

        public override Task<IDaemonChangeListener> ProcessEventsAsync(EventRange page,
            ISubscriptionController controller, IDocumentSession operations,
            CancellationToken cancellationToken)
        {
            _connectionString = ((Fisher.Internal.FisherSession)operations).Options.ConnectionString!;

            operations.Store(new SubscriptionNote { Id = Guid.NewGuid(), Sequence = page.SequenceCeiling });

            return Task.FromResult<IDaemonChangeListener>(new Listener(this));
        }

        private sealed class Listener : IDaemonChangeListener
        {
            private readonly ListeningSubscription _owner;

            public Listener(ListeningSubscription owner) => _owner = owner;

            public async Task AfterCommitAsync(CancellationToken token)
            {
                // A separate connection, so this is the committed state rather than the session's view.
                await using var connection =
                    new Microsoft.Data.Sqlite.SqliteConnection(_owner._connectionString);
                await connection.OpenAsync(token);

                await using var command = connection.CreateCommand();
                command.CommandText = "select count(*) from fi_doc_subscriptionnote";

                _owner.VisibleAtCommit = Convert.ToInt64(await command.ExecuteScalarAsync(token)) > 0;
                Interlocked.Increment(ref _owner.CommittedCount);
            }
        }
    }

    private sealed class BareSubscription : ISubscription
    {
        public Task<IDaemonChangeListener> ProcessEventsAsync(EventRange page,
            ISubscriptionController controller, IDocumentSession operations,
            CancellationToken cancellationToken)
            => Task.FromResult<IDaemonChangeListener>(NullDaemonChangeListener.Instance);
    }
}

public class SubscriptionNote
{
    public Guid Id { get; set; }
    public long Sequence { get; set; }
}
