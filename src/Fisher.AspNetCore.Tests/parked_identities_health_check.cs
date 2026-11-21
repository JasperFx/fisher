using Fisher.AspNetCore.Daemon;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Fisher.AspNetCore.Tests;

public record Weighed(bool Poison);

public class WeighIn
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Apply(Weighed e)
    {
        if (e.Poison) throw new InvalidOperationException("Can't weigh this one");
        Count++;
    }
}

/// <summary>
///     fisher#652: the parked identities health check is Degraded while anything is parked, and Healthy again once the
///     identity is discarded. fisher#661: it is Unhealthy while a shard is paused at the parking cap, which it reads from
///     the extended progression row.
/// </summary>
public class parked_identities_health_check
{
    private const string TheShard = "WeighIn:All";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<DocumentStore> buildStoreAsync(TemporaryDatabase database, int maxParked)
    {
        var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.EnableExtendedProgressionTracking = true;
            options.Projections.Snapshot<WeighIn>(SnapshotLifecycle.Async);
            options.Projections.Errors.ApplyErrors = ProjectionErrorAction.Park;
            options.Projections.Errors.MaxParkedIdentitiesPerShard = maxParked;
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        return store;
    }

    private static async Task appendAsync(DocumentStore store, params bool[] poison)
    {
        await using var session = store.LightweightSession();
        foreach (var p in poison)
        {
            session.Events.StartStream<WeighIn>(Guid.NewGuid(), new Weighed(p));
        }

        await session.SaveChangesAsync(Token);
    }

    private static async Task<HealthCheckResult> waitForStatusAsync(IHealthCheck check, HealthStatus status)
    {
        using var timeout = new CancellationTokenSource(DaemonWait.Timeout);

        while (true)
        {
            var result = await check.CheckHealthAsync(new HealthCheckContext(), Token);
            if (result.Status == status)
            {
                return result;
            }

            if (timeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The check never reported {status}; last saw {result.Status}: {result.Description}");
            }

            await Task.Delay(50, Token);
        }
    }

    [Fact]
    public async Task healthy_with_nothing_parked()
    {
        using var database = TemporaryDatabase.Create("parked-health-none");
        await using var store = await buildStoreAsync(database, maxParked: 1);
        await appendAsync(store, false, false);

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        try
        {
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

            var result = await new ParkedIdentitiesHealthCheckExtensions.ParkedIdentitiesHealthCheck(store)
                .CheckHealthAsync(new HealthCheckContext(), Token);

            result.Status.ShouldBe(HealthStatus.Healthy);
            result.Description.ShouldBe("No projection identities are parked.");
        }
        finally
        {
            await daemon.StopAllAsync();
        }
    }

    [Fact]
    public async Task degraded_while_parked_identities_are_below_the_cap()
    {
        using var database = TemporaryDatabase.Create("parked-health-below-cap");
        await using var store = await buildStoreAsync(database, maxParked: 5);
        await appendAsync(store, false, true, true);

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        try
        {
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

            var result = await new ParkedIdentitiesHealthCheckExtensions.ParkedIdentitiesHealthCheck(store)
                .CheckHealthAsync(new HealthCheckContext(), Token);

            result.Status.ShouldBe(HealthStatus.Degraded);
            result.Description.ShouldNotBeNull().ShouldContain("2 projection identities are parked");

            // The shard is running, not paused, so the row carries no failure
            (await store.Database.AllProjectionProgress(Token)).Single(x => x.ShardName == TheShard)
                .Failure.ShouldBeNull();
        }
        finally
        {
            await daemon.StopAllAsync();
        }
    }

    [Fact]
    public async Task unhealthy_while_a_shard_is_paused_at_the_parking_cap()
    {
        using var database = TemporaryDatabase.Create("parked-health-at-cap");
        await using var store = await buildStoreAsync(database, maxParked: 1);

        // A good batch commits first, so the shard has a row before it pauses
        await appendAsync(store, false);

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        try
        {
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

            // Two failing identities in one batch pass a cap of one
            await appendAsync(store, true, true);

            var check = new ParkedIdentitiesHealthCheckExtensions.ParkedIdentitiesHealthCheck(store);
            var result = await waitForStatusAsync(check, HealthStatus.Unhealthy);

            result.Description.ShouldBe(
                $"Paused at the parking cap: {TheShard}. Fix the projection, replay or discard what's parked, then restart the shard.");
            result.Exception.ShouldBeNull();
        }
        finally
        {
            await daemon.StopAllAsync();
        }
    }

    /// <summary>
    ///     fisher#660 gave a shard that pauses before its first commit a progression row, so the cap reaches the check
    ///     even when nothing has committed.
    /// </summary>
    [Fact]
    public async Task unhealthy_when_a_shard_hits_the_parking_cap_on_its_first_batch()
    {
        using var database = TemporaryDatabase.Create("parked-health-at-cap-first-batch");
        await using var store = await buildStoreAsync(database, maxParked: 1);
        await appendAsync(store, true, true);

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        try
        {
            var check = new ParkedIdentitiesHealthCheckExtensions.ParkedIdentitiesHealthCheck(store);
            var result = await waitForStatusAsync(check, HealthStatus.Unhealthy);

            result.Description.ShouldNotBeNull().ShouldStartWith($"Paused at the parking cap: {TheShard}.");
            result.Exception.ShouldBeNull();
        }
        finally
        {
            await daemon.StopAllAsync();
        }
    }

    [Fact]
    public async Task degraded_while_an_identity_is_parked()
    {
        using var database = TemporaryDatabase.Create("parked-health");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<WeighIn>(SnapshotLifecycle.Async);
        });
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var check = new ParkedIdentitiesHealthCheckExtensions.ParkedIdentitiesHealthCheck(store);
        (await check.CheckHealthAsync(new HealthCheckContext(), Token)).Status.ShouldBe(HealthStatus.Healthy);

        var stream = Guid.NewGuid();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<WeighIn>(stream, new Weighed(false), new Weighed(true));
            await session.SaveChangesAsync(Token);
        }

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        try
        {
            await store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));

            var degraded = await check.CheckHealthAsync(new HealthCheckContext(), Token);
            degraded.Status.ShouldBe(HealthStatus.Degraded);
            degraded.Description.ShouldNotBeNull().ShouldContain("1 projection identity is parked");

            var name = store.Advanced.AllAsyncProjectionShardNames().Single().Name;
            (await daemon.DiscardParkedIdentityAsync(name, null, stream, Token)).Outcome
                .ShouldBe(ParkedReplayOutcome.Discarded);

            (await check.CheckHealthAsync(new HealthCheckContext(), Token)).Status.ShouldBe(HealthStatus.Healthy);
        }
        finally
        {
            await daemon.StopAllAsync();
        }
    }
}
