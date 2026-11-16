using System.Collections.Concurrent;
using Fisher.Events.Daemon;
using Fisher.Subscriptions;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Daemon;

public interface IQuestMilestone
{
    string Name { get; }
}

public abstract record QuestLogEntry(string Name);

public record WaypointReached(string Name) : QuestLogEntry(Name), IQuestMilestone;

public record TreasureFound(string Name) : QuestLogEntry(Name), IQuestMilestone;

public record CampMade(string Name);

/// <summary>
///     Folds through an interface: its declared event types, and so the async shard's filter, name
///     <see cref="IQuestMilestone" /> rather than either concrete event.
/// </summary>
public class MilestoneLog
{
    public Guid Id { get; set; }
    public List<string> Milestones { get; set; } = new();

    public void Apply(IQuestMilestone milestone) => Milestones.Add(milestone.Name);
}

/// <summary>
///     fisher#575 / weasel#845: a shard filtered on an interface or abstract event type has to read the
///     concrete events behind it.
/// </summary>
/// <remarks>
///     The filter is pushed into SQL as <c>type in (...)</c>, and a stored row carries its concrete
///     type's alias. Before Weasel 10.0.0-alpha.4, <c>EventTypeAllowList.For</c> mapped
///     <c>IncludeType&lt;IQuestMilestone&gt;()</c> (or <c>Apply(IQuestMilestone)</c>) to the
///     interface's own alias, which no row carries: the page came back empty, the ceiling claimed the
///     high-water mark, and the shard reported itself caught up with nothing applied and nothing
///     logged. The allow list now admits every type in that case, as Marten's loader always has, and
///     the projection or subscription does its own dispatch after hydration.
/// </remarks>
public class interface_event_type_filtering : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("interface-filter");
    private readonly MilestoneSubscription _subscription = new();
    private DocumentStore _store = null!;
    private IProjectionDaemon? _daemon;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<MilestoneLog>(SnapshotLifecycle.Async);
            options.Projections.Subscribe(_subscription);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_daemon is not null)
        {
            await _daemon.StopAllAsync();
            _daemon.Dispose();
        }

        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<Guid> AppendAsync(params object[] events)
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Events.StartStream<MilestoneLog>(streamId, events);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return streamId;
    }

    private static EventRequest Request(long floor, long highWater)
        => new()
        {
            Floor = floor,
            HighWater = highWater,
            BatchSize = 100,
            Name = new ShardName("milestones"),
            ErrorOptions = new ErrorHandlingOptions()
        };

    private async Task<EventPage> LoadFilteredOnAsync(Type includedType)
    {
        var filtering = new EventFilterable();
        filtering.IncludeType(includedType);

        var loader = new FisherEventLoader(_store.Database, _store.Options, filtering);
        return await loader.LoadAsync(Request(0, 3), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_subscription_filtered_on_an_interface_reads_the_events_that_implement_it()
    {
        await AppendAsync(new WaypointReached("Weathertop"), new CampMade("Bree"), new TreasureFound("Mithril"));

        var page = await LoadFilteredOnAsync(typeof(IQuestMilestone));

        page.Where(x => x.Data is IQuestMilestone)
            .Select(x => ((IQuestMilestone)x.Data).Name)
            .ShouldBe(["Weathertop", "Mithril"]);
    }

    [Fact]
    public async Task a_subscription_filtered_on_an_abstract_type_reads_its_concrete_subtypes()
    {
        await AppendAsync(new WaypointReached("Weathertop"), new CampMade("Bree"), new TreasureFound("Mithril"));

        var page = await LoadFilteredOnAsync(typeof(QuestLogEntry));

        page.Where(x => x.Data is QuestLogEntry)
            .Select(x => ((QuestLogEntry)x.Data).Name)
            .ShouldBe(["Weathertop", "Mithril"]);
    }

    [Fact]
    public async Task an_async_projection_that_applies_an_interface_sees_its_events()
    {
        var streamId = await AppendAsync(new WaypointReached("Weathertop"), new CampMade("Bree"),
            new TreasureFound("Mithril"));

        _daemon = await _store.BuildProjectionDaemonAsync();
        await _daemon.StartAllAsync();
        await _store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

        await using var query = _store.LightweightSession();
        var log = await query.LoadAsync<MilestoneLog>(streamId, TestContext.Current.CancellationToken);

        log.ShouldNotBeNull();
        log.Milestones.ShouldBe(["Weathertop", "Mithril"]);
    }

    [Fact]
    public async Task a_subscription_that_includes_an_interface_sees_its_events_through_the_daemon()
    {
        await AppendAsync(new WaypointReached("Weathertop"), new CampMade("Bree"), new TreasureFound("Mithril"));

        _daemon = await _store.BuildProjectionDaemonAsync();
        await _daemon.StartAllAsync();
        await _store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

        _subscription.Seen
            .Select(x => x.Data)
            .OfType<IQuestMilestone>()
            .Select(x => x.Name)
            .ShouldBe(["Weathertop", "Mithril"]);

        var progress = await _store.Database
            .ProjectionProgressFor(new ShardName(_subscription.Name), TestContext.Current.CancellationToken);

        progress.ShouldBe(3);
    }

    /// <summary>
    ///     <c>IncludeType&lt;IQuestMilestone&gt;()</c>: the subscription-side twin of
    ///     <see cref="MilestoneLog" />'s <c>Apply(IQuestMilestone)</c>.
    /// </summary>
    private sealed class MilestoneSubscription : SubscriptionBase
    {
        public MilestoneSubscription()
        {
            IncludeType<IQuestMilestone>();
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
}
