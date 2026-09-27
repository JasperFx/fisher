using JasperFx;
using Fisher.Tests.Daemon;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Events;

/// <summary>
///     A non-stale wait that times out says what the daemon last reported about each lagging shard
///     (fisher#329).
/// </summary>
/// <remarks>
///     fisher#329's only evidence was a timeout naming <c>AggregateMemoVectors:All</c> with no progress and
///     "the daemon may not be running" — beside a sibling shard sitting at the head, so the daemon was
///     running and the one thing that would have explained the stall, the agent's own state, was thrown
///     away. A stopped shard and a slow one are different investigations.
/// </remarks>
public class non_stale_timeout_diagnostics
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task a_shard_that_stopped_on_an_error_is_named_with_the_error()
    {
        await using var database = TemporaryDatabase.Create("stale-diagnostics");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<PoisonTally>(SnapshotLifecycle.Async);
            options.Projections.Errors.SkipApplyErrors = false;
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<PoisonTally>(Guid.NewGuid(), new Fisher.Tests.Daemon.Counted(PoisonTally.Poison));
            await session.SaveChangesAsync(Token);
        }

        var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        try
        {
            var ex = await Should.ThrowAsync<TimeoutException>(
                () => store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(3)));

            ex.Message.ShouldContain("PoisonTally:All");
            ex.Message.ShouldContain("This event cannot be applied.");
            ex.Message.ShouldNotContain("the daemon may not be running");
        }
        finally
        {
            await daemon.StopAllAsync();
            daemon.Dispose();
        }
    }

    /// <remarks>
    ///     With no daemon in this process there is nothing to report, and the old hint is the honest one.
    ///     The tracker is read rather than created, so asking cannot invent an observation.
    /// </remarks>
    [Fact]
    public async Task with_no_daemon_in_this_process_the_hint_is_kept()
    {
        await using var database = TemporaryDatabase.Create("stale-diagnostics-nodaemon");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<PoisonTally>(SnapshotLifecycle.Async);
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<PoisonTally>(Guid.NewGuid(), new Fisher.Tests.Daemon.Counted(1));
            await session.SaveChangesAsync(Token);
        }

        var ex = await Should.ThrowAsync<TimeoutException>(
            () => store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromMilliseconds(300)));

        ex.Message.ShouldContain("the daemon may not be running");
        ex.Message.ShouldNotContain("Agents:");
    }
}
