using Fisher.Internal;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;
using SQLitePCL;

namespace Fisher.Tests.Events;

/// <summary>
///     fisher#374 / jasperfx#930: <c>FetchManyForWriting</c>, natively.
/// </summary>
/// <remarks>
///     The shared <c>FetchForWritingCompliance</c> and <c>StringStreamIdentityCompliance</c> facts pin
///     the result, which the contract's one-stream-at-a-time default also gets right. So they pass
///     whether or not Fisher's implementation exists. The statement count is what tells the two apart.
/// </remarks>
public class fetching_many_for_writing : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("fetch-many-for-writing");
    private readonly CountingAggregateWriteCache _cache = new();
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            options.Projections.Snapshot<BarTab>(SnapshotLifecycle.Inline);
            options.Events.AggregateWriteCaching.Cache = _cache;
            options.Events.CacheAggregatesForWriting<BarTab>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<Guid> aPartyAsync(params string[] members)
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Events.StartStream<QuestParty>(streamId,
            [new QuestStarted("Find the ring"), .. members.Select(x => new MemberJoined(x))]);
        await session.SaveChangesAsync(Token);

        return streamId;
    }

    [Fact]
    public async Task each_handle_carries_its_own_aggregate_and_version_in_the_order_given()
    {
        var two = await aPartyAsync("Frodo");
        var four = await aPartyAsync("Sam", "Merry", "Pippin");
        var missing = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        var streams = await session.Events.FetchManyForWriting<QuestParty>([four, missing, two], Token);

        streams.Select(x => x.Id).ShouldBe([four, missing, two]);

        streams[0].Aggregate!.Members.ShouldBe(["Sam", "Merry", "Pippin"]);
        streams[0].StartingVersion.ShouldBe(4);

        streams[1].Aggregate.ShouldBeNull();
        streams[1].StartingVersion.ShouldBe(0);

        streams[2].Aggregate!.Members.ShouldBe(["Frodo"]);
        streams[2].Aggregate!.Id.ShouldBe(two);
        streams[2].StartingVersion.ShouldBe(2);
    }

    [Fact]
    public async Task it_is_two_statements_however_many_streams_are_fetched()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++) ids.Add(await aPartyAsync($"member {i}"));

        await using var session = _store.LightweightSession();
        var connection = await ((FisherSession)session).EventConnectionAsync(Token);

        var statements = 0;
        // SQLite's own per-statement hook, because event reads do not go through the session logger.
        // Filtered to the event tables so the connection's own housekeeping is not counted.
        raw.sqlite3_trace(connection.Handle, new strdelegate_trace((_, statement) =>
        {
            var sql = statement;
            if (sql.Contains(_store.Options.EventGraph.EventsTableName)
                || sql.Contains(_store.Options.EventGraph.StreamsTableName))
            {
                statements++;
            }
        }), null);

        try
        {
            // Through the contract, which is the route the default would otherwise take.
            IEventStoreOperations contract = session.Events;
            var streams = await contract.FetchManyForWriting<QuestParty>(ids, Token);

            streams.Count.ShouldBe(6);
            statements.ShouldBe(2);
        }
        finally
        {
            raw.sqlite3_trace(connection.Handle, (strdelegate_trace?)null, null);
        }
    }

    [Fact]
    public async Task a_repeated_id_is_refused_before_anything_is_read()
    {
        var id = await aPartyAsync("Frodo");

        await using var session = _store.LightweightSession();

        var ex = await Should.ThrowAsync<ArgumentException>(
            () => session.Events.FetchManyForWriting<QuestParty>([id, id], Token));
        ex.ParamName.ShouldBe("ids");
    }

    [Fact]
    public async Task an_empty_list_fetches_nothing()
    {
        await using var session = _store.LightweightSession();
        (await session.Events.FetchManyForWriting<QuestParty>(Array.Empty<Guid>(), Token)).ShouldBeEmpty();
    }

    [Fact]
    public async Task appends_through_every_handle_commit_together()
    {
        var first = await aPartyAsync("Frodo");
        var second = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            var streams = await session.Events.FetchManyForWriting<QuestParty>([first, second], Token);
            streams[0].AppendOne(new MemberJoined("Sam"));
            streams[1].AppendMany(new QuestStarted("Go home"), new MemberJoined("Rosie"));
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.Events.AggregateStreamAsync<QuestParty>(first, token: Token))!.Members
            .ShouldBe(["Frodo", "Sam"]);
        (await query.Events.AggregateStreamAsync<QuestParty>(second, token: Token))!.Members
            .ShouldBe(["Rosie"]);
    }

    [Fact]
    public async Task each_stream_is_guarded_on_its_own_version()
    {
        var first = await aPartyAsync("Frodo");
        var second = await aPartyAsync("Sam");

        await using var session = _store.LightweightSession();
        var streams = await session.Events.FetchManyForWriting<QuestParty>([first, second], Token);

        await using (var racer = _store.LightweightSession())
        {
            racer.Events.Append(second, new MemberJoined("Gollum"));
            await racer.SaveChangesAsync(Token);
        }

        streams[0].AppendOne(new MemberJoined("Merry"));
        streams[1].AppendOne(new MemberJoined("Pippin"));

        await Should.ThrowAsync<EventStreamUnexpectedMaxEventIdException>(
            () => session.SaveChangesAsync(Token));

        // Nothing from the losing unit of work landed, on either stream.
        await using var query = _store.LightweightSession();
        (await query.Events.FetchStreamStateAsync(first, Token))!.Version.ShouldBe(2);
        (await query.Events.FetchStreamStateAsync(second, Token))!.Version.ShouldBe(3);
    }

    [Fact]
    public async Task a_cached_baseline_is_folded_onto_and_written_back()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream<BarTab>(streamId, new TabOpened("Hilda"), new TabCharged(100));
            await session.SaveChangesAsync(Token);
        }

        // The first fetch misses and writes the baseline back at version 2 once the unit of work ends.
        await using (var session = _store.LightweightSession())
        {
            var streams = await session.Events.FetchManyForWriting<BarTab>([streamId], Token);
            streams[0].AppendOne(new TabCharged(10));
            await session.SaveChangesAsync(Token);
        }

        _cache.Stored.Values.Single().Version.ShouldBe(2);
        var hits = _cache.Hits;

        // The second claims it and folds only the charge after it.
        await using (var session = _store.LightweightSession())
        {
            var streams = await session.Events.FetchManyForWriting<BarTab>([streamId], Token);

            _cache.Hits.ShouldBe(hits + 1);
            streams[0].Aggregate!.Balance.ShouldBe(110);
            streams[0].StartingVersion.ShouldBe(3);
        }
    }

    [Fact]
    public async Task an_aggregate_type_not_enrolled_never_reaches_the_cache()
    {
        var id = await aPartyAsync("Frodo");
        var takes = _cache.Takes;

        await using var session = _store.LightweightSession();
        await session.Events.FetchManyForWriting<QuestParty>([id], Token);
        await session.SaveChangesAsync(Token);

        _cache.Takes.ShouldBe(takes);
        _cache.Stored.Keys.ShouldNotContain(x => x.DocumentType == typeof(QuestParty));
    }

    [Fact]
    public async Task a_stream_already_appended_to_in_the_session_keeps_its_pending_events()
    {
        var id = await aPartyAsync("Frodo");

        await using (var session = _store.LightweightSession())
        {
            session.Events.Append(id, new MemberJoined("Sam"));
            var streams = await session.Events.FetchManyForWriting<QuestParty>([id], Token);
            streams[0].AppendOne(new MemberJoined("Merry"));
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        (await query.Events.AggregateStreamAsync<QuestParty>(id, token: Token))!.Members
            .ShouldBe(["Frodo", "Sam", "Merry"]);
    }
}

