// Deliberately NOT importing Fisher.Linq, for the reason stream_state_queries gives.
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Documents;

namespace Fisher.Tests.Events;

public record PagedReading(string Gauge, int Level);

/// <summary>
///     fisher#599, the event-table twin of fisher#589: an operator written after <c>Take</c>/<c>Skip</c>
///     on <c>QueryRawEvents()</c>, <c>QueryStreamStates()</c> or <c>QueryEventData&lt;T&gt;()</c> applies
///     to the page, not to the rows the page was taken from.
/// </summary>
/// <remarks>
///     <para>
///         LINQ to Objects is the oracle throughout: each shape is one delegate, run against the store's
///         queryable and against the store's own unpaged answer held in memory. The data is chosen so
///         that applying an operator before the <c>LIMIT</c> gives a different answer from applying it
///         to the page — correlation ids, gauges and versions all tie, so a re-sort also has to keep the
///         page's order among its ties.
///     </para>
///     <para>
///         Stream <c>s3</c> is archived, and its one event is early enough to fall inside most pages:
///         the archived filter belongs inside the page, so a provider applying it outside would page
///         over the archived event and then drop it, returning a short page.
///     </para>
/// </remarks>
public class event_queries_after_paging : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("event_queries_after_paging");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // stream, correlation id, gauge, level — one save each, so sequences run 1..9 in this order.
    private static readonly (string Stream, string Correlation, string Gauge, int Level)[] Appends =
    [
        ("s1", "c2", "g-b", 3),
        ("s2", "c1", "g-a", 1),
        ("s3", "c3", "g-c", 4),
        ("s1", "c1", "g-a", 2),
        ("s4", "c2", "g-b", 5),
        ("s2", "c3", "g-c", 1),
        ("s5", "c1", "g-a", 4),
        ("s1", "c2", "g-b", 2),
        ("s6", "c3", "g-c", 6)
    ];

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.StreamIdentity = StreamIdentity.AsString;
            options.Events.EnableCorrelationId = true;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        foreach (var (stream, correlation, gauge, level) in Appends)
        {
            await using var session = _store.LightweightSession();
            session.CorrelationId = correlation;
            session.Events.Append(stream, new PagedReading(gauge, level));
            await session.SaveChangesAsync(Token);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Events.ArchiveStream("s3");
            await session.SaveChangesAsync(Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private IReadOnlyEventStore Reader => ((IEventStore)_store).OpenReadOnlyEventStore();

    // ---- raw events ----

    private static readonly Dictionary<string, Func<IQueryable<IEvent>, IQueryable<IEvent>>> EventShapes = new()
    {
        ["take-take"] = q => q.Take(5).Take(3),
        ["take-take-larger"] = q => q.Take(3).Take(5),
        ["skip-skip"] = q => q.Skip(2).Skip(3),
        ["take-skip"] = q => q.Take(5).Skip(2),
        ["take-skip-past-the-page"] = q => q.Take(3).Skip(5),
        ["skip-take"] = q => q.Skip(2).Take(3),
        ["skip-take-skip-take"] = q => q.Skip(1).Take(6).Skip(2).Take(3),
        ["negative-take"] = q => q.Take(-1),
        ["take-where"] = q => q.Take(5).Where(x => x.CorrelationId == "c1"),
        ["skip-where"] = q => q.Skip(3).Where(x => x.CorrelationId == "c1"),
        ["ordered-take-where"] = q => q.OrderByDescending(x => x.Sequence).Take(5).Where(x => x.CorrelationId == "c2"),
        ["take-where-where"] = q => q.Take(6).Where(x => x.CorrelationId != "c3").Where(x => x.Sequence > 1),
        ["take-order-by"] = q => q.Take(6).OrderBy(x => x.CorrelationId),
        ["ordered-take-order-by"] = q => q.OrderByDescending(x => x.Sequence).Take(6).OrderBy(x => x.CorrelationId),
        ["take-order-by-then-by"] =
            q => q.Take(7).OrderByDescending(x => x.CorrelationId).ThenByDescending(x => x.Version),
        ["take-where-take"] = q => q.Take(4).Where(x => x.CorrelationId == "c2").Take(3),
        ["take-where-skip"] = q => q.Take(6).Where(x => x.CorrelationId != "c3").Skip(1),
        ["two-pages-deep"] =
            q => q.Take(7).Where(x => x.CorrelationId != "c3").Take(3).OrderByDescending(x => x.Sequence),
        ["take-zero-where"] = q => q.Take(0).Where(x => x.CorrelationId == "c1")
    };

    public static TheoryData<string> EventShapeNames => [..EventShapes.Keys];

    [Theory]
    [MemberData(nameof(EventShapeNames))]
    public async Task raw_events_match_linq_to_objects(string shape)
    {
        var all = await Reader.QueryRawEvents().ToListAsync(Token);
        all.Select(x => x.Sequence).ShouldBe([1, 2, 4, 5, 6, 7, 8, 9]); // the premise: s3 is archived

        var actual = await EventShapes[shape](Reader.QueryRawEvents()).ToListAsync(Token);
        var expected = EventShapes[shape](all.AsQueryable()).ToList();

        actual.Select(x => x.Sequence).ShouldBe(expected.Select(x => x.Sequence));
    }

    [Fact]
    public async Task raw_event_terminals_read_the_page()
    {
        var all = await Reader.QueryRawEvents().ToListAsync(Token);
        IQueryable<IEvent> Page(IQueryable<IEvent> q) => q.Take(4);

        (await Page(Reader.QueryRawEvents()).CountAsync(x => x.CorrelationId == "c2", Token))
            .ShouldBe(Page(all.AsQueryable()).Count(x => x.CorrelationId == "c2"));

        (await Page(Reader.QueryRawEvents()).AnyAsync(x => x.Sequence > 6, Token))
            .ShouldBe(Page(all.AsQueryable()).Any(x => x.Sequence > 6));

        (await Page(Reader.QueryRawEvents()).FirstOrDefaultAsync(x => x.CorrelationId == "c3", Token))
            .ShouldBe(Page(all.AsQueryable()).FirstOrDefault(x => x.CorrelationId == "c3"));

        (await Reader.QueryRawEvents().Take(6).Skip(2).CountAsync(Token)).ShouldBe(4);

        (await Reader.QueryRawEvents().Take(6).Where(x => x.CorrelationId == "c1").CountAsync(Token))
            .ShouldBe(all.Take(6).Count(x => x.CorrelationId == "c1"));

        (await Reader.QueryRawEvents().Take(0).FirstOrDefaultAsync(Token)).ShouldBeNull();
    }

    // ---- stream states ----

    // Every shape orders before it pages: the provider adds no order of its own to stream states, so an
    // unordered page has no defined membership to compare.
    private static readonly Dictionary<string, Func<IQueryable<StreamState>, IQueryable<StreamState>>>
        StreamShapes = new()
        {
            ["take-skip"] = q => q.OrderBy(x => x.Key).Take(5).Skip(2),
            ["skip-take-take"] = q => q.OrderBy(x => x.Key).Skip(1).Take(4).Take(2),
            ["take-where"] = q => q.OrderBy(x => x.Key).Take(4).Where(x => x.Version == 1),
            ["skip-take-where"] = q => q.OrderBy(x => x.Key).Skip(1).Take(4).Where(x => x.Version == 1),
            ["take-order-by"] = q => q.OrderBy(x => x.Key).Take(5).OrderBy(x => x.Version),
            ["take-order-by-descending"] = q => q.OrderByDescending(x => x.Key).Take(5).OrderByDescending(x => x.Version),
            ["take-order-by-then-by"] =
                q => q.OrderBy(x => x.Key).Take(5).OrderByDescending(x => x.Version).ThenByDescending(x => x.Key),
            ["take-where-take"] = q => q.OrderBy(x => x.Key).Take(3).Where(x => x.Version == 1).Take(5),
            ["two-keys-take-order-by"] =
                q => q.OrderByDescending(x => x.Version).ThenBy(x => x.Key).Take(3).OrderByDescending(x => x.Key),
            ["archived-in-the-page"] = q => q.OrderBy(x => x.Key).Take(3).Where(x => !x.IsArchived)
        };

    public static TheoryData<string> StreamShapeNames => [..StreamShapes.Keys];

    [Theory]
    [MemberData(nameof(StreamShapeNames))]
    public async Task stream_states_match_linq_to_objects(string shape)
    {
        var all = await Reader.QueryStreamStates().ToListAsync(Token);
        all.Count.ShouldBe(6);

        var actual = await StreamShapes[shape](Reader.QueryStreamStates()).ToListAsync(Token);
        var expected = StreamShapes[shape](all.AsQueryable()).ToList();

        actual.Select(x => x.Key).ShouldBe(expected.Select(x => x.Key));
    }

    [Fact]
    public async Task stream_state_terminals_read_the_page()
    {
        var all = await Reader.QueryStreamStates().ToListAsync(Token);
        IQueryable<StreamState> Page(IQueryable<StreamState> q) => q.OrderBy(x => x.Key).Take(3);

        (await Page(Reader.QueryStreamStates()).CountAsync(x => x.Version == 1, Token))
            .ShouldBe(Page(all.AsQueryable()).Count(x => x.Version == 1));

        (await Page(Reader.QueryStreamStates()).AnyAsync(x => x.Key == "s5", Token))
            .ShouldBe(Page(all.AsQueryable()).Any(x => x.Key == "s5"));

        (await Page(Reader.QueryStreamStates()).FirstOrDefaultAsync(x => x.Version == 1, Token))!.Key
            .ShouldBe(Page(all.AsQueryable()).First(x => x.Version == 1).Key);
    }

    // ---- event data ----

    private static readonly Dictionary<string, Func<IQueryable<PagedReading>, IQueryable<PagedReading>>>
        DataShapes = new()
        {
            ["take-skip"] = q => q.Take(5).Skip(3),
            ["take-where"] = q => q.Take(5).Where(x => x.Level > 2),
            ["take-order-by"] = q => q.Take(6).OrderBy(x => x.Gauge),
            ["take-where-order-by-descending"] = q => q.Take(5).Where(x => x.Gauge != "g-c").OrderByDescending(x => x.Level)
        };

    public static TheoryData<string> DataShapeNames => [..DataShapes.Keys];

    [Theory]
    [MemberData(nameof(DataShapeNames))]
    public async Task event_data_matches_linq_to_objects(string shape)
    {
        var all = await Reader.QueryEventData<PagedReading>().ToListAsync(Token);
        all.Count.ShouldBe(8);

        var actual = await DataShapes[shape](Reader.QueryEventData<PagedReading>()).ToListAsync(Token);
        var expected = DataShapes[shape](all.AsQueryable()).ToList();

        actual.ShouldBe(expected);
    }
}
