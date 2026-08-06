using Fisher.Tests.Schema;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Protected;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Events;

public record CoinsEarned(int Amount);

public record CoinsSpent(int Amount);

public class Purse
{
    public Guid Id { get; set; }
    public int Balance { get; set; }

    public void Apply(CoinsEarned earned) => Balance += earned.Amount;

    public void Apply(CoinsSpent spent) => Balance -= spent.Amount;
}

/// <summary>
///     fisher#10 — collapsing a stream's history into a single <c>Compacted&lt;T&gt;</c> snapshot.
/// </summary>
public class compacting_streams : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("compacting");
    private DocumentStore _store = null!;

    private readonly TerritoryId _shire = new(Guid.NewGuid());

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.RegisterTagType<TerritoryId>("territory");
            options.Events.AddEventType(typeof(CoinsEarned));
            options.Events.AddEventType(typeof(CoinsSpent));
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task compacting_leaves_one_snapshot_event()
    {
        var streamId = await StartPurseAsync(new CoinsEarned(100), new CoinsSpent(30), new CoinsEarned(5));

        await using (var session = _store.LightweightSession())
        {
            await session.Events.CompactStreamAsync<Purse>(streamId);
        }

        await using var query = _store.LightweightSession();
        var events = await query.Events.FetchStreamAsync(streamId,
            token: TestContext.Current.CancellationToken);

        var only = events.ShouldHaveSingleItem();
        var compacted = only.Data.ShouldBeOfType<Compacted<Purse>>();
        compacted.Snapshot.Balance.ShouldBe(75);
        compacted.PreviousStreamId.ShouldBe(streamId);
    }

    /// <summary>
    ///     The snapshot keeps the last event's place, so the stream's version does not move and the
    ///     next append carries on from where it would have.
    /// </summary>
    [Fact]
    public async Task compacting_leaves_the_stream_version_alone()
    {
        var streamId = await StartPurseAsync(new CoinsEarned(100), new CoinsSpent(30), new CoinsEarned(5));

        await using (var session = _store.LightweightSession())
        {
            await session.Events.CompactStreamAsync<Purse>(streamId);
        }

        await using var query = _store.LightweightSession();

        var state = await query.Events.FetchStreamStateAsync(streamId, TestContext.Current.CancellationToken);
        state.ShouldNotBeNull();
        state.Version.ShouldBe(3);

        var events = await query.Events.FetchStreamAsync(streamId,
            token: TestContext.Current.CancellationToken);
        events[0].Version.ShouldBe(3);
    }

    /// <summary>
    ///     JasperFx's aggregator fast-forwards through a <c>Compacted&lt;T&gt;</c>, so live aggregation
    ///     over a compacted stream is the same answer as before — this is the property that makes
    ///     compacting invisible to readers.
    /// </summary>
    [Fact]
    public async Task aggregating_a_compacted_stream_gives_the_same_answer()
    {
        var streamId = await StartPurseAsync(new CoinsEarned(100), new CoinsSpent(30), new CoinsEarned(5));

        await using (var session = _store.LightweightSession())
        {
            (await session.Events.AggregateStreamAsync<Purse>(streamId,
                token: TestContext.Current.CancellationToken))!.Balance.ShouldBe(75);

            await session.Events.CompactStreamAsync<Purse>(streamId);
        }

        await using var query = _store.LightweightSession();
        var purse = await query.Events.AggregateStreamAsync<Purse>(streamId,
            token: TestContext.Current.CancellationToken);

        purse.ShouldNotBeNull();
        purse.Balance.ShouldBe(75);
    }

    [Fact]
    public async Task events_appended_after_compacting_fold_onto_the_snapshot()
    {
        var streamId = await StartPurseAsync(new CoinsEarned(100), new CoinsSpent(30));

        await using (var session = _store.LightweightSession())
        {
            await session.Events.CompactStreamAsync<Purse>(streamId);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Events.Append(streamId, new CoinsEarned(10));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        var purse = await query.Events.AggregateStreamAsync<Purse>(streamId,
            token: TestContext.Current.CancellationToken);

        purse!.Balance.ShouldBe(80);
    }

    /// <summary>
    ///     A bounded compaction keeps everything above the bound as real events.
    /// </summary>
    [Fact]
    public async Task compacting_to_a_version_keeps_what_came_after()
    {
        var streamId = await StartPurseAsync(new CoinsEarned(100), new CoinsSpent(30), new CoinsEarned(5));

        await using (var session = _store.LightweightSession())
        {
            await session.Events.CompactStreamAsync<Purse>(streamId, r => r.Version = 2);
        }

        await using var query = _store.LightweightSession();
        var events = await query.Events.FetchStreamAsync(streamId,
            token: TestContext.Current.CancellationToken);

        events.Count.ShouldBe(2);
        events[0].Data.ShouldBeOfType<Compacted<Purse>>().Snapshot.Balance.ShouldBe(70);
        events[1].Data.ShouldBeOfType<CoinsEarned>().Amount.ShouldBe(5);

        (await query.Events.AggregateStreamAsync<Purse>(streamId,
            token: TestContext.Current.CancellationToken))!.Balance.ShouldBe(75);
    }

    [Fact]
    public async Task compacting_an_already_compacted_stream_is_a_no_op()
    {
        var streamId = await StartPurseAsync(new CoinsEarned(100), new CoinsSpent(30));

        await using (var session = _store.LightweightSession())
        {
            await session.Events.CompactStreamAsync<Purse>(streamId);
        }

        Guid idAfterFirst;
        await using (var query = _store.LightweightSession())
        {
            idAfterFirst = (await query.Events.FetchStreamAsync(streamId,
                token: TestContext.Current.CancellationToken))[0].Id;
        }

        await using (var session = _store.LightweightSession())
        {
            await session.Events.CompactStreamAsync<Purse>(streamId);
        }

        await using var after = _store.LightweightSession();
        var events = await after.Events.FetchStreamAsync(streamId,
            token: TestContext.Current.CancellationToken);

        events.ShouldHaveSingleItem().Id.ShouldBe(idAfterFirst);
    }

    [Fact]
    public async Task compacting_a_stream_that_does_not_exist_does_nothing()
    {
        await using var session = _store.LightweightSession();

        await Should.NotThrowAsync(() => session.Events.CompactStreamAsync<Purse>(Guid.NewGuid()));
    }

    /// <summary>
    ///     The compacted events' tag rows have a real foreign key to <c>fi_events(seq_id)</c>, so this
    ///     is the ordering fisher#6 established reaching compaction through <c>DeleteEvents</c>.
    /// </summary>
    [Fact]
    public async Task compacting_clears_the_tag_rows_of_the_events_it_removes()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            var first = session.Events.BuildEvent(new CoinsEarned(100));
            first.WithTag(_shire);

            var second = session.Events.BuildEvent(new CoinsSpent(30));
            second.WithTag(_shire);

            session.Events.StartStream(streamId, first, second);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await TagRowCountAsync()).ShouldBe(2);

        await using (var session = _store.LightweightSession())
        {
            await session.Events.CompactStreamAsync<Purse>(streamId);
        }

        // Both go: the first event's row is deleted with the event, and the last event's row is
        // deleted by the replace, because a tag describes the event that was appended and the
        // replacement is a different event. Leaving that one behind would tag the Compacted<Purse>
        // snapshot as though it were the CoinsSpent it replaced.
        (await TagRowCountAsync()).ShouldBe(0);
    }

    // ---- the archiver hook ----

    [Fact]
    public async Task the_archiver_sees_the_events_before_they_are_removed()
    {
        var streamId = await StartPurseAsync(new CoinsEarned(100), new CoinsSpent(30), new CoinsEarned(5));

        var archiver = new RecordingArchiver();

        await using (var session = _store.LightweightSession())
        {
            await session.Events.CompactStreamAsync<Purse>(streamId, r => r.Archiver = archiver);
        }

        archiver.Seen.Count.ShouldBe(3);
        archiver.SequenceOnRequest.ShouldBe(archiver.Seen[^1].Sequence);

        // Still present when the archiver ran — it is handed live events, not a memory of them.
        archiver.CountAtCallTime.ShouldBe(3);
    }

    // ---- the tooling entry point ----

    /// <summary>
    ///     <c>IEventStore.CompactStreamAsync</c> has no type parameter, so it resolves the aggregate
    ///     from the stream row. Polecat throws here; Fisher does not, because the type is recorded.
    /// </summary>
    [Fact]
    public async Task the_untyped_entry_point_resolves_the_aggregate_from_the_stream()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream<Purse>(streamId, new CoinsEarned(100), new CoinsSpent(30));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await ((JasperFx.Events.IEventStore)_store).CompactStreamAsync(streamId,
            TestContext.Current.CancellationToken);

        await using var query = _store.LightweightSession();
        var events = await query.Events.FetchStreamAsync(streamId,
            token: TestContext.Current.CancellationToken);

        events.ShouldHaveSingleItem().Data.ShouldBeOfType<Compacted<Purse>>()
            .Snapshot.Balance.ShouldBe(70);
    }

    [Fact]
    public async Task the_untyped_entry_point_says_so_when_the_stream_names_no_aggregate()
    {
        var streamId = await StartPurseAsync(new CoinsEarned(100));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            ((JasperFx.Events.IEventStore)_store).CompactStreamAsync(streamId,
                TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("CompactStreamAsync<T>");
    }

    // ---- helpers ----

    private async Task<Guid> StartPurseAsync(params object[] events)
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Events.StartStream(streamId, events);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return streamId;
    }

    private async Task<long> TagRowCountAsync()
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = conn.CreateCommand();
        command.CommandText = "select count(*) from fi_event_tag_territory";

        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private sealed class RecordingArchiver : IEventsArchiver<IDocumentSession>
    {
        public List<IEvent> Seen { get; } = [];
        public long SequenceOnRequest { get; private set; }
        public int CountAtCallTime { get; private set; }

        public async Task MaybeArchiveAsync<T>(IDocumentSession operations, StreamCompactingRequest<T> request,
            IReadOnlyList<IEvent> events, CancellationToken cancellation) where T : class
        {
            Seen.AddRange(events);
            SequenceOnRequest = request.Sequence;

            CountAtCallTime = (await operations.Events.FetchStreamAsync(request.StreamId!.Value,
                token: cancellation)).Count;
        }
    }
}
