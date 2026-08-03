using Fisher.Exceptions;
using JasperFx;
using JasperFx.Events;

namespace Fisher.Tests.Events;

public class writing_to_aggregates : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("write-aggregate");
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
            new QuestStarted("Find the ring"), new MemberJoined("Frodo"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    // ---- FetchForWriting ----

    [Fact]
    public async Task fetches_the_aggregate_and_its_current_version()
    {
        await using var session = _store.LightweightSession();
        var stream = await session.Events.FetchForWriting<QuestParty>(_streamId,
            TestContext.Current.CancellationToken);

        stream.Aggregate.ShouldNotBeNull();
        stream.Aggregate!.Members.ShouldBe(["Frodo"]);
        stream.Id.ShouldBe(_streamId);
        stream.StartingVersion.ShouldBe(2);
        stream.CurrentVersion.ShouldBe(2);
    }

    [Fact]
    public async Task appends_through_the_stream_and_commits()
    {
        await using (var session = _store.LightweightSession())
        {
            var stream = await session.Events.FetchForWriting<QuestParty>(_streamId,
                TestContext.Current.CancellationToken);

            stream.AppendOne(new MemberJoined("Sam"));
            stream.CurrentVersion.ShouldBe(3);

            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        var party = await query.Events.AggregateStreamAsync<QuestParty>(_streamId,
            token: TestContext.Current.CancellationToken);

        party!.Members.ShouldBe(["Frodo", "Sam"]);
    }

    [Fact]
    public async Task fetching_a_stream_that_does_not_exist_yet_starts_it()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            var stream = await session.Events.FetchForWriting<QuestParty>(streamId,
                TestContext.Current.CancellationToken);

            stream.Aggregate.ShouldBeNull();
            stream.StartingVersion.ShouldBe(0);

            stream.AppendMany(new QuestStarted("A new quest"), new MemberJoined("Pippin"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        var state = await query.Events.FetchStreamStateAsync(streamId, TestContext.Current.CancellationToken);

        state!.Version.ShouldBe(2);
        state.AggregateType.ShouldBe(typeof(QuestParty));
    }

    [Fact]
    public async Task honours_an_expected_version()
    {
        await using var session = _store.LightweightSession();

        var stream = await session.Events.FetchForWriting<QuestParty>(_streamId, 2,
            TestContext.Current.CancellationToken);
        stream.Aggregate.ShouldNotBeNull();

        await Should.ThrowAsync<EventStreamUnexpectedMaxEventIdException>(
            () => session.Events.FetchForWriting<QuestParty>(_streamId, 5,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task two_fetches_in_one_session_share_the_pending_stream()
    {
        await using (var session = _store.LightweightSession())
        {
            var first = await session.Events.FetchForWriting<QuestParty>(_streamId,
                TestContext.Current.CancellationToken);
            first.AppendOne(new MemberJoined("Sam"));

            // Fisher tracks pending streams in a dictionary keyed by identity, so a second fetch must
            // reuse the tracked action rather than replacing it and losing the append above.
            var second = await session.Events.FetchForWriting<QuestParty>(_streamId,
                TestContext.Current.CancellationToken);
            second.AppendOne(new MemberJoined("Merry"));

            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        var party = await query.Events.AggregateStreamAsync<QuestParty>(_streamId,
            token: TestContext.Current.CancellationToken);

        party!.Members.ShouldBe(["Frodo", "Sam", "Merry"]);
    }

    // ---- WriteToAggregate ----

    [Fact]
    public async Task write_to_aggregate_fetches_appends_and_saves()
    {
        await using (var session = _store.LightweightSession())
        {
            await session.Events.WriteToAggregate<QuestParty>(_streamId,
                stream => stream.AppendOne(new MonsterSlain("Balrog")),
                TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        var party = await query.Events.AggregateStreamAsync<QuestParty>(_streamId,
            token: TestContext.Current.CancellationToken);

        party!.MonstersSlain.ShouldBe(1);
    }

    [Fact]
    public async Task write_to_aggregate_passes_the_current_aggregate_to_the_caller()
    {
        await using var session = _store.LightweightSession();

        QuestParty? seen = null;
        await session.Events.WriteToAggregate<QuestParty>(_streamId, stream =>
        {
            seen = stream.Aggregate;
            stream.AppendOne(new MemberJoined("Sam"));
        }, TestContext.Current.CancellationToken);

        seen.ShouldNotBeNull();
        seen!.Members.ShouldBe(["Frodo"]);
    }

    // ---- AppendOptimistic ----

    [Fact]
    public async Task append_optimistic_stamps_the_current_version()
    {
        await using (var session = _store.LightweightSession())
        {
            await session.Events.AppendOptimistic(_streamId, TestContext.Current.CancellationToken,
                new MemberJoined("Sam"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        (await query.Events.FetchStreamStateAsync(_streamId, TestContext.Current.CancellationToken))!
            .Version.ShouldBe(3);
    }

    [Fact]
    public async Task append_optimistic_rejects_a_stream_that_does_not_exist()
    {
        await using var session = _store.LightweightSession();

        await Should.ThrowAsync<NonExistentStreamException>(
            () => session.Events.AppendOptimistic(Guid.NewGuid(), TestContext.Current.CancellationToken,
                new MemberJoined("Nobody")));
    }

    [Fact]
    public async Task append_optimistic_loses_to_a_concurrent_commit()
    {
        await using var first = _store.LightweightSession();
        await using var second = _store.LightweightSession();

        // Both read version 2 before either writes.
        await first.Events.AppendOptimistic(_streamId, TestContext.Current.CancellationToken,
            new MemberJoined("Sam"));
        await second.Events.AppendOptimistic(_streamId, TestContext.Current.CancellationToken,
            new MemberJoined("Merry"));

        await first.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<EventStreamUnexpectedMaxEventIdException>(
            () => second.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task append_exclusive_behaves_as_append_optimistic()
    {
        // SQLite has no row lock to take, so the exclusive variants are the optimistic ones. This
        // asserts the documented divergence rather than an aspiration — see CLAUDE.md.
        await using var first = _store.LightweightSession();
        await using var second = _store.LightweightSession();

        await first.Events.AppendExclusive(_streamId, TestContext.Current.CancellationToken,
            new MemberJoined("Sam"));
        await second.Events.AppendExclusive(_streamId, TestContext.Current.CancellationToken,
            new MemberJoined("Merry"));

        await first.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<EventStreamUnexpectedMaxEventIdException>(
            () => second.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    // ---- FetchLatest / ProjectLatest ----

    [Fact]
    public async Task fetch_latest_returns_the_committed_state()
    {
        await using var session = _store.LightweightSession();
        var party = await session.Events.FetchLatest<QuestParty>(_streamId,
            TestContext.Current.CancellationToken);

        party!.Members.ShouldBe(["Frodo"]);
    }

    [Fact]
    public async Task project_latest_includes_uncommitted_events()
    {
        await using var session = _store.LightweightSession();

        session.Events.Append(_streamId, new MemberJoined("Sam"));

        // Not saved — FetchLatest sees the database, ProjectLatest sees the session.
        (await session.Events.FetchLatest<QuestParty>(_streamId, TestContext.Current.CancellationToken))!
            .Members.ShouldBe(["Frodo"]);

        (await session.Events.ProjectLatest<QuestParty>(_streamId, TestContext.Current.CancellationToken))!
            .Members.ShouldBe(["Frodo", "Sam"]);
    }

    // ---- AggregateStreamToLastKnown ----

    [Fact]
    public async Task aggregates_to_the_last_known_state_before_a_delete()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream<QuestParty>(streamId,
                new QuestStarted("Doomed"), new MemberJoined("Boromir"), new QuestEnded());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();

        // The QuestEnded deletes the aggregate outright.
        (await query.Events.AggregateStreamAsync<QuestParty>(streamId,
            token: TestContext.Current.CancellationToken)).ShouldBeNull();

        var lastKnown = await query.Events.AggregateStreamToLastKnownAsync<QuestParty>(streamId,
            token: TestContext.Current.CancellationToken);

        lastKnown.ShouldNotBeNull();
        lastKnown!.Members.ShouldBe(["Boromir"]);
        lastKnown.Id.ShouldBe(streamId);
    }

    [Fact]
    public async Task last_known_of_an_unknown_stream_is_null()
    {
        await using var session = _store.LightweightSession();

        (await session.Events.AggregateStreamToLastKnownAsync<QuestParty>(Guid.NewGuid(),
            token: TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    // ---- the surface itself ----

    [Fact]
    public async Task the_session_event_surface_is_the_shared_jasperfx_contract()
    {
        // What the cross-store compliance fixture's EventsFor(session) has to return.
        await using var session = _store.LightweightSession();
        session.Events.ShouldBeAssignableTo<IEventStoreOperations>();
    }
}

public class writing_to_aggregates_with_string_identity : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("write-aggregate-string");
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
    public async Task round_trips_a_write_to_aggregate()
    {
        await using (var session = _store.LightweightSession())
        {
            await session.Events.WriteToAggregate<KeyedQuestParty>("quest/one",
                stream =>
                {
                    stream.Key.ShouldBe("quest/one");
                    stream.Aggregate!.Members.ShouldBe(["Merry"]);
                    stream.AppendOne(new MemberJoined("Pippin"));
                }, TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        var party = await query.Events.AggregateStreamAsync<KeyedQuestParty>("quest/one",
            token: TestContext.Current.CancellationToken);

        party!.Members.ShouldBe(["Merry", "Pippin"]);
    }

    [Fact]
    public async Task fetch_for_writing_by_generic_id_accepts_the_stream_identity_type()
    {
        await using var session = _store.LightweightSession();

        var stream = await session.Events.FetchForWriting<KeyedQuestParty, string>("quest/one",
            TestContext.Current.CancellationToken);

        stream.Aggregate!.Members.ShouldBe(["Merry"]);
    }

    [Fact]
    public async Task fetch_for_writing_by_generic_id_rejects_anything_else()
    {
        await using var session = _store.LightweightSession();

        await Should.ThrowAsync<NotImplementedException>(
            () => session.Events.FetchForWriting<KeyedQuestParty, int>(42,
                TestContext.Current.CancellationToken));
    }
}
