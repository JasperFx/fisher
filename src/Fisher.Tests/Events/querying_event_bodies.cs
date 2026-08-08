using JasperFx;

namespace Fisher.Tests.Events;

/// <summary>
///     <c>QueryEventDataAsync&lt;T&gt;</c> — fisher#41.
/// </summary>
/// <remarks>
///     The counterpart to <c>QueryEventsAsync</c>, which queries an event's metadata. That method's
///     doc comment says a body member is unreachable "because the body is JSON of a type the row only
///     names" — true of <c>IEvent</c> in general, and false once the caller names the type.
/// </remarks>
public class querying_event_bodies : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("event-bodies");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
        });
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        session.Events.StartStream(Guid.NewGuid(),
            new Landed("Trout", 3, At(1)),
            new Landed("Pike", 11, At(2)),
            new Lost("snagged"));
        session.Events.StartStream(Guid.NewGuid(),
            new Landed("Chub", 2, At(3)),
            new Landed("Trout", 7, At(4)));
        await session.SaveChangesAsync(Token);
    }

    private static DateTimeOffset At(int day) => new(2026, 8, day, 9, 0, 0, TimeSpan.Zero);

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task query_by_a_body_member()
    {
        await using var session = _store.LightweightSession();

        var trout = await session.Events.QueryEventDataAsync<Landed>(x => x.Species == "Trout", Token);

        trout.Select(x => x.Weight).ShouldBe([3, 7]);
    }

    [Fact]
    public async Task a_range_over_a_body_member()
    {
        await using var session = _store.LightweightSession();

        (await session.Events.QueryEventDataAsync<Landed>(x => x.Weight > 5, Token))
            .Select(x => x.Species).ShouldBe(["Pike", "Trout"]);
    }

    /// <summary>
    ///     A timestamp inside an event body needs fisher#1's <c>strftime</c> normalisation exactly as a
    ///     document member does — it is the same JSON written by the same serializer.
    /// </summary>
    [Fact]
    public async Task a_range_over_a_body_timestamp()
    {
        await using var session = _store.LightweightSession();

        (await session.Events.QueryEventDataAsync<Landed>(x => x.When >= At(3), Token))
            .Select(x => x.Species).ShouldBe(["Chub", "Trout"]);
    }

    /// <summary>
    ///     The type filter is what keeps a member name shared by two event types from matching both.
    /// </summary>
    [Fact]
    public async Task only_events_of_the_named_type_are_considered()
    {
        await using var session = _store.LightweightSession();

        (await session.Events.QueryEventDataAsync<Lost>(x => x.Reason == "snagged", Token))
            .ShouldHaveSingleItem();

        (await session.Events.QueryEventDataAsync<Landed>(x => x.Species == "snagged", Token))
            .ShouldBeEmpty();
    }

    /// <summary>
    ///     An event type with a member called <c>Id</c> must not have it resolved to
    ///     <c>fi_events.id</c>, which is the event's identity rather than the body's — that would
    ///     compare against the wrong column and return rows rather than an error.
    /// </summary>
    [Fact]
    public async Task a_body_member_called_id_is_not_the_events_own_id()
    {
        var marker = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(Guid.NewGuid(), new Tagged(marker));
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();

        (await query.Events.QueryEventDataAsync<Tagged>(x => x.Id == marker, Token))
            .ShouldHaveSingleItem().Id.ShouldBe(marker);
    }

    [Fact]
    public async Task no_matches()
    {
        await using var session = _store.LightweightSession();

        (await session.Events.QueryEventDataAsync<Landed>(x => x.Species == "Barracuda", Token))
            .ShouldBeEmpty();
    }

    /// <summary>
    ///     Asking about an event type must not give it a document table — the mapping is constructed
    ///     rather than registered.
    /// </summary>
    [Fact]
    public async Task querying_a_body_does_not_register_a_document_type()
    {
        await using var session = _store.LightweightSession();
        await session.Events.QueryEventDataAsync<Landed>(x => x.Weight > 0, Token);

        _store.Options.Schema.HasMappingFor(typeof(Landed)).ShouldBeFalse();
    }

    public record Landed(string Species, int Weight, DateTimeOffset When);

    public record Lost(string Reason);

    public record Tagged(Guid Id);
}
