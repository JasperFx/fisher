using System.Collections.Concurrent;
using Fisher.Subscriptions;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Events;

public sealed record ShardCounted(Guid TallyId, int Amount);

public sealed class ShardCounter
{
    public Guid Id { get; set; }
    public int Total { get; set; }

    public static ShardCounter Create(ShardCounted e) => new() { Total = e.Amount };

    public void Apply(ShardCounted e) => Total += e.Amount;
}

public sealed class ShardCounterView
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public static ShardCounterView Create(ShardCounted e) => new() { Count = 1 };

    public void Apply(ShardCounted e) => Count++;
}

public sealed class ShardTally
{
    public Guid Id { get; set; }
    public int Total { get; set; }
    public int Events { get; set; }
}

public partial class ShardTallyProjection : Fisher.Projections.MultiStreamProjection<ShardTally, Guid>
{
    public ShardTallyProjection()
    {
        Identity<ShardCounted>(x => x.TallyId);
    }

    public void Apply(ShardTally tally, ShardCounted e)
    {
        tally.Total += e.Amount;
        tally.Events++;
    }
}

public sealed class ShardCountingSubscription : SubscriptionBase
{
    public ConcurrentBag<long> Sequences { get; } = new();

    public override Task<IDaemonChangeListener> ProcessEventsAsync(EventRange page,
        ISubscriptionController controller, IDocumentSession operations, CancellationToken cancellationToken)
    {
        foreach (var e in page.Events.Where(x => x.Data is ShardCounted))
        {
            Sequences.Add(e.Sequence);
        }

        return Task.FromResult<IDaemonChangeListener>(NullDaemonChangeListener.Instance);
    }
}

/// <summary>
///     fisher#634, port of marten#6050. Compaction replaces a stream's last event with a Compacted&lt;T&gt; and
///     deletes the events below it. A single-stream projection of T fast-forwards over the snapshot, but an
///     async multi-stream projection or a subscription that had not reached those events yet never saw them.
/// </summary>
public class compaction_and_async_shards : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("compaction-shards");
    private readonly ShardCountingSubscription _subscription = new();
    private DocumentStore? _store;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private DocumentStore Store => _store ?? throw new InvalidOperationException("Call StartAsync first");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        _database.Dispose();
    }

    private async Task StartAsync(Action<StoreOptions> configure)
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            configure(options);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    private async Task<(Guid Stream, Guid Tally)> ThreeCountedEvents()
    {
        var stream = Guid.NewGuid();
        var tally = Guid.NewGuid();

        await using var session = Store.LightweightSession();
        session.Events.StartStream<ShardCounter>(stream, new ShardCounted(tally, 1), new ShardCounted(tally, 2),
            new ShardCounted(tally, 3));
        await session.SaveChangesAsync(Token);

        return (stream, tally);
    }

    private async Task Compact(Guid stream)
    {
        await using var session = Store.LightweightSession();
        await session.Events.CompactStreamAsync<ShardCounter>(stream);
        await session.SaveChangesAsync(Token);
    }

    private async Task<int> CountedEventsIn(Guid stream)
    {
        await using var query = Store.QuerySession();
        var events = await query.Events.FetchStreamAsync(stream, token: Token);
        return events.Count(x => x.Data is ShardCounted);
    }

    private async Task CatchUp()
    {
        using var daemon = await Store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();
        await Store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);
        await daemon.StopAllAsync();
    }

    [Fact]
    public async Task compaction_is_refused_while_an_async_projection_and_a_subscription_have_not_processed_the_events()
    {
        await StartAsync(options =>
        {
            options.Projections.Add(new ShardTallyProjection(), ProjectionLifecycle.Async);
            options.Projections.Subscribe(_subscription);
        });

        var (stream, tallyId) = await ThreeCountedEvents();

        // Before the guard this compacted, and the projection and the subscription never saw the three events:
        // the tally was never created and the subscription saw nothing
        var refused = await Should.ThrowAsync<InvalidOperationException>(() => Compact(stream));
        refused.Message.ShouldContain(nameof(ShardTally));
        refused.Message.ShouldContain(nameof(ShardCountingSubscription));
        refused.Message.ShouldContain("Nothing was compacted");

        (await CountedEventsIn(stream)).ShouldBe(3);

        await CatchUp();

        await using var query = Store.QuerySession();
        (await query.LoadAsync<ShardTally>(tallyId, Token))!.Events.ShouldBe(3);
        _subscription.Sequences.Count.ShouldBe(3);
    }

    [Fact]
    public async Task compaction_is_refused_while_only_a_subscription_is_behind()
    {
        await StartAsync(options => options.Projections.Subscribe(_subscription));

        var (stream, _) = await ThreeCountedEvents();

        await Should.ThrowAsync<InvalidOperationException>(() => Compact(stream));
        (await CountedEventsIn(stream)).ShouldBe(3);
    }

    [Fact]
    public async Task compaction_proceeds_once_every_shard_has_processed_the_events()
    {
        await StartAsync(options =>
        {
            options.Projections.Add(new ShardTallyProjection(), ProjectionLifecycle.Async);
            options.Projections.Subscribe(_subscription);
        });

        var (stream, tallyId) = await ThreeCountedEvents();
        await CatchUp();

        await Compact(stream);
        (await CountedEventsIn(stream)).ShouldBe(0);

        await using (var session = Store.LightweightSession())
        {
            session.Events.Append(stream, new ShardCounted(tallyId, 4), new ShardCounted(tallyId, 5));
            await session.SaveChangesAsync(Token);
        }

        await CatchUp();

        await using var query = Store.QuerySession();
        var tally = await query.LoadAsync<ShardTally>(tallyId, Token);
        tally!.Events.ShouldBe(5);
        tally.Total.ShouldBe(15);
        _subscription.Sequences.Count.ShouldBe(5);

        (await query.Events.AggregateStreamAsync<ShardCounter>(stream, token: Token))!.Total.ShouldBe(15);
    }

    [Fact]
    public async Task the_compacted_aggregates_own_async_projection_being_behind_does_not_block_compaction()
    {
        // A single-stream projection of the compacted type fast-forwards over the Compacted<T>
        await StartAsync(options => options.Projections.Snapshot<ShardCounter>(SnapshotLifecycle.Async));

        var (stream, _) = await ThreeCountedEvents();

        await Compact(stream);
        (await CountedEventsIn(stream)).ShouldBe(0);

        await CatchUp();

        await using var query = Store.QuerySession();
        (await query.LoadAsync<ShardCounter>(stream, Token))!.Total.ShouldBe(6);
    }

    [Fact]
    public async Task another_aggregates_async_projection_of_the_stream_still_blocks_compaction()
    {
        await StartAsync(options =>
        {
            options.Projections.Snapshot<ShardCounter>(SnapshotLifecycle.Async);
            options.Projections.Snapshot<ShardCounterView>(SnapshotLifecycle.Async);
        });

        var (stream, _) = await ThreeCountedEvents();

        var refused = await Should.ThrowAsync<InvalidOperationException>(() => Compact(stream));
        refused.Message.ShouldContain(nameof(ShardCounterView));
        refused.Message.ShouldNotContain(nameof(ShardCounter) + ":");
    }
}
