using Fisher.Tests.Events;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Projections;

/// <summary>
///     marten#6004: read-your-writes. <c>session.LastAppendedPosition</c> says where a commit's events are, and
///     <c>store.WaitForProjectionsAsync</c> waits for the named projections to process that position, and nothing
///     past it, by polling <c>fi_event_progression</c>.
/// </summary>
public class read_your_writes : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("read-your-writes");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = buildStore();
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private DocumentStore buildStore()
        => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<QuestParty>(SnapshotLifecycle.Async);
        });

    private async Task<EventStorePosition> appendAsync(int count)
    {
        await using var session = _store.LightweightSession();
        for (var i = 0; i < count; i++)
        {
            session.Events.StartStream(Guid.NewGuid(), new QuestStarted($"quest-{i}"));
        }

        await session.SaveChangesAsync(Token);

        return session.LastAppendedPosition.ShouldNotBeNull();
    }

    private async Task<long> highestSequenceAsync()
    {
        await using var connection = await _store.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "select max(seq_id) from fi_events";
        return Convert.ToInt64(await command.ExecuteScalarAsync(Token));
    }

    private async Task writeProgressAsync(long sequence)
    {
        await using var connection = await _store.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "insert into fi_event_progression (name, last_seq_id) values (@name, @seq) " +
            "on conflict (name) do update set last_seq_id = excluded.last_seq_id";
        command.Parameters.AddWithValue("@name", shard);
        command.Parameters.AddWithValue("@seq", sequence);
        await command.ExecuteNonQueryAsync(Token);
    }

    private string shard => _store.Options.Projections.AllShards().Single().Name.Identity;

    [Fact]
    public async Task the_session_records_the_highest_sequence_a_commit_appended()
    {
        await using var session = _store.LightweightSession();
        session.LastAppendedPosition.ShouldBeNull();

        session.Events.StartStream(Guid.NewGuid(), new QuestStarted("one"), new MemberJoined("Frodo"));
        session.Events.StartStream(Guid.NewGuid(), new QuestStarted("two"));
        await session.SaveChangesAsync(Token);

        var first = session.LastAppendedPosition.ShouldNotBeNull();
        first.Sequence.ShouldBe(await highestSequenceAsync());
        first.DatabaseIdentifier.ShouldBe(_store.Database.Identifier);
        first.TenantId.ShouldBe(session.TenantId);

        session.Events.StartStream(Guid.NewGuid(), new QuestStarted("three"));
        await session.SaveChangesAsync(Token);

        session.LastAppendedPosition!.Sequence.ShouldBeGreaterThan(first.Sequence);
    }

    [Fact]
    public async Task reached_once_the_projection_passes_the_position_seen_from_another_store()
    {
        var position = await appendAsync(3);

        using var daemon = await _store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        // A second store over the same file runs no daemon
        await using var other = buildStore();
        var result = await other.WaitForProjectionsAsync(position, [typeof(QuestParty)], TimeSpan.FromSeconds(30), Token);

        await daemon.StopAllAsync();

        result.Reached.ShouldBeTrue(result.ToString());
    }

    [Fact]
    public async Task a_busy_store_does_not_hold_the_caller()
    {
        var position = await appendAsync(2);

        // Another writer commits after the caller, and the projection has only got as far as the caller
        await appendAsync(20);
        await writeProgressAsync(position.Sequence);

        var result = await _store.WaitForProjectionsAsync(position, [typeof(QuestParty)], TimeSpan.FromSeconds(5), Token);
        result.Reached.ShouldBeTrue(result.ToString());

        // The store-wide wait would still be waiting for the other writer's events
        await Should.ThrowAsync<TimeoutException>(() =>
            ((IEventDatabase)_store.Database).WaitForNonStaleProjectionDataAsync(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task a_timeout_is_a_result_with_how_far_each_shard_got()
    {
        var position = await appendAsync(4);

        var nothing = await _store.WaitForProjectionsAsync(position, [typeof(QuestParty)],
            TimeSpan.FromMilliseconds(300), Token);
        nothing.TimedOut.ShouldBeTrue();
        nothing.Shards.Single().Sequence.ShouldBeNull();

        await writeProgressAsync(position.Sequence - 1);

        var behind = await _store.WaitForProjectionsAsync(position, [shard.Split(':')[0]],
            TimeSpan.FromMilliseconds(300), Token);
        behind.TimedOut.ShouldBeTrue();
        behind.Shards.Single().Sequence.ShouldBe(position.Sequence - 1);
    }
}
