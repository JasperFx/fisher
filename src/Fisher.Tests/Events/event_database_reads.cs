using Fisher.Tests.Events;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Daemon;

/// <summary>
///     The <see cref="IEventDatabase" /> reads the async daemon runs on.
/// </summary>
/// <remarks>
///     Verified before anything is built on top of them: a high-water detector or event loader that
///     reads a wrong sequence does not fail loudly, it silently skips events.
/// </remarks>
public class event_database_reads : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("eventdb");
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

    private IEventDatabase TheDatabase => _store.Database;

    private async Task<int> AppendAsync(int count)
    {
        await using var session = _store.LightweightSession();
        for (var i = 0; i < count; i++)
        {
            session.Events.StartStream(Guid.NewGuid(), new QuestStarted($"Quest {i}"));
        }

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return count;
    }

    [Fact]
    public async Task the_highest_sequence_is_zero_on_an_empty_store()
    {
        (await TheDatabase.FetchHighestEventSequenceNumber(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task the_highest_sequence_tracks_appends()
    {
        await AppendAsync(3);
        (await TheDatabase.FetchHighestEventSequenceNumber(TestContext.Current.CancellationToken)).ShouldBe(3);

        await AppendAsync(2);
        (await TheDatabase.FetchHighestEventSequenceNumber(TestContext.Current.CancellationToken)).ShouldBe(5);
    }

    /// <summary>
    ///     A shard with no row and a shard at zero mean the same thing, and neither is an error — the
    ///     daemon reads both as "start from the beginning".
    /// </summary>
    [Fact]
    public async Task progress_for_an_unknown_shard_is_zero()
    {
        var name = new ShardName("never_registered");

        (await TheDatabase.ProjectionProgressFor(name, TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task all_progress_is_empty_before_anything_runs()
    {
        (await TheDatabase.AllProjectionProgress(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task progress_round_trips_through_the_progression_table()
    {
        // The row is keyed by ShardName.Identity, so it is derived rather than spelled out — the
        // grammar is JasperFx's and this test is not the place to pin it.
        var name = new ShardName("tally");
        await WriteProgressAsync(name.Identity, 7);

        (await TheDatabase.ProjectionProgressFor(name, TestContext.Current.CancellationToken)).ShouldBe(7);

        var all = await TheDatabase.AllProjectionProgress(TestContext.Current.CancellationToken);
        all.Count.ShouldBe(1);
        all[0].ShardName.ShouldBe(name.Identity);
        all[0].Sequence.ShouldBe(7);
    }

    /// <summary>
    ///     Exact-identity delete: ejecting one shard must not take a sibling whose name it prefixes.
    /// </summary>
    [Fact]
    public async Task deleting_progress_matches_the_identity_exactly()
    {
        var target = new ShardName("tally").Identity;
        var sibling = target + "Other";

        await WriteProgressAsync(target, 7);
        await WriteProgressAsync(sibling, 9);

        await TheDatabase.DeleteProjectionProgressByShardNameAsync(target, TestContext.Current.CancellationToken);

        var all = await TheDatabase.AllProjectionProgress(TestContext.Current.CancellationToken);
        all.Select(x => x.ShardName).ShouldBe([sibling]);
    }

    [Fact]
    public async Task deleting_a_shard_that_does_not_exist_is_a_no_op()
    {
        await Should.NotThrowAsync(async () =>
            await TheDatabase.DeleteProjectionProgressByShardNameAsync("nothing:All",
                TestContext.Current.CancellationToken));
    }

    /// <summary>
    ///     The floor is what a timestamp-bounded rebuild starts from. Text comparison over
    ///     <c>SqliteTimestamp</c>'s fixed-width UTC format is an instant comparison, which is the whole
    ///     point of that format.
    /// </summary>
    [Fact]
    public async Task the_event_store_floor_at_a_time_bounds_by_timestamp()
    {
        await AppendAsync(2);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        var cutoff = DateTimeOffset.UtcNow;
        await Task.Delay(20, TestContext.Current.CancellationToken);
        await AppendAsync(2);

        var floor = await TheDatabase.FindEventStoreFloorAtTimeAsync(cutoff, TestContext.Current.CancellationToken);

        floor.ShouldBe(2);
    }

    [Fact]
    public async Task the_floor_is_null_when_nothing_is_that_old()
    {
        await AppendAsync(2);

        var floor = await TheDatabase.FindEventStoreFloorAtTimeAsync(
            DateTimeOffset.UtcNow.AddDays(-1), TestContext.Current.CancellationToken);

        floor.ShouldBeNull();
    }

    /// <summary>
    ///     Nothing appended means nothing can be stale, so the wait returns rather than timing out.
    /// </summary>
    [Fact]
    public async Task waiting_for_non_stale_data_returns_immediately_on_an_empty_store()
    {
        await Should.NotThrowAsync(async () =>
            await TheDatabase.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    ///     Times out rather than returning quietly — a caller that asked to wait for non-stale data and
    ///     silently got stale data has no way to tell.
    /// </summary>
    [Fact]
    public async Task waiting_times_out_when_a_shard_never_catches_up()
    {
        await AppendAsync(3);
        await WriteProgressAsync(new ShardName("tally").Identity, 1);

        await Should.ThrowAsync<TimeoutException>(async () =>
            await TheDatabase.WaitForNonStaleProjectionDataAsync(TimeSpan.FromMilliseconds(300)));
    }

    /// <summary>
    ///     fisher#7 — the timeout is reported as a <see cref="TimeoutException" /> wherever in the poll
    ///     cycle the clock lands, not only when it lands in the delay.
    /// </summary>
    /// <remarks>
    ///     An already-elapsed timeout is what makes this deterministic: the token is cancelled before
    ///     the first query runs, so the cancellation necessarily comes out of a read rather than out of
    ///     <c>Task.Delay</c>. That is the path that used to escape as an
    ///     <see cref="OperationCanceledException" />, and it only showed up as a rare flake in
    ///     <see cref="waiting_times_out_when_a_shard_never_catches_up" /> under the full suite's load.
    /// </remarks>
    [Fact]
    public async Task waiting_reports_a_timeout_even_when_the_clock_elapses_inside_a_query()
    {
        await AppendAsync(3);
        await WriteProgressAsync(new ShardName("tally").Identity, 1);

        await Should.ThrowAsync<TimeoutException>(async () =>
            await TheDatabase.WaitForNonStaleProjectionDataAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task waiting_returns_once_every_shard_has_reached_the_head()
    {
        await AppendAsync(3);
        await WriteProgressAsync(new ShardName("tally").Identity, 3);

        await Should.NotThrowAsync(async () =>
            await TheDatabase.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(5)));
    }

    private async Task WriteProgressAsync(string name, long sequence)
    {
        await using var connection = await _store.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "insert into fi_event_progression (name, last_seq_id) values (@name, @seq) " +
            "on conflict (name) do update set last_seq_id = excluded.last_seq_id";
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@seq", sequence);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
