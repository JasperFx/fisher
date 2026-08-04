using JasperFx;
using JasperFx.Events;

namespace Fisher.Tests.Events;

public class fetching_events : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("fetch");
    private DocumentStore _store = null!;
    private readonly Guid _streamId = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.EnableCorrelationId = true;
            options.Events.EnableCausationId = true;
            options.Events.EnableHeaders = true;
            options.Events.EnableUserName = true;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using var session = _store.LightweightSession();
        session.Events.StartStream<Quest>(_streamId,
            new QuestStarted("Find the ring"),
            new MemberJoined("Frodo"),
            new MonsterSlain("Troll"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task fetches_the_whole_stream_in_version_order()
    {
        await using var session = _store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(_streamId, token: TestContext.Current.CancellationToken);

        events.Count.ShouldBe(3);
        events.Select(x => x.Version).ShouldBe([1, 2, 3]);
        events[0].Data.ShouldBeOfType<QuestStarted>().Name.ShouldBe("Find the ring");
        events[1].Data.ShouldBeOfType<MemberJoined>().Member.ShouldBe("Frodo");
        events[2].Data.ShouldBeOfType<MonsterSlain>().Monster.ShouldBe("Troll");
    }

    [Fact]
    public async Task hydrates_the_event_envelope_metadata()
    {
        await using var session = _store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(_streamId, token: TestContext.Current.CancellationToken);
        var first = events[0];

        first.Id.ShouldNotBe(Guid.Empty);
        first.StreamId.ShouldBe(_streamId);
        first.Sequence.ShouldBeGreaterThan(0);
        first.EventTypeName.ShouldBe("quest_started");
        first.TenantId.ShouldBe(StorageConstants.DefaultTenantId);
        first.IsArchived.ShouldBeFalse();

        // The timestamp round-trips through ISO-8601 TEXT rather than a native date type, so this is
        // really asserting that SqliteTimestamp's read and write halves agree.
        first.Timestamp.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));
        first.Timestamp.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task fetching_an_unknown_stream_returns_empty()
    {
        await using var session = _store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(Guid.NewGuid(),
            token: TestContext.Current.CancellationToken);

        events.ShouldBeEmpty();
    }

    [Fact]
    public async Task fetches_up_to_a_version()
    {
        await using var session = _store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(_streamId, 2,
            token: TestContext.Current.CancellationToken);

        events.Select(x => x.Version).ShouldBe([1, 2]);
    }

    [Fact]
    public async Task fetches_from_a_version()
    {
        await using var session = _store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(_streamId, fromVersion: 2,
            token: TestContext.Current.CancellationToken);

        events.Select(x => x.Version).ShouldBe([2, 3]);
    }

    [Fact]
    public async Task fetches_up_to_a_timestamp()
    {
        await using var session = _store.LightweightSession();

        var all = await session.Events.FetchStreamAsync(_streamId, token: TestContext.Current.CancellationToken);
        var future = await session.Events.FetchStreamAsync(_streamId,
            timestamp: DateTimeOffset.UtcNow.AddMinutes(5), token: TestContext.Current.CancellationToken);
        var past = await session.Events.FetchStreamAsync(_streamId,
            timestamp: DateTimeOffset.UtcNow.AddMinutes(-5), token: TestContext.Current.CancellationToken);

        future.Count.ShouldBe(all.Count);
        past.ShouldBeEmpty();
    }

    [Fact]
    public async Task fetches_stream_state()
    {
        await using var session = _store.LightweightSession();
        var state = await session.Events.FetchStreamStateAsync(_streamId, TestContext.Current.CancellationToken);

        state.ShouldNotBeNull();
        state!.Id.ShouldBe(_streamId);
        state.Version.ShouldBe(3);
        state.AggregateType.ShouldBe(typeof(Quest));
        state.IsArchived.ShouldBeFalse();
        state.Created.ShouldBeLessThanOrEqualTo(state.LastTimestamp);
    }

    [Fact]
    public async Task stream_state_for_an_unknown_stream_is_null()
    {
        await using var session = _store.LightweightSession();
        var state = await session.Events.FetchStreamStateAsync(Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        state.ShouldBeNull();
    }

    [Fact]
    public async Task loads_a_single_event_by_id()
    {
        await using var session = _store.LightweightSession();
        var all = await session.Events.FetchStreamAsync(_streamId, token: TestContext.Current.CancellationToken);

        var loaded = await session.Events.LoadAsync(all[1].Id, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded!.Id.ShouldBe(all[1].Id);
        loaded.Version.ShouldBe(2);
        loaded.StreamId.ShouldBe(_streamId);
        loaded.Data.ShouldBeOfType<MemberJoined>().Member.ShouldBe("Frodo");
    }

    [Fact]
    public async Task loads_a_single_event_typed()
    {
        await using var session = _store.LightweightSession();
        var all = await session.Events.FetchStreamAsync(_streamId, token: TestContext.Current.CancellationToken);

        var typed = await session.Events.LoadAsync<MemberJoined>(all[1].Id, TestContext.Current.CancellationToken);
        typed.ShouldNotBeNull();
        typed!.Data.Member.ShouldBe("Frodo");

        // A mismatched payload type is a miss, not a cast failure.
        (await session.Events.LoadAsync<MonsterSlain>(all[1].Id, TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task loading_an_unknown_event_returns_null()
    {
        await using var session = _store.LightweightSession();
        (await session.Events.LoadAsync(Guid.NewGuid(), TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task optional_metadata_round_trips()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            var wrapped = session.Events.BuildEvent(new QuestStarted("Metadata"));
            wrapped.CorrelationId = "corr-1";
            wrapped.CausationId = "cause-1";
            wrapped.UserName = "frodo";
            wrapped.Headers = new Dictionary<string, object> { ["source"] = "test" };

            session.Events.StartStream(streamId, wrapped);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        var events = await query.Events.FetchStreamAsync(streamId, token: TestContext.Current.CancellationToken);

        events[0].CorrelationId.ShouldBe("corr-1");
        events[0].CausationId.ShouldBe("cause-1");
        events[0].UserName.ShouldBe("frodo");
        events[0].Headers!["source"].ToString().ShouldBe("test");
    }
}

public class fetching_events_with_string_identity : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("fetch-string");
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
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task round_trips_a_string_identified_stream()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream("quest/one", new QuestStarted("Find the ring"), new MemberJoined("Sam"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();

        var events = await query.Events.FetchStreamAsync("quest/one", token: TestContext.Current.CancellationToken);
        events.Count.ShouldBe(2);
        events[0].StreamKey.ShouldBe("quest/one");

        var state = await query.Events.FetchStreamStateAsync("quest/one", TestContext.Current.CancellationToken);
        state!.Key.ShouldBe("quest/one");
        state.Version.ShouldBe(2);
    }

    [Fact]
    public async Task the_guid_overloads_are_rejected_under_string_identity()
    {
        await using var session = _store.LightweightSession();

        await Should.ThrowAsync<InvalidOperationException>(
            () => session.Events.FetchStreamAsync(Guid.NewGuid(), token: TestContext.Current.CancellationToken));
    }
}

public class archiving_streams : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("archive");
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
        session.Events.StartStream(_streamId, new QuestStarted("Find the ring"), new MemberJoined("Sam"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task archives_the_stream_and_all_of_its_events()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Events.ArchiveStream(_streamId);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();

        (await query.Events.FetchStreamStateAsync(_streamId, TestContext.Current.CancellationToken))!
            .IsArchived.ShouldBeTrue();

        var events = await query.Events.FetchStreamAsync(_streamId, token: TestContext.Current.CancellationToken);
        events.Count.ShouldBe(2);
        events.ShouldAllBe(x => x.IsArchived);
    }

    [Fact]
    public async Task un_archives_the_stream_again()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Events.ArchiveStream(_streamId);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Events.UnArchiveStream(_streamId);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();

        (await query.Events.FetchStreamStateAsync(_streamId, TestContext.Current.CancellationToken))!
            .IsArchived.ShouldBeFalse();

        var events = await query.Events.FetchStreamAsync(_streamId, token: TestContext.Current.CancellationToken);
        events.ShouldAllBe(x => !x.IsArchived);
    }

    [Fact]
    public async Task tombstoning_removes_the_stream_and_its_events_outright()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Events.TombstoneStream(_streamId);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();

        // Unlike archiving, there is nothing left to read.
        (await query.Events.FetchStreamStateAsync(_streamId, TestContext.Current.CancellationToken))
            .ShouldBeNull();

        (await query.Events.FetchStreamAsync(_streamId, token: TestContext.Current.CancellationToken))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task a_tombstoned_stream_does_not_release_its_sequence_numbers()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Events.TombstoneStream(_streamId);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var replacement = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(replacement, new QuestStarted("Again"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = conn.CreateCommand();
        command.CommandText = "select min(seq_id) from fi_events";

        // AUTOINCREMENT, not a bare INTEGER PRIMARY KEY: a reused seq_id would sit below an async
        // projection's high-water mark and be skipped forever.
        Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))
            .ShouldBeGreaterThan(2);
    }

    [Fact]
    public async Task tombstoning_leaves_other_streams_alone()
    {
        var other = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(other, new QuestStarted("Survivor"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Events.TombstoneStream(_streamId);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();

        (await query.Events.FetchStreamAsync(other, token: TestContext.Current.CancellationToken))
            .Count.ShouldBe(1);
    }
}