public class fetching_many_for_writing_with_string_identity_and_conjoined_tenancy : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("fetch-many-string");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.StreamIdentity = StreamIdentity.AsString;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        // One key, two tenants, two different parties.
        foreach (var (tenant, member) in new[] { ("north", "Merry"), ("south", "Pippin") })
        {
            await using var session = _store.LightweightSession(tenant);
            session.Events.StartStream<KeyedQuestParty>("quest/one",
                new QuestStarted("Find the ring"), new MemberJoined(member));
            await session.SaveChangesAsync(Token);
        }

        await using var more = _store.LightweightSession("north");
        more.Events.StartStream<KeyedQuestParty>("quest/two", new QuestStarted("Go home"));
        await more.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task each_tenant_fetches_its_own_streams()
    {
        await using (var north = _store.LightweightSession("north"))
        {
            var streams = await north.Events.FetchManyForWriting<KeyedQuestParty>(["quest/two", "quest/one"], Token);

            streams.Select(x => x.Key).ShouldBe(["quest/two", "quest/one"]);
            streams[0].StartingVersion.ShouldBe(1);
            streams[1].Aggregate!.Members.ShouldBe(["Merry"]);
            streams[1].Aggregate!.Id.ShouldBe("quest/one");
        }

        await using var south = _store.LightweightSession("south");
        var southern = await south.Events.FetchManyForWriting<KeyedQuestParty>(["quest/one", "quest/two"], Token);

        southern[0].Aggregate!.Members.ShouldBe(["Pippin"]);
        southern[1].Aggregate.ShouldBeNull();
        southern[1].StartingVersion.ShouldBe(0);
    }

    [Fact]
    public async Task a_repeated_key_is_refused()
    {
        await using var session = _store.LightweightSession("north");

        var ex = await Should.ThrowAsync<ArgumentException>(
            () => session.Events.FetchManyForWriting<KeyedQuestParty>(["quest/one", "quest/one"], Token));
        ex.ParamName.ShouldBe("keys");
    }
}
