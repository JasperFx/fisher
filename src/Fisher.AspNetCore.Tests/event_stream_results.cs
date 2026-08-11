using System.Text;
using System.Text.Json;
using Fisher.AspNetCore.Daemon;
using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Fisher.AspNetCore.Tests;

/// <summary>
///     fisher#49 — the event-stream results and the high-water health check.
/// </summary>
/// <remarks>
///     <para>
///         <b>The ETag is read before the aggregate is folded, and that is the point of
///         <c>StreamAggregate</c>.</b> A stream's version moves if and only if an event was appended,
///         so a matching <c>If-None-Match</c> answers <c>304</c> having read one row of
///         <c>fi_streams</c> and folded nothing — which for a long stream is the difference between an
///         endpoint that is cheap when nothing changed and one that is not.
///     </para>
///     <para>
///         The health check has an argument of its own here: Fisher's daemon <em>warns</em> rather
///         than refuses when the journal mode is not WAL, because that misconfiguration presents as a
///         slow projection rather than an error. This is how an operator finds out the warning
///         mattered.
///     </para>
/// </remarks>
public class event_stream_results : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("aspnet-events");
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

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private static (DefaultHttpContext Context, MemoryStream Body) NewContext()
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;

        return (context, body);
    }

    private static string Read(MemoryStream body) => Encoding.UTF8.GetString(body.ToArray());

    private async Task<Guid> AppendAsync(params object[] events)
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Events.StartStream<Voyage>(streamId, events);
        await session.SaveChangesAsync(Token);

        return streamId;
    }

    // ---- stream metadata ----

    [Fact]
    public async Task stream_event_state_reports_the_stream()
    {
        var streamId = await AppendAsync(new LegSailed(12), new LegSailed(8));

        await using var query = _store.LightweightSession();
        var (context, body) = NewContext();

        await query.StreamEventState(streamId).ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);

        var state = JsonDocument.Parse(Read(body)).RootElement;
        state.GetProperty("version").GetInt64().ShouldBe(2);
        state.GetProperty("aggregateTypeName").GetString().ShouldBe(nameof(Voyage));
    }

    [Fact]
    public async Task an_unknown_stream_is_a_404()
    {
        await using var query = _store.LightweightSession();
        var (context, _) = NewContext();

        await query.StreamEventState(Guid.NewGuid()).ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    // ---- events ----

    [Fact]
    public async Task stream_events_writes_them_in_order()
    {
        var streamId = await AppendAsync(new LegSailed(12), new LegSailed(8));

        await using var query = _store.LightweightSession();
        var (context, body) = NewContext();

        await query.StreamEvents(streamId).ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);

        var events = JsonDocument.Parse(Read(body)).RootElement;
        events.GetArrayLength().ShouldBe(2);
        events[0].GetProperty("version").GetInt64().ShouldBe(1);
        events[0].GetProperty("data").GetProperty("miles").GetInt32().ShouldBe(12);
    }

    [Fact]
    public async Task stream_events_honours_the_version_bound()
    {
        var streamId = await AppendAsync(new LegSailed(12), new LegSailed(8), new LegSailed(3));

        await using var query = _store.LightweightSession();
        var (context, body) = NewContext();

        await query.StreamEvents(streamId, version: 2).ExecuteAsync(context);

        JsonDocument.Parse(Read(body)).RootElement.GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task a_stream_with_no_events_is_a_404()
    {
        await using var query = _store.LightweightSession();
        var (context, _) = NewContext();

        await query.StreamEvents(Guid.NewGuid()).ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    // ---- live aggregation ----

    [Fact]
    public async Task stream_aggregate_projects_the_stream_live()
    {
        var streamId = await AppendAsync(new LegSailed(12), new LegSailed(8));

        await using var query = _store.LightweightSession();
        var (context, body) = NewContext();

        await query.StreamAggregate<Voyage>(streamId).ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        JsonDocument.Parse(Read(body)).RootElement.GetProperty("miles").GetInt32().ShouldBe(20);
    }

    /// <remarks>
    ///     The claim worth pinning: a <c>304</c> costs one row of <c>fi_streams</c> and no fold. Checked
    ///     by asserting the body is empty — an aggregate that had been built would have been written.
    /// </remarks>
    [Fact]
    public async Task an_unchanged_aggregate_is_a_304_without_folding()
    {
        var streamId = await AppendAsync(new LegSailed(12));

        await using var query = _store.LightweightSession();

        var (first, _) = NewContext();
        await query.StreamAggregate<Voyage>(streamId).ExecuteAsync(first);

        var etag = first.Response.Headers.ETag.ToString();
        etag.ShouldBe("\"1\"");

        var (second, secondBody) = NewContext();
        second.Request.Headers["If-None-Match"] = etag;

        await query.StreamAggregate<Voyage>(streamId).ExecuteAsync(second);

        second.Response.StatusCode.ShouldBe(StatusCodes.Status304NotModified);
        Read(secondBody).ShouldBeEmpty();
    }

    [Fact]
    public async Task appending_moves_the_etag_on()
    {
        var streamId = await AppendAsync(new LegSailed(12));

        await using var query = _store.LightweightSession();

        var (first, _) = NewContext();
        await query.StreamAggregate<Voyage>(streamId).ExecuteAsync(first);
        var etag = first.Response.Headers.ETag.ToString();

        await using (var session = _store.LightweightSession())
        {
            session.Events.Append(streamId, new LegSailed(5));
            await session.SaveChangesAsync(Token);
        }

        await using var second = _store.LightweightSession();
        var (context, body) = NewContext();
        context.Request.Headers["If-None-Match"] = etag;

        await second.StreamAggregate<Voyage>(streamId).ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        JsonDocument.Parse(Read(body)).RootElement.GetProperty("miles").GetInt32().ShouldBe(17);
    }

    // ---- health check ----

    /// <remarks>
    ///     A store with no async projections has no high-water mark to be behind. Reporting it
    ///     unhealthy would make the check useless in exactly the applications that add it defensively.
    /// </remarks>
    [Fact]
    public async Task the_health_check_is_healthy_with_no_async_projections()
    {
        var check = CheckFor(_store, TimeProvider.System);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), Token);

        result.Status.ShouldBe(HealthStatus.Healthy);
        result.Description.ShouldContain("No async projections");
    }

    /// <remarks>
    ///     A daemon that has caught up is healthy, and one behind by a single event is too — the daemon
    ///     is always at least one event behind a writer that has just committed, which is what the
    ///     minimum gap exists for.
    /// </remarks>
    [Fact]
    public async Task the_health_check_is_healthy_when_the_daemon_keeps_up()
    {
        await using var database = TemporaryDatabase.Create("aspnet-health-ok");
        await using var store = StoreWithAsyncProjection(database);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Voyage>(Guid.NewGuid(), new LegSailed(12));
            await session.SaveChangesAsync(Token);
        }

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();
        await store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));
        await daemon.StopAllAsync();

        var result = await CheckFor(store, TimeProvider.System).CheckHealthAsync(new HealthCheckContext(), Token);

        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    /// <summary>
    ///     A daemon that never ran, with events piling up behind it, is unhealthy — after the
    ///     threshold, not before.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two readings, because one is not enough: a single reading says the daemon is behind,
    ///         which is normal, and only two identical readings separated by the threshold say it has
    ///         stopped. Driven with a fake clock rather than by waiting, so the test does not take
    ///         thirty seconds and does not go intermittent under load.
    ///     </para>
    ///     <para>
    ///         <b>The liveness touch is turned off here on purpose</b> (fisher#60). It is the primary
    ///         signal and would answer first, and this test is about the secondary one — the gap
    ///         heuristic, which is all that is left for a store that would rather its daemon never
    ///         wrote periodically. The primary signal has its own tests in
    ///         <c>high_water_health_check</c>.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task the_health_check_is_unhealthy_when_the_mark_is_stuck()
    {
        await using var database = TemporaryDatabase.Create("aspnet-health-stalled");
        await using var store = StoreWithAsyncProjection(database, TimeSpan.Zero);

        // The daemon runs once so a high-water row exists, then stops while events keep arriving.
        using (var daemon = await store.BuildProjectionDaemonAsync())
        {
            await daemon.StartAllAsync();

            await using (var session = store.LightweightSession())
            {
                session.Events.StartStream<Voyage>(Guid.NewGuid(), new LegSailed(1));
                await session.SaveChangesAsync(Token);
            }

            await store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));
            await daemon.StopAllAsync();
        }

        await using (var session = store.LightweightSession())
        {
            for (var i = 0; i < 5; i++)
            {
                session.Events.StartStream<Voyage>(Guid.NewGuid(), new LegSailed(i + 1));
            }

            await session.SaveChangesAsync(Token);
        }

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var check = CheckFor(store, clock);

        // First reading: behind, but only just observed — not yet a fault.
        (await check.CheckHealthAsync(new HealthCheckContext(), Token)).Status.ShouldBe(HealthStatus.Healthy);

        clock.Advance(TimeSpan.FromMinutes(1));

        var stalled = await check.CheckHealthAsync(new HealthCheckContext(), Token);

        stalled.Status.ShouldBe(HealthStatus.Unhealthy);
        stalled.Description.ShouldContain("stuck at");
        stalled.Description.ShouldContain("WAL");
    }

    private DocumentStore StoreWithAsyncProjection(TemporaryDatabase database, TimeSpan? livenessInterval = null)
    {
        var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.DaemonSettings.AsyncMode = DaemonMode.Solo;
            options.Projections.Snapshot<Voyage>(SnapshotLifecycle.Async);

            if (livenessInterval is { } interval)
            {
                options.Events.HighWaterLivenessInterval = interval;
            }
        });

        store.ApplyAllConfiguredChangesToDatabaseAsync(Token).GetAwaiter().GetResult();

        return store;
    }

    private static HighWaterHealthCheckExtensions.HighWaterHealthCheck CheckFor(
        IDocumentStore store, TimeProvider clock)
        => new(store,
            new HighWaterHealthCheckExtensions.HighWaterHealthCheckSettings(TimeSpan.FromSeconds(30), 1),
            clock, new HighWaterHealthCheckExtensions.HighWaterStateTracker());

    /// <summary>A clock the test moves, so a staleness threshold costs no wall time.</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        internal FakeTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}

public record LegSailed(int Miles);

public class Voyage
{
    public Guid Id { get; set; }
    public int Miles { get; set; }

    public void Apply(LegSailed leg) => Miles += leg.Miles;
}
