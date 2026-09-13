using System.Collections.Concurrent;
using Fisher.Linq;
using Fisher.Subscriptions;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

    /// <summary>
    ///     fisher#153 — the loader pushes a subscription's event-type allow-list into SQL, so rows
    ///     of other types never leave SQLite. Two facts a daemon-level test can pin: events of
    ///     non-allowed types do not reach the subscription, and the shard's recorded progress still
    ///     advances past a long run of filtered-out events all the way to the head — get the
    ///     ceiling accounting wrong under a server-side filter and this either stalls short of the
    ///     head or skips the matching event entirely.
    /// </summary>
    [Fact]
    public async Task a_filtered_subscription_sees_only_its_types_and_still_reaches_the_head()
    {
        var recorder = new FilteredRecordingSubscription();
        await StartWithAsync(recorder);

        await AppendAsync(new QuestStarted("Find the ring"));

        // A run of events the subscription filters out, appended after the one it wants —
        // progress has to advance past all of them.
        for (var i = 0; i < 25; i++)
        {
            await AppendAsync(new MemberJoined($"member-{i}"));
        }

        await _store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));

        recorder.Seen.Count.ShouldBe(1);
        recorder.Seen.Single().Data.ShouldBeOfType<QuestStarted>();

        var progress = await _store.Database
            .ProjectionProgressFor(new ShardName(recorder.Name), TestContext.Current.CancellationToken);

        progress.ShouldBe(26);
    }

    /*
     * Two facts retired in fisher#184, both superseded verbatim by the deepened SubscriptionCompliance
     * (jasperfx#768):
     *
     *   writes_through_the_supplied_session_are_committed_with_the_batch -> same name upstream, and
     *       stronger: it plants one note per event id and reads each back by id, where this counted
     *       rows.
     *   the_returned_listener_runs_after_the_commit -> the_listener_returned_by_the_subscription_
     *       runs_after_the_commit, carrying the same separate-connection probe AND the same warning
     *       about not waiting on non-staleness — the shared suite's remarks cite this file's note as
     *       the reason.
     *
     * The listener half is worth noting as a win rather than a swap: the return value of
     * ProcessEventsAsync used to be NullDaemonChangeListener in the shared compliance subscription,
     * which is exactly the shape a store that ignored it produces. It is now counted upstream, so
     * every store is held to running it.
     */

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

    private sealed class FilteredRecordingSubscription : SubscriptionBase
    {
        public FilteredRecordingSubscription()
        {
            IncludeType<QuestStarted>();
        }

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

    private sealed class BareSubscription : ISubscription
    {
        public Task<IDaemonChangeListener> ProcessEventsAsync(EventRange page,
            ISubscriptionController controller, IDocumentSession operations,
            CancellationToken cancellationToken)
            => Task.FromResult<IDaemonChangeListener>(NullDaemonChangeListener.Instance);
    }
}

/// <summary>
///     A subscription running under the <em>hosted</em> daemon — <c>AddAsyncDaemon()</c>, which is the
///     route the documentation names and the only one an application actually takes.
/// </summary>
/// <remarks>
///     <para>
///         <b>Written for jasperfx#827 and kept for a better reason: Fisher turned out to be immune,
///         and the immunity is one line deep.</b> That bug was
///         <c>JasperFxSubscriptionBase.BuildExecution</c>'s two overloads disagreeing — the
///         <c>ILoggerFactory</c> one passed the <em>database</em> where
///         <c>SubscriptionExecution&lt;T&gt;</c> resolves its <c>ISubscriptionRunner&lt;T&gt;</c> off
///         the <em>store</em>, so construction threw <c>ArgumentOutOfRangeException</c> and no
///         subscription could start on that path. <c>JasperFxAsyncDaemon.buildAgentForShard</c> takes
///         it whenever the daemon was built with a logger factory.
///     </para>
///     <para>
///         <b>Fisher never reaches it, because Fisher's daemon is built with an <c>ILogger</c> — on the
///         hosted path too.</b> <c>FisherDaemonHostedService</c> calls
///         <c>BuildProjectionDaemonsAsync(_logger)</c>, so the correct overload is the only one this
///         store has ever taken. <b>Verified rather than assumed</b>: this test was run against the
///         2.69.0 pin and passed, which is what turned "Fisher was broken and is now fixed" into "the
///         hosted path was untested here and happens to be safe".
///     </para>
///     <para>
///         <b>The gap it closes is therefore Fisher's own coverage, not Fisher's behaviour.</b> Every
///         subscription test above builds its daemon by hand, and so does
///         <c>SubscriptionCompliance</c> — which is why nothing on any store exercised the other
///         overload. So there was no fact anywhere saying a subscription runs under
///         <c>AddAsyncDaemon()</c>, which is the route the documentation names and the only one an
///         application takes. This is that fact, and it is what would catch Fisher if the daemon ever
///         moved to the logger-factory constructor.
///     </para>
///     <para>
///         Deliberately end-to-end and unglamorous — start the host, append, wait for the
///         subscription's own signal — because the failure mode it guards against is <em>starting</em>,
///         and any assertion reaching the events at all would catch it.
///     </para>
/// </remarks>
public class subscriptions_under_the_hosted_daemon : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("hosted-subscription");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task a_subscription_starts_and_sees_events_under_add_async_daemon()
    {
        var subscription = new HostedRecordingSubscription();

        using var host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddFisher(options =>
                {
                    options.ConnectionString = _database.ConnectionString;
                    options.AutoCreateSchemaObjects = AutoCreate.All;
                    options.Projections.Subscribe(subscription);
                })
                .ApplyAllDatabaseChangesOnStartup()
                .AddAsyncDaemon())
            .StartAsync(Token);

        try
        {
            var store = host.Services.GetRequiredService<IDocumentStore>();

            await using (var session = store.LightweightSession())
            {
                session.Events.StartStream<Quest>(Guid.NewGuid(),
                    new QuestStarted("Chart the Minch"), new QuestStarted("Chart the Solent"));
                await session.SaveChangesAsync(Token);
            }

            // Waited on the subscription's own signal rather than on non-staleness: the progression
            // row is written inside the batch's transaction, so non-stale becomes true strictly before
            // anything the subscription did is observable. Same trap fisher#232 records one seam over.
            await subscription.SawTwo.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

            subscription.Seen.Count.ShouldBe(2);
        }
        finally
        {
            await host.StopAsync(Token);
        }
    }

    private sealed class HostedRecordingSubscription : SubscriptionBase
    {
        public ConcurrentQueue<IEvent> Seen { get; } = new();

        public TaskCompletionSource SawTwo { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<IDaemonChangeListener> ProcessEventsAsync(EventRange page,
            ISubscriptionController controller, IDocumentSession operations,
            CancellationToken cancellationToken)
        {
            foreach (var @event in page.Events)
            {
                Seen.Enqueue(@event);
            }

            if (Seen.Count >= 2)
            {
                SawTwo.TrySetResult();
            }

            return Task.FromResult<IDaemonChangeListener>(NullDaemonChangeListener.Instance);
        }
    }
}
