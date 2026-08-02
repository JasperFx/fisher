using Fisher.Exceptions;
using JasperFx;
using JasperFx.Events;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Events;

public record QuestStarted(string Name);

public record MemberJoined(string Member);

public record MonsterSlain(string Monster);

public class appending_events : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("events");
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

    private async Task<List<(long SeqId, string Type, long Version, string Data, string StreamId)>> ReadEventsAsync()
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = conn.CreateCommand();
        command.CommandText = "select seq_id, type, version, data, stream_id from fi_events order by seq_id";

        var rows = new List<(long, string, long, string, string)>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3),
                reader.GetString(4)));
        }

        return rows;
    }

    private async Task<(long Version, string? Type, string TenantId)?> ReadStreamAsync(Guid id)
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = conn.CreateCommand();
        command.CommandText = "select version, type, tenant_id from fi_streams where id = @id";
        command.Parameters.AddWithValue("@id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            return null;
        }

        return (reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2));
    }

    [Fact]
    public async Task start_stream_writes_the_stream_row_and_its_events()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(streamId, new QuestStarted("Find the ring"), new MemberJoined("Frodo"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var stream = await ReadStreamAsync(streamId);
        stream.ShouldNotBeNull();
        stream!.Value.Version.ShouldBe(2);
        stream.Value.TenantId.ShouldBe(StorageConstants.DefaultTenantId);

        var events = await ReadEventsAsync();
        events.Count.ShouldBe(2);
        events[0].Type.ShouldBe("quest_started");
        events[0].Version.ShouldBe(1);
        events[0].StreamId.ShouldBe(streamId.ToString());
        events[1].Type.ShouldBe("member_joined");
        events[1].Version.ShouldBe(2);
    }

    [Fact]
    public async Task event_data_round_trips_as_json_text()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(streamId, new QuestStarted("Find the ring"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var events = await ReadEventsAsync();

        // Stored as TEXT that SQLite's json1 functions can read, not as an opaque blob.
        events[0].Data.ShouldContain("Find the ring");

        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = conn.CreateCommand();
        command.CommandText = "select json_extract(data, '$.name') from fi_events limit 1";
        (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe("Find the ring");
    }

    [Fact]
    public async Task sequences_are_assigned_back_onto_the_events()
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        var action = session.Events.StartStream(streamId, new QuestStarted("A"), new MemberJoined("B"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        // This is what the trailing SELECT read-back exists for: the daemon and projections key off
        // IEvent.Sequence, so an append that did not populate it would look like it wrote nothing.
        action.Events[0].Sequence.ShouldBeGreaterThan(0);
        action.Events[1].Sequence.ShouldBeGreaterThan(action.Events[0].Sequence);
    }

    [Fact]
    public async Task appending_to_an_existing_stream_continues_the_version_sequence()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(streamId, new QuestStarted("Find the ring"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Events.Append(streamId, new MemberJoined("Sam"), new MonsterSlain("Troll"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await ReadStreamAsync(streamId))!.Value.Version.ShouldBe(3);

        var events = await ReadEventsAsync();
        events.Select(x => x.Version).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task two_appends_to_one_stream_in_one_session_are_merged()
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Events.StartStream(streamId, new QuestStarted("Find the ring"));
        session.Events.Append(streamId, new MemberJoined("Sam"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Left unmerged these would be two stream-row writes in one transaction, the second of which
        // would fail its expected-version guard.
        (await ReadStreamAsync(streamId))!.Value.Version.ShouldBe(2);
        (await ReadEventsAsync()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task starting_a_stream_that_already_exists_is_rejected()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(streamId, new QuestStarted("Find the ring"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var second = _store.LightweightSession();
        second.Events.StartStream(streamId, new QuestStarted("Again"));

        await Should.ThrowAsync<ExistingStreamIdCollisionException>(
            () => second.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task a_stale_expected_version_is_rejected()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(streamId, new QuestStarted("Find the ring"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var session2 = _store.LightweightSession();
        session2.Events.Append(streamId, 5, new MemberJoined("Sam"));

        await Should.ThrowAsync<EventStreamUnexpectedMaxEventIdException>(
            () => session2.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task the_aggregate_type_alias_is_stamped_on_the_stream()
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Events.StartStream<Quest>(streamId, new QuestStarted("Find the ring"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var stream = await ReadStreamAsync(streamId);
        stream!.Value.Type.ShouldBe("Quest");

        // AggregateAliasFor also registers the alias, so the writing process can resolve the type it
        // just stamped.
        _store.Options.EventGraph.AggregateTypeFor("Quest").ShouldBe(typeof(Quest));
    }

    [Fact]
    public async Task the_append_observer_sees_committed_events()
    {
        var observed = new List<IEvent>();
        _store.Options.Events.AppendObserver = events => observed.AddRange(events);

        await using var session = _store.LightweightSession();
        session.Events.StartStream(Guid.NewGuid(), new QuestStarted("A"), new MemberJoined("B"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        observed.Count.ShouldBe(2);
    }

    [Fact]
    public async Task saving_an_empty_session_is_a_no_op()
    {
        await using var session = _store.LightweightSession();
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await ReadEventsAsync()).ShouldBeEmpty();
    }
}

public class Quest
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
