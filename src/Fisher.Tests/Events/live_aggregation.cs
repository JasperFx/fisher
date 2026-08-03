using JasperFx;
using JasperFx.Events;

namespace Fisher.Tests.Events;

public class live_aggregation : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("aggregate");
    private DocumentStore _store = null!;
    private readonly Guid _streamId = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using var session = _store.LightweightSession();
        session.Events.StartStream<QuestParty>(_streamId,
            new QuestStarted("Find the ring"),
            new MemberJoined("Frodo"),
            new MemberJoined("Sam"),
            new MonsterSlain("Troll"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task folds_the_whole_stream()
    {
        await using var session = _store.LightweightSession();
        var party = await session.Events.AggregateStreamAsync<QuestParty>(_streamId,
            token: TestContext.Current.CancellationToken);

        party.ShouldNotBeNull();
        party!.Name.ShouldBe("Find the ring");
        party.Members.ShouldBe(["Frodo", "Sam"]);
        party.MonstersSlain.ShouldBe(1);
    }

    [Fact]
    public async Task assigns_the_stream_id_to_the_aggregate()
    {
        await using var session = _store.LightweightSession();
        var party = await session.Events.AggregateStreamAsync<QuestParty>(_streamId,
            token: TestContext.Current.CancellationToken);

        // Nothing in QuestParty's Create or Apply methods sets Id — it comes from the stream.
        party!.Id.ShouldBe(_streamId);
    }

    [Fact]
    public async Task folds_up_to_a_version()
    {
        await using var session = _store.LightweightSession();
        var party = await session.Events.AggregateStreamAsync<QuestParty>(_streamId, 3,
            token: TestContext.Current.CancellationToken);

        party!.Members.ShouldBe(["Frodo", "Sam"]);
        party.MonstersSlain.ShouldBe(0);
    }

    [Fact]
    public async Task folds_up_to_a_timestamp()
    {
        await using var session = _store.LightweightSession();

        var current = await session.Events.AggregateStreamAsync<QuestParty>(_streamId,
            timestamp: DateTimeOffset.UtcNow.AddMinutes(5), token: TestContext.Current.CancellationToken);
        var before = await session.Events.AggregateStreamAsync<QuestParty>(_streamId,
            timestamp: DateTimeOffset.UtcNow.AddMinutes(-5), token: TestContext.Current.CancellationToken);

        current!.Members.Count.ShouldBe(2);
        before.ShouldBeNull();
    }

    [Fact]
    public async Task continues_from_supplied_state()
    {
        await using var session = _store.LightweightSession();

        var state = new QuestParty { Id = _streamId, Name = "Find the ring" };
        state.Members.Add("Frodo");

        // Everything through version 2 is already folded into state, so pick up at version 3.
        var party = await session.Events.AggregateStreamAsync(_streamId, state: state, fromVersion: 3,
            token: TestContext.Current.CancellationToken);

        party!.Members.ShouldBe(["Frodo", "Sam"]);
        party.MonstersSlain.ShouldBe(1);
    }

    [Fact]
    public async Task an_empty_stream_aggregates_to_null()
    {
        await using var session = _store.LightweightSession();
        var party = await session.Events.AggregateStreamAsync<QuestParty>(Guid.NewGuid(),
            token: TestContext.Current.CancellationToken);

        party.ShouldBeNull();
    }

    [Fact]
    public async Task an_empty_stream_returns_the_supplied_state_untouched()
    {
        await using var session = _store.LightweightSession();

        var state = new QuestParty { Name = "Untouched" };
        var party = await session.Events.AggregateStreamAsync(Guid.NewGuid(), state: state,
            token: TestContext.Current.CancellationToken);

        party.ShouldBeSameAs(state);
    }

    [Fact]
    public async Task a_deleting_event_aggregates_to_null()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream<QuestParty>(streamId,
                new QuestStarted("Doomed"), new MemberJoined("Boromir"), new QuestEnded());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        (await query.Events.AggregateStreamAsync<QuestParty>(streamId,
            token: TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task an_aggregate_with_no_identity_member_is_rejected()
    {
        await using var session = _store.LightweightSession();

        // Live aggregation itself never needs the aggregate's own id, but JasperFx's source generator
        // keys the dispatcher it emits on (aggregate, id type) and silently skips a type it cannot
        // resolve one for. Failing here names the missing Id; letting it through would fail later
        // complaining about a missing generated dispatcher.
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => session.Events.AggregateStreamAsync<QuestTally>(_streamId,
                token: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("no identity member");
    }

    [Fact]
    public async Task registers_the_event_types_the_aggregate_handles()
    {
        await using var session = _store.LightweightSession();
        await session.Events.AggregateStreamAsync<QuestParty>(_streamId,
            token: TestContext.Current.CancellationToken);

        _store.Options.EventGraph.AllKnownEventTypes()
            .Select(x => x.EventType)
            .ShouldContain(typeof(QuestEnded));
    }
}

public class live_aggregation_with_string_identity : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("aggregate-string");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.StreamIdentity = StreamIdentity.AsString;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using var session = _store.LightweightSession();
        session.Events.StartStream<KeyedQuestParty>("quest/one",
            new QuestStarted("Find the ring"), new MemberJoined("Merry"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task folds_a_string_identified_stream()
    {
        await using var session = _store.LightweightSession();
        var party = await session.Events.AggregateStreamAsync<KeyedQuestParty>("quest/one",
            token: TestContext.Current.CancellationToken);

        party.ShouldNotBeNull();
        party!.Id.ShouldBe("quest/one");
        party.Members.ShouldBe(["Merry"]);
    }

    [Fact]
    public async Task an_aggregate_identified_by_guid_is_rejected_under_string_identity()
    {
        await using var session = _store.LightweightSession();

        // QuestParty.Id is a Guid, which under string stream identity could only ever come back empty.
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => session.Events.AggregateStreamAsync<QuestParty>("quest/one",
                token: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(nameof(QuestParty));
    }
}

public record QuestEnded;

/// <summary>
///     A self-aggregating single stream aggregate — no projection class, no registration. The Create
///     and Apply methods are what JasperFx discovers by convention.
/// </summary>
public class QuestParty
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<string> Members { get; } = new();
    public int MonstersSlain { get; set; }

    public static QuestParty Create(QuestStarted started) => new() { Name = started.Name };

    public void Apply(MemberJoined joined) => Members.Add(joined.Member);

    public void Apply(MonsterSlain slain) => MonstersSlain++;

    public bool ShouldDelete(QuestEnded ended) => true;
}

/// <summary>
///     The same shape keyed by string, for a store using <see cref="StreamIdentity.AsString" />.
/// </summary>
public class KeyedQuestParty
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> Members { get; } = new();

    public static KeyedQuestParty Create(QuestStarted started) => new() { Name = started.Name };

    public void Apply(MemberJoined joined) => Members.Add(joined.Member);
}

/// <summary>
///     Self-aggregating, but with no identity member at all.
/// </summary>
public class QuestTally
{
    public int Members { get; set; }

    public static QuestTally Create(QuestStarted started) => new();

    public void Apply(MemberJoined joined) => Members++;
}
