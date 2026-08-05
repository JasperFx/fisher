using Fisher.Events.Daemon;
using Fisher.Tests.Events;
using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Daemon;

/// <summary>
///     The event loader the async daemon pages through.
/// </summary>
public class daemon_event_loading : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("loader");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private FisherEventLoader TheLoader => new(_store.Database, _store.Options);

    private static EventRequest Request(long floor, long highWater, int batchSize = 100)
        => new()
        {
            Floor = floor,
            HighWater = highWater,
            BatchSize = batchSize,
            Name = new ShardName("tally"),
            ErrorOptions = new ErrorHandlingOptions()
        };

    private async Task<Guid> AppendAsync(params object[] events)
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Events.StartStream(streamId, events);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return streamId;
    }

    [Fact]
    public async Task an_empty_store_yields_an_empty_page()
    {
        var page = await TheLoader.LoadAsync(Request(0, 0), TestContext.Current.CancellationToken);

        page.ShouldBeEmpty();
    }

    [Fact]
    public async Task events_load_in_sequence_order()
    {
        await AppendAsync(new QuestStarted("one"), new MemberJoined("Frodo"));
        await AppendAsync(new QuestStarted("two"));

        var page = await TheLoader.LoadAsync(Request(0, 3), TestContext.Current.CancellationToken);

        page.Count.ShouldBe(3);
        page.Select(x => x.Sequence).ShouldBe([1, 2, 3]);
    }

    /// <summary>
    ///     The floor is exclusive and the ceiling inclusive, which is what lets the daemon page by
    ///     handing back the last sequence it saw.
    /// </summary>
    [Fact]
    public async Task the_floor_is_exclusive_and_the_ceiling_inclusive()
    {
        await AppendAsync(new QuestStarted("one"), new QuestStarted("two"), new QuestStarted("three"));

        var page = await TheLoader.LoadAsync(Request(1, 2), TestContext.Current.CancellationToken);

        page.Select(x => x.Sequence).ShouldBe([2]);
    }

    [Fact]
    public async Task the_batch_size_bounds_the_page()
    {
        await AppendAsync(new QuestStarted("one"), new QuestStarted("two"), new QuestStarted("three"));

        var page = await TheLoader.LoadAsync(Request(0, 3, batchSize: 2), TestContext.Current.CancellationToken);

        page.Count.ShouldBe(2);

        // A full page means there may be more, so the ceiling is where the page ended rather than the
        // high-water mark — that is what makes the next request resume correctly.
        page.Ceiling.ShouldBe(2);
    }

    [Fact]
    public async Task a_partial_page_takes_the_high_water_mark_as_its_ceiling()
    {
        await AppendAsync(new QuestStarted("one"), new QuestStarted("two"));

        var page = await TheLoader.LoadAsync(Request(0, 2, batchSize: 10), TestContext.Current.CancellationToken);

        page.Ceiling.ShouldBe(2);
    }

    /// <summary>
    ///     Archived events are excluded — an archived stream is out of the projection's world, and the
    ///     <c>(is_archived, seq_id)</c> index exists so this predicate is not a scan.
    /// </summary>
    [Fact]
    public async Task archived_events_are_not_loaded()
    {
        var streamId = await AppendAsync(new QuestStarted("one"), new QuestStarted("two"));
        await AppendAsync(new QuestStarted("three"));

        await using (var session = _store.LightweightSession())
        {
            session.Events.ArchiveStream(streamId);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var page = await TheLoader.LoadAsync(Request(0, 3), TestContext.Current.CancellationToken);

        page.Count.ShouldBe(1);
        page.Single().Data.ShouldBeOfType<QuestStarted>().Name.ShouldBe("three");
    }

    /// <summary>
    ///     Each event keeps its own stream identity: a page spans streams, so taking it from the
    ///     hydration context would stamp every event with the same wrong id.
    /// </summary>
    [Fact]
    public async Task events_keep_the_stream_they_came_from()
    {
        var first = await AppendAsync(new QuestStarted("one"));
        var second = await AppendAsync(new QuestStarted("two"));

        var page = await TheLoader.LoadAsync(Request(0, 2), TestContext.Current.CancellationToken);

        page.Select(x => x.StreamId).ShouldBe([first, second]);
    }

    [Fact]
    public async Task events_are_fully_hydrated()
    {
        await AppendAsync(new QuestStarted("Destroy the ring"));

        var page = await TheLoader.LoadAsync(Request(0, 1), TestContext.Current.CancellationToken);

        var @event = page.Single();
        @event.Data.ShouldBeOfType<QuestStarted>().Name.ShouldBe("Destroy the ring");
        @event.Version.ShouldBe(1);
        @event.Sequence.ShouldBe(1);
        @event.Id.ShouldNotBe(Guid.Empty);
    }

    /// <summary>
    ///     A subscription that names its event types reads only those, and the filtered-out events
    ///     still count toward the page's range so paging does not stall on them.
    /// </summary>
    [Fact]
    public async Task an_event_type_filter_narrows_the_page()
    {
        await AppendAsync(new QuestStarted("one"), new MemberJoined("Frodo"), new QuestStarted("two"));

        var filtering = new EventFilterable();
        filtering.IncludeType<QuestStarted>();

        var loader = new FisherEventLoader(_store.Database, _store.Options, filtering);
        var page = await loader.LoadAsync(Request(0, 3), TestContext.Current.CancellationToken);

        page.Count.ShouldBe(2);
        page.ShouldAllBe(x => x.Data is QuestStarted);
    }
}
