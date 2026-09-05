using Fisher.TestUtils;

namespace Fisher.Benchmarks.Scenarios;

/// <summary>
///     Scenario 1: N documents queued into one <c>SaveChangesAsync</c>. The harness for the future
///     write-batching fix — today every queued operation is compiled and executed as its own command
///     and round trip inside the one <c>BEGIN IMMEDIATE</c> transaction.
/// </summary>
public static class DocSaveScenario
{
    public static async Task<ScenarioReport> RunAsync(int documents, int rounds)
    {
        await using var database = TemporaryDatabase.Create("bench-doc-save");
        await using var store = Harness.BuildStore(database);
        await store.ApplyAllConfiguredChangesToDatabaseAsync();

        // One warm-up commit so the first measured round is not paying the first-use table ensure —
        // that cost is the cold-start scenario's subject, not this one's.
        await SaveBatchAsync(store, 1);

        var timings = new double[rounds];
        for (var round = 0; round < rounds; round++)
        {
            timings[round] = await Harness.TimeAsync(() => SaveBatchAsync(store, documents));
        }

        Array.Sort(timings);
        var median = timings[rounds / 2];

        return new ScenarioReport($"doc-save ({documents} docs/commit, {rounds} rounds)",
        [
            new Metric("median commit", Harness.Ms(median)),
            new Metric("fastest commit", Harness.Ms(timings[0])),
            new Metric("slowest commit", Harness.Ms(timings[^1])),
            new Metric("docs/sec at median", Harness.PerSecond(documents, median))
        ]);
    }

    private static async Task SaveBatchAsync(DocumentStore store, int documents)
    {
        await using var session = store.LightweightSession();

        for (var i = 0; i < documents; i++)
        {
            session.Store(new BenchDoc
            {
                Name = $"doc-{i}",
                Number = i,
                Timestamp = DateTimeOffset.UtcNow
            });
        }

        await session.SaveChangesAsync();
    }
}
