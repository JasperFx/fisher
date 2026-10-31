using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Tags;

namespace Fisher.Tests.Events;

public record DockTag(Guid Value);

public record ShipBerthed(string Name);

[BoundaryAggregate]
public class DockLog
{
    public List<string> Ships { get; } = [];

    public void Apply(ShipBerthed e) => Ships.Add(e.Name);
}

/// <summary>
///     fisher#485, from marten#5677. The tag reads saw archived events while every other event read did
///     not, so a retired stream could still block or satisfy a boundary.
/// </summary>
public class tag_reads_exclude_archived : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("tags-archived");
    private readonly DockTag _dock = new(Guid.NewGuid());
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private EventTagQuery Query => new EventTagQuery().Or<DockTag>(_dock);

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.AddEventType<ShipBerthed>();
            options.Events.RegisterTagType<DockTag>("dock");
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<Guid> BerthAsync(string name)
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        var @event = session.Events.BuildEvent(new ShipBerthed(name));
        @event.WithTag(_dock);
        session.Events.StartStream(streamId, @event);
        await session.SaveChangesAsync(Token);

        return streamId;
    }

    private async Task ArchiveAsync(Guid streamId)
    {
        await using var session = _store.LightweightSession();
        session.Events.ArchiveStream(streamId);
        await session.SaveChangesAsync(Token);
    }

    [Fact]
    public async Task a_tag_query_returns_only_the_live_events()
    {
        await ArchiveAsync(await BerthAsync("Archived"));
        await BerthAsync("Live");

        await using var session = _store.LightweightSession();

        var events = await session.Events.QueryByTagsAsync(Query, Token);

        events.Select(x => ((ShipBerthed)x.Data).Name).ShouldBe(["Live"]);
    }

    /// <summary>The check a boundary is built on, which is what makes this more than a wrong count.</summary>
    [Fact]
    public async Task events_exist_is_false_once_they_are_archived()
    {
        var streamId = await BerthAsync("Archived");

        await using var session = _store.LightweightSession();
        (await session.Events.EventsExistAsync(Query, Token)).ShouldBeTrue();

        await ArchiveAsync(streamId);

        (await session.Events.EventsExistAsync(Query, Token)).ShouldBeFalse();
    }

    [Fact]
    public async Task an_aggregate_folds_only_the_live_events()
    {
        await ArchiveAsync(await BerthAsync("Archived"));
        await BerthAsync("Live");

        await using var session = _store.LightweightSession();

        var log = await session.Events.AggregateByTagsAsync<DockLog>(Query, Token);

        log!.Ships.ShouldBe(["Live"]);
    }

    [Fact]
    public async Task a_boundary_over_only_archived_events_is_an_empty_boundary()
    {
        await ArchiveAsync(await BerthAsync("Archived"));

        await using var session = _store.LightweightSession();

        var boundary = await session.Events.FetchForWritingByTags<DockLog>(Query, Token);

        boundary.Aggregate.ShouldBeNull();
    }

    /// <summary>
    ///     The commit-time half: an event archived before the commit is not a concurrent change to a
    ///     boundary. The live twin is what shows the guard is still armed.
    /// </summary>
    [Fact]
    public async Task the_boundary_check_at_commit_ignores_an_archived_event()
    {
        await BerthAsync("Seen");

        await using var session = _store.LightweightSession();
        var boundary = await session.Events.FetchForWritingByTags<DockLog>(Query, Token);

        await ArchiveAsync(await BerthAsync("Berthed meanwhile, then retired"));

        var @event = session.Events.BuildEvent(new ShipBerthed("Mine"));
        @event.WithTag(_dock);
        boundary.AppendOne(@event);

        await session.SaveChangesAsync(Token);
    }

    [Fact]
    public async Task the_boundary_check_at_commit_still_sees_a_live_event()
    {
        await BerthAsync("Seen");

        await using var session = _store.LightweightSession();
        var boundary = await session.Events.FetchForWritingByTags<DockLog>(Query, Token);

        await BerthAsync("Berthed meanwhile");

        var @event = session.Events.BuildEvent(new ShipBerthed("Mine"));
        @event.WithTag(_dock);
        boundary.AppendOne(@event);

        await Should.ThrowAsync<DcbConcurrencyException>(() => session.SaveChangesAsync(Token));
    }

    [Fact]
    public async Task un_archiving_brings_the_events_back()
    {
        var streamId = await BerthAsync("Returned");
        await ArchiveAsync(streamId);

        await using (var session = _store.LightweightSession())
        {
            session.Events.UnArchiveStream(streamId);
            await session.SaveChangesAsync(Token);
        }

        await using var reader = _store.LightweightSession();
        (await reader.Events.QueryByTagsAsync(Query, Token)).Count.ShouldBe(1);
    }
}
