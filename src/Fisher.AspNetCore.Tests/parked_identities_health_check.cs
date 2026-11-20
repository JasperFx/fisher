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
///     identity is discarded.
/// </summary>
public class parked_identities_health_check
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

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
