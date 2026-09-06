using Fisher.TestUtils;
using JasperFx.Events.Projections;

namespace Fisher.Benchmarks.Scenarios;

/// <summary>
///     Scenario 3: the async daemon pushing M events through a single-stream projection — first the
///     initial catch-up from a standing start, then a full <c>RebuildProjectionAsync</c> over the
///     same data.
/// </summary>
public static class DaemonRebuildScenario
{
    private const int EventsPerStream = 10;
    private const int StreamsPerSeedCommit = 50;

    public static async Task<ScenarioReport> RunAsync(int events)
    {
        await using var database = TemporaryDatabase.Create("bench-rebuild");
        await using var store = Harness.BuildStore(database,
            options => options.Projections.Snapshot<BenchTally>(SnapshotLifecycle.Async));
        await store.ApplyAllConfiguredChangesToDatabaseAsync();

        var streams = Math.Max(1, events / EventsPerStream);
        var seedMs = await Harness.TimeAsync(() => SeedAsync(store, streams));

        var daemon = await store.BuildProjectionDaemonAsync();
        try
        {
            var catchUpMs = await Harness.TimeAsync(async () =>
            {
                await daemon.StartAllAsync();
                await store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromMinutes(10));
            });

            var rebuildMs = await Harness.TimeAsync(
                () => daemon.RebuildProjectionAsync<BenchTally>(CancellationToken.None));

            var totalEvents = streams * EventsPerStream;

            return new ScenarioReport(
                $"daemon-rebuild ({totalEvents} events, {streams} streams, async Snapshot<BenchTally>)",
            [
                new Metric("seed (not daemon time)", Harness.Ms(seedMs)),
                new Metric("initial catch-up", Harness.Ms(catchUpMs)),
                new Metric("catch-up events/sec", Harness.PerSecond(totalEvents, catchUpMs)),
                new Metric("rebuild", Harness.Ms(rebuildMs)),
                new Metric("rebuild events/sec", Harness.PerSecond(totalEvents, rebuildMs))
            ]);
        }
        finally
        {
            await daemon.StopAllAsync();
            daemon.Dispose();
        }
    }

    private static async Task SeedAsync(DocumentStore store, int streams)
    {
        var seeded = 0;
        while (seeded < streams)
        {
            await using var session = store.LightweightSession();

            var batch = Math.Min(StreamsPerSeedCommit, streams - seeded);
            for (var i = 0; i < batch; i++)
            {
                var events = new object[EventsPerStream];
                for (var j = 0; j < EventsPerStream; j++)
                {
                    events[j] = j % 2 == 0 ? new BenchCheckIn(j) : new BenchCheckOut(j);
                }

                session.Events.StartStream<BenchTally>(Guid.NewGuid(), events);
            }

            await session.SaveChangesAsync();
            seeded += batch;
        }
    }
}
