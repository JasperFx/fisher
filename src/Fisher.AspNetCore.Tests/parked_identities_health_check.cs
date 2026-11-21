using Fisher.AspNetCore.Daemon;
using Fisher.Projections;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
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

public record BadgeIssued(string Badge);

public record BadgeWeighed(bool Poison);

public class BadgeWeighIn
{
    public Guid Id { get; set; }

    [NaturalKey] public string Badge { get; set; } = "";

    public int Count { get; set; }
}

/// <summary>fisher#667: an aggregate with a natural key, so a parked slice of it shows the key.</summary>
public partial class BadgeWeighInProjection : SingleStreamProjection<BadgeWeighIn, Guid>
{
    [NaturalKeySource]
    public static BadgeWeighIn Create(BadgeIssued issued) => new() { Badge = issued.Badge };

    public static void Apply(BadgeWeighed e, BadgeWeighIn weighIn)
    {
        if (e.Poison) throw new InvalidOperationException("Can't weigh this badge");
        weighIn.Count++;
    }
}

/// <summary>
///     fisher#652: the parked identities health check is Degraded while anything is parked, and Healthy again once the
///     identity is discarded. fisher#661: it is Unhealthy while a shard is paused at the parking cap, which it reads from
///     the extended progression row. fisher#667: while Degraded, the description lists the parked identities, ten per
///     projection at most, with the natural key beside the id when the aggregate has one.
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

    [Fact]
    public async Task degraded_lists_the_parked_identities_with_their_natural_keys()
    {
        using var database = TemporaryDatabase.Create("parked-health-natural-keys");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<WeighIn>(SnapshotLifecycle.Async);
            options.Projections.Add(new BadgeWeighInProjection(), ProjectionLifecycle.Async);
        });
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var keyless = Guid.NewGuid();
        var keyed = Guid.NewGuid();
        var keyedWithoutKey = Guid.NewGuid();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<WeighIn>(keyless, new Weighed(true));
            session.Events.StartStream<BadgeWeighIn>(keyed, new BadgeIssued("BADGE-7"), new BadgeWeighed(true));
            session.Events.StartStream<BadgeWeighIn>(keyedWithoutKey, new BadgeWeighed(true));
            await session.SaveChangesAsync(Token);
        }

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        try
        {
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

            var result = await new ParkedIdentitiesHealthCheckExtensions.ParkedIdentitiesHealthCheck(store)
                .CheckHealthAsync(new HealthCheckContext(), Token);

            result.Status.ShouldBe(HealthStatus.Degraded);
            var description = result.Description.ShouldNotBeNull();

            // The counts are unchanged
            description.ShouldStartWith("3 projection identities are parked: ");
            description.ShouldContain("WeighIn (tenant *DEFAULT*): 1");
            description.ShouldContain("BadgeWeighIn (tenant *DEFAULT*): 2");

            // Then the identities, keyed where the stream has a natural key
            description.ShouldContain($"WeighIn: {keyless:D}");
            description.ShouldNotContain($"{keyless:D} (");
            description.ShouldContain($"{keyed:D} (BADGE-7)");
            description.ShouldContain($"{keyedWithoutKey:D}");
            description.ShouldNotContain($"{keyedWithoutKey:D} (");
        }
        finally
        {
            await daemon.StopAllAsync();
        }
    }

    [Fact]
    public async Task degraded_lists_at_most_ten_identities_per_projection()
    {
        using var database = TemporaryDatabase.Create("parked-health-ten");
        await using var store = await buildStoreAsync(database, maxParked: 100);

        var streams = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToList();
        await using (var session = store.LightweightSession())
        {
            foreach (var stream in streams)
            {
                session.Events.StartStream<WeighIn>(stream, new Weighed(true));
            }

            await session.SaveChangesAsync(Token);
        }

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        try
        {
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

            var result = await new ParkedIdentitiesHealthCheckExtensions.ParkedIdentitiesHealthCheck(store)
                .CheckHealthAsync(new HealthCheckContext(), Token);

            result.Status.ShouldBe(HealthStatus.Degraded);
            var description = result.Description.ShouldNotBeNull();
            description.ShouldStartWith("12 projection identities are parked: WeighIn (tenant *DEFAULT*): 12. ");

            streams.Count(x => description.Contains(x.ToString("D")))
                .ShouldBe(ParkedIdentitiesHealthCheckExtensions.ParkedIdentitiesHealthCheck.MaxIdentitiesPerProjection);
            description.ShouldEndWith(", and 2 more");
        }
        finally
        {
            await daemon.StopAllAsync();
        }
    }
}
