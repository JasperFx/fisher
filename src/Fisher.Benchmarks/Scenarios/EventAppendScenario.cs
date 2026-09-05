using Fisher.TestUtils;

namespace Fisher.Benchmarks.Scenarios;

/// <summary>
///     Scenario 2: event appends in the two shapes that stress different halves of the append path —
///     many streams with a few events each (per-stream version reads dominate), and one stream with
///     many events appended in batches (the trailing sequence read-back and version chain dominate).
/// </summary>
public static class EventAppendScenario
{
    public static async Task<ScenarioReport> RunManyStreamsAsync(int streams, int eventsPerStream)
    {
        await using var database = TemporaryDatabase.Create("bench-append-many");
        await using var store = Harness.BuildStore(database);
        await store.ApplyAllConfiguredChangesToDatabaseAsync();

        var elapsed = await Harness.TimeAsync(async () =>
        {
            for (var i = 0; i < streams; i++)
            {
                await using var session = store.LightweightSession();
                var events = new object[eventsPerStream];
                for (var j = 0; j < eventsPerStream; j++)
                {
                    events[j] = j % 2 == 0 ? new BenchCheckIn(j) : new BenchCheckOut(j);
                }

                session.Events.StartStream<BenchTally>(Guid.NewGuid(), events);
                await session.SaveChangesAsync();
            }
        });

        var totalEvents = streams * eventsPerStream;

        return new ScenarioReport($"event-append many-streams ({streams} streams x {eventsPerStream} events)",
        [
            new Metric("total", Harness.Ms(elapsed)),
            new Metric("commits/sec", Harness.PerSecond(streams, elapsed)),
            new Metric("events/sec", Harness.PerSecond(totalEvents, elapsed))
        ]);
    }

    public static async Task<ScenarioReport> RunSingleStreamAsync(int events, int eventsPerCommit)
    {
        await using var database = TemporaryDatabase.Create("bench-append-single");
        await using var store = Harness.BuildStore(database);
        await store.ApplyAllConfiguredChangesToDatabaseAsync();

        var streamId = Guid.NewGuid();

        // Start the stream outside the measurement so every measured commit is the same shape: an
        // append to an existing stream, which is the case that reads the current version each time.
        await using (var seed = store.LightweightSession())
        {
            seed.Events.StartStream<BenchTally>(streamId, new BenchCheckIn(0));
            await seed.SaveChangesAsync();
        }

        var commits = events / eventsPerCommit;

        var elapsed = await Harness.TimeAsync(async () =>
        {
            for (var i = 0; i < commits; i++)
            {
                await using var session = store.LightweightSession();
                var batch = new object[eventsPerCommit];
                for (var j = 0; j < eventsPerCommit; j++)
                {
                    batch[j] = j % 2 == 0 ? new BenchCheckIn(j) : new BenchCheckOut(j);
                }

                session.Events.Append(streamId, batch);
                await session.SaveChangesAsync();
            }
        });

        return new ScenarioReport(
            $"event-append single-stream ({events} events, {eventsPerCommit}/commit)",
        [
            new Metric("total", Harness.Ms(elapsed)),
            new Metric("commits/sec", Harness.PerSecond(commits, elapsed)),
            new Metric("events/sec", Harness.PerSecond(commits * eventsPerCommit, elapsed))
        ]);
    }
}
