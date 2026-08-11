using Fisher.AspNetCore.Daemon;
using Microsoft.Data.Sqlite;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Fisher.AspNetCore.Tests;

/// <summary>
///     fisher#60 — the high-water health check, and the signal it reads.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every test here drives a real daemon.</b> That is the whole lesson of the sibling finding
///         (marten#5181): the check used to prefer <c>ShardState.LastHeartbeat</c>, which nothing in a
///         real deployment ever writes for a high-water row — JasperFx's
///         <c>ExtendedProgressionWriter.OnNext</c> returns early for <c>ShardState.HighWaterMark</c>,
///         and Fisher's own <c>AllProjectionProgress</c> never populated the field at all. The branch
///         was unreachable and the check silently degraded to the gap heuristic. Marten's tests passed
///         anyway, because they seeded the heartbeat column with raw SQL. So no test here writes a
///         progression row itself; what the daemon actually persists is the entire question.
///     </para>
///     <para>
///         The clock is the one thing faked, and only on the check's side. The daemon's writes carry
///         SQLite's own <c>strftime('now')</c>, so a test cannot make them old — it can only ask the
///         check what it makes of them from further along.
///     </para>
/// </remarks>
public class high_water_health_check : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("high-water-health");
    private DocumentStore _store = null!;
    private IProjectionDaemon? _daemon;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_daemon is not null)
        {
            await _daemon.StopAllAsync();
            _daemon.Dispose();
        }

        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task StartStoreAsync(TimeSpan livenessInterval)
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            // The check reports Healthy outright for a store with no async projections, and for any
            // daemon mode other than Solo.
            options.Projections.Snapshot<Catch>(SnapshotLifecycle.Async);
            options.DaemonSettings.AsyncMode = DaemonMode.Solo;

            options.Events.HighWaterLivenessInterval = livenessInterval;

            // Deliberately on: the point of a_running_daemon_never_writes_the_heartbeat_column is that
            // even with extended progression enabled, nothing writes it for the high-water row.
            options.Events.EnableExtendedProgressionTracking = true;

            // Well under the staleness thresholds below, so an idle loop cycles several times inside
            // one.
            options.DaemonSettings.SlowPollingTime = TimeSpan.FromMilliseconds(100);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    private async Task AppendAsync()
    {
        await using var session = _store.LightweightSession();
        session.Events.StartStream<Catch>(Guid.NewGuid(), new Landed("Trout"));
        await session.SaveChangesAsync(Token);
    }

    private async Task RunDaemonAsync()
    {
        _daemon = await _store.BuildProjectionDaemonAsync();
        await _daemon.StartAllAsync();
        await _store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));
    }

    private async Task StopDaemonAsync()
    {
        if (_daemon is null)
        {
            return;
        }

        await _daemon.StopAllAsync();
        _daemon.Dispose();
        _daemon = null;
    }

    private async Task<HealthCheckResult> CheckAsync(TimeSpan staleThreshold, TimeProvider? clock = null,
        HighWaterHealthCheckExtensions.HighWaterStateTracker? tracker = null)
    {
        var check = new HighWaterHealthCheckExtensions.HighWaterHealthCheck(_store,
            new HighWaterHealthCheckExtensions.HighWaterHealthCheckSettings(staleThreshold, 1),
            clock ?? TimeProvider.System,
            tracker ?? new HighWaterHealthCheckExtensions.HighWaterStateTracker());

        return await check.CheckHealthAsync(new HealthCheckContext(), Token);
    }

    /// <summary>
    ///     The premise of the defect, checked directly: no heartbeat is ever persisted for the
    ///     high-water row, extended progression tracking or not.
    /// </summary>
    /// <remarks>
    ///     This is what makes reading <c>LastHeartbeat</c> as the primary signal a dead branch rather
    ///     than a rarely-taken one. Asserted against a daemon that has genuinely run and advanced the
    ///     mark, so "the column is null" cannot be explained by the daemon not having started.
    /// </remarks>
    [Fact]
    public async Task a_running_daemon_never_writes_the_heartbeat_column()
    {
        await StartStoreAsync(TimeSpan.FromMilliseconds(100));
        await AppendAsync();
        await RunDaemonAsync();

        // Read over a connection of the test's own, naming the physical table: the question is what is
        // on disk, and Fisher's own reader is the thing that would have to be trusted otherwise.
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select heartbeat from fi_event_progression where name = 'HighWaterMark'";

        var value = await command.ExecuteScalarAsync(Token);

        // The row exists — the mark advanced — and its heartbeat is null.
        value.ShouldNotBeNull();
        value.ShouldBe(DBNull.Value);
    }

    /// <summary>
    ///     A store nobody is writing to stays healthy, however long it stays quiet.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the test the whole feature exists for, and the one the old implementation could
    ///         not pass on its primary signal. The mark advances once and then never again, so anything
    ///         keyed to the mark <em>moving</em> ages without bound — which is why the agent re-stamps
    ///         <c>last_updated</c> on an idle cycle.
    ///     </para>
    ///     <para>
    ///         The idle wait is deliberately longer than the staleness threshold: without the liveness
    ///         touch, <c>last_updated</c> would still hold the time of that one advance and the check
    ///         would report a healthy daemon as stopped.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_quiet_store_with_a_running_daemon_stays_healthy()
    {
        await StartStoreAsync(TimeSpan.FromMilliseconds(200));
        await AppendAsync();
        await RunDaemonAsync();

        // Nothing is appended here: the mark cannot move, only the poll loop can prove itself.
        await Task.Delay(TimeSpan.FromSeconds(2), Token);

        var result = await CheckAsync(TimeSpan.FromSeconds(1));

        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    /// <summary>
    ///     A daemon that has stopped is reported unhealthy, naming the poll cycle.
    /// </summary>
    /// <remarks>
    ///     Time is moved on the check's side rather than waiting it out: the daemon really is stopped,
    ///     so no amount of real waiting changes what the row holds, and a test that slept for the
    ///     threshold would only be slower.
    /// </remarks>
    [Fact]
    public async Task a_stopped_daemon_is_unhealthy()
    {
        await StartStoreAsync(TimeSpan.FromMilliseconds(200));
        await AppendAsync();
        await RunDaemonAsync();
        await StopDaemonAsync();

        var result = await CheckAsync(TimeSpan.FromSeconds(30), new ShiftedClock(TimeSpan.FromMinutes(5)));

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description.ShouldContain("poll cycle");
    }

    /// <summary>
    ///     A database the daemon has never run against is healthy, not unhealthy.
    /// </summary>
    [Fact]
    public async Task a_database_with_no_high_water_row_is_healthy()
    {
        await StartStoreAsync(TimeSpan.FromMilliseconds(200));

        var result = await CheckAsync(TimeSpan.FromSeconds(1), new ShiftedClock(TimeSpan.FromMinutes(5)));

        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    /// <summary>
    ///     With the liveness touch turned off, the gap heuristic is what is left — and it still works.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two readings, because one reading of a mark that is behind says nothing: a daemon that
    ///         is merely busy is behind too. The tracker is shared across both calls, which is what the
    ///         registered singleton does.
    ///     </para>
    ///     <para>
    ///         Note what this path cannot tell you, and why it is the secondary signal: a store nobody
    ///         is writing to has no gap, so a daemon that died against a quiet store reports healthy
    ///         here forever.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task the_gap_heuristic_still_catches_a_stuck_mark_without_the_liveness_touch()
    {
        await StartStoreAsync(TimeSpan.Zero);
        await AppendAsync();
        await RunDaemonAsync();
        await StopDaemonAsync();

        // Events the stopped daemon will never mark.
        await AppendAsync();
        await AppendAsync();
        await AppendAsync();

        var tracker = new HighWaterHealthCheckExtensions.HighWaterStateTracker();

        // First reading: behind, but that alone is normal.
        (await CheckAsync(TimeSpan.FromSeconds(30), tracker: tracker)).Status.ShouldBe(HealthStatus.Healthy);

        // Second reading, past the threshold, with the mark unmoved.
        var result = await CheckAsync(TimeSpan.FromSeconds(30), new ShiftedClock(TimeSpan.FromMinutes(5)), tracker);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description.ShouldContain("stuck at");
    }

    /// <summary>
    ///     Reports a fixed offset from the real clock, so the check can be asked what it would say
    ///     later without the test waiting that long.
    /// </summary>
    private sealed class ShiftedClock : TimeProvider
    {
        private readonly TimeSpan _shift;

        public ShiftedClock(TimeSpan shift) => _shift = shift;

        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + _shift;
    }

    public record Landed(string Species);

    public class Catch
    {
        public Guid Id { get; set; }
        public int Count { get; set; }

        public void Apply(Landed _) => Count++;
    }
}
