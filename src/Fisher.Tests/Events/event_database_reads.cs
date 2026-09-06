using Fisher.Tests.Events;
using Fisher.Tests.Projections;
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

            // fisher#102. The waiting tests below are about *registered* shards, because that is what
            // the wait is defined against — a progression row is evidence about a shard rather than the
            // definition of one. Two of them, because the bug being pinned is only visible with two: one
            // shard at the head while the other has never run. The daemon is deliberately never started
            // here, so every row these tests read is one they seeded.
            options.Projections.Snapshot<AsyncQuestTally>(SnapshotLifecycle.Async);
            options.Projections.Snapshot<AsyncQuestRoster>(SnapshotLifecycle.Async);
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
        await WriteProgressAsync(TheRegisteredShard, 1);

        await Should.ThrowAsync<TimeoutException>(async () =>
            await TheDatabase.WaitForNonStaleProjectionDataAsync(TimeSpan.FromMilliseconds(300)));
    }

    /// <summary>
    ///     <b>fisher#102, the fact the bug actually lived in: one shard at the head does not make the
    ///     store non-stale while another has never run.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the discriminating shape, and the one below is not. With <em>no</em> rows at all
    ///         the old rule also waited — <c>shards.Length > 0</c> was false — so a store where nothing
    ///         has run cannot tell the two rules apart. It takes a shard that <em>has</em> reported,
    ///         standing in for the whole set, which is exactly what happened on CI: the turbine shard
    ///         reached the head and the audit shard had not started, and the wait believed the first
    ///         one.
    ///     </para>
    ///     <para>
    ///         Seeded rather than raced. The window is the gap between one shard's first commit and
    ///         another's, so a daemon-driven test would be trying to land inside the bug rather than
    ///         pinning the rule.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task one_shard_at_the_head_does_not_speak_for_a_shard_that_never_ran()
    {
        await AppendAsync(3);
        await WriteProgressAsync(TheRegisteredShard, 3);

        await Should.ThrowAsync<TimeoutException>(async () =>
            await TheDatabase.WaitForNonStaleProjectionDataAsync(TimeSpan.FromMilliseconds(300)));
    }

    /// <summary>
    ///     <b>fisher#102 — a registered shard that has never run is stale, and used not to be.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The wait used to read its answer off the rows in <c>fi_event_progression</c>, which makes
    ///         a shard that has not started <em>invisible</em>: with no row it has no sequence to be
    ///         behind, so a store with two async projections was declared non-stale the moment the first
    ///         one reached the head. Behind <c>QueryForNonStaleData</c> that tells an application its
    ///         data is current while a projection has never run.
    ///     </para>
    ///     <para>
    ///         Seeded rather than raced, because the window is exactly the gap between one shard's first
    ///         commit and another's — a daemon-driven test would be trying to land inside the bug rather
    ///         than pinning the rule. Here nothing has run at all, which is the same state at its
    ///         simplest.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_registered_shard_that_has_never_run_is_stale()
    {
        await AppendAsync(3);

        await Should.ThrowAsync<TimeoutException>(async () =>
            await TheDatabase.WaitForNonStaleProjectionDataAsync(TimeSpan.FromMilliseconds(300)));
    }

    /// <summary>
    ///     The timeout says which shards have recorded nothing at all, because "never started" and
    ///     "still catching up" are different operational situations and a caller should not have to
    ///     guess which it got.
    /// </summary>
    [Fact]
    public async Task the_timeout_names_a_shard_that_recorded_nothing()
    {
        await AppendAsync(3);

        var timeout = await Should.ThrowAsync<TimeoutException>(async () =>
            await TheDatabase.WaitForNonStaleProjectionDataAsync(TimeSpan.FromMilliseconds(300)));

        timeout.Message.ShouldContain(TheRegisteredShard);
        timeout.Message.ShouldContain("the daemon may not be running");
    }

    /// <summary>
    ///     A store with no async projections has nothing to wait for, so the wait returns rather than
    ///     timing out (fisher#102, second half).
    /// </summary>
    /// <remarks>
    ///     This was broken the other way round by the same rule: with events present and no rows,
    ///     <c>shards.Length > 0</c> was false forever, so <c>QueryForNonStaleData</c> against a store
    ///     with no async projections <em>always</em> threw <see cref="TimeoutException" /> — a wait for
    ///     something that could never happen.
    /// </remarks>
    [Fact]
    public async Task a_store_with_no_async_projections_never_waits()
    {
        using var database = TemporaryDatabase.Create("eventdb-no-projections");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(Guid.NewGuid(), new QuestStarted("Quest"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await Should.NotThrowAsync(async () =>
            await store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    ///     A progression row for a shard nothing registers does not make the store stale.
    /// </summary>
    /// <remarks>
    ///     A projection removed from configuration leaves its row behind — that is what
    ///     <c>DeleteProjectionProgressByShardNameAsync</c> exists to clean up, and its own remarks say
    ///     the abstraction targets orphans that may never have been registered. Blocking on one would
    ///     make every subsequent wait hang on a shard that will never advance again, so the registered
    ///     set is the authority in both directions.
    /// </remarks>
    [Fact]
    public async Task a_row_for_a_shard_nothing_registers_is_ignored()
    {
        await AppendAsync(3);
        await WriteProgressAsync(TheRegisteredShard, 3);
        await WriteProgressAsync(TheOtherRegisteredShard, 3);
        await WriteProgressAsync(new ShardName("removed_projection").Identity, 1);

        await Should.NotThrowAsync(async () =>
            await TheDatabase.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(5)));
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
        await WriteProgressAsync(TheRegisteredShard, 1);

        await Should.ThrowAsync<TimeoutException>(async () =>
            await TheDatabase.WaitForNonStaleProjectionDataAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task waiting_returns_once_every_shard_has_reached_the_head()
    {
        await AppendAsync(3);
        await WriteProgressAsync(TheRegisteredShard, 3);
        await WriteProgressAsync(TheOtherRegisteredShard, 3);

        await Should.NotThrowAsync(async () =>
            await TheDatabase.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    ///     jasperfx#619 — the correlation behind the wait is now
    ///     <see cref="ProjectionLagCalculator" />'s, and a registered shard with no progression row is
    ///     reported as <em>fully behind</em> rather than as caught up.
    /// </summary>
    /// <remarks>
    ///     This is the shared spelling of fisher#102's rule, reachable through
    ///     <see cref="IEventDatabase.FetchProjectionLagAsync(IReadOnlyList{ShardName},CancellationToken)" />
    ///     — a default interface method Fisher inherits, so the read exists for a caller wanting the
    ///     numbers rather than the wait. <see cref="ProjectionLag.HasProgressionRow" /> is a real field
    ///     rather than a <c>Sequence == 0</c> sentinel precisely so "never started" and "at zero" stay
    ///     distinguishable.
    /// </remarks>
    [Fact]
    public async Task a_registered_shard_with_no_row_reports_as_having_none()
    {
        await AppendAsync(3);
        await WriteProgressAsync(ShardState.HighWaterMark, 3);
        await WriteProgressAsync(TheRegisteredShard, 3);

        var lags = await TheDatabase.FetchProjectionLagAsync(RegisteredShards(),
            TestContext.Current.CancellationToken);

        var ran = lags.Single(x => x.Shard.Identity == TheRegisteredShard);
        ran.HasProgressionRow.ShouldBeTrue();
        ran.IsCaughtUp.ShouldBeTrue();
        ran.Lag.ShouldBe(0);

        var neverRan = lags.Single(x => x.Shard.Identity == TheOtherRegisteredShard);
        neverRan.HasProgressionRow.ShouldBeFalse();
        neverRan.IsCaughtUp.ShouldBeFalse();
        neverRan.Sequence.ShouldBe(0);
        neverRan.HighWaterMark.ShouldBe(3);
    }

    /// <summary>
    ///     marten#5161 — a bookkeeping row is not a projection that never advances, and the high-water
    ///     row is the bar rather than a cell of its own.
    /// </summary>
    [Fact]
    public async Task bookkeeping_rows_are_not_reported_as_projections()
    {
        await AppendAsync(3);
        await WriteProgressAsync(ShardState.HighWaterMark, 3);
        await WriteProgressAsync("some bookkeeping row", 1);

        var lags = await TheDatabase.FetchProjectionLagAsync(RegisteredShards(),
            TestContext.Current.CancellationToken);

        lags.Count.ShouldBe(2);
        lags.Select(x => x.Shard.Identity)
            .ShouldBe([TheRegisteredShard, TheOtherRegisteredShard], ignoreOrder: true);
        lags.ShouldAllBe(x => x.HighWaterMark == 3);
        lags.ShouldAllBe(x => x.DatabaseIdentifier == _store.Database.Identifier);
    }

    /// <summary>
    ///     <b>The wait's bar is <c>max(seq_id)</c>, not the persisted high-water mark</b> — the one
    ///     place it deliberately does not read <see cref="ProjectionLag.IsCaughtUp" />.
    /// </summary>
    /// <remarks>
    ///     Every shard here is level with a high-water row that is itself behind the events, which is
    ///     what a store looks like between a commit and the agent's next poll. Measured against the
    ///     mark, every cell is caught up and the wait returns — telling a caller who just committed
    ///     that their own events are projected when nothing has read them. Measured against
    ///     <c>max(seq_id)</c>, which is the honest committed ceiling on SQLite because committed
    ///     sequences are contiguous, the store is stale and the wait times out. Swapping the check for
    ///     <c>IsCaughtUp</c> makes this test fail by returning.
    /// </remarks>
    [Fact]
    public async Task a_mark_that_trails_the_committed_events_does_not_make_the_store_current()
    {
        await AppendAsync(3);
        await WriteProgressAsync(ShardState.HighWaterMark, 1);
        await WriteProgressAsync(TheRegisteredShard, 1);
        await WriteProgressAsync(TheOtherRegisteredShard, 1);

        var lags = await TheDatabase.FetchProjectionLagAsync(RegisteredShards(),
            TestContext.Current.CancellationToken);

        // Against the mark alone, everything looks finished.
        lags.ShouldAllBe(x => x.IsCaughtUp);

        await Should.ThrowAsync<TimeoutException>(async () =>
            await TheDatabase.WaitForNonStaleProjectionDataAsync(TimeSpan.FromMilliseconds(250)));
    }

    /// <summary>
    ///     The shards the store registers, at their current versions — what the shared correlation
    ///     anchors on.
    /// </summary>
    private IReadOnlyList<ShardName> RegisteredShards()
        => _store.Options.Projections.AllShards().Select(x => x.Name).ToList();

    /// <summary>
    ///     The identity of a registered async shard — taken from the store rather than spelled out, so
    ///     a change to how a shard is named cannot leave these tests seeding a row that matches nothing
    ///     and passing for the wrong reason.
    /// </summary>
    private string ShardFor<T>()
        => _store.Options.Projections.AllShards()
            .Single(x => x.Name.Name == typeof(T).Name).Name.Identity;

    private string TheRegisteredShard => ShardFor<AsyncQuestTally>();

    private string TheOtherRegisteredShard => ShardFor<AsyncQuestRoster>();

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
