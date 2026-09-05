using Fisher.TestUtils;

namespace Fisher.Benchmarks.Scenarios;

/// <summary>
///     Scenario 5: cold-start warm-up — the first unit of work that touches T document types on a
///     fresh database file, which is what pays the first-use table-ensure migration once per type
///     (the O(types × objects) migration finding). The second, warm commit of the identical shape is
///     the contrast that isolates the migration cost from the write cost.
/// </summary>
public static class ColdStartScenario
{
    public static async Task<ScenarioReport> RunAsync(int types, int rounds)
    {
        if (types < 1 || types > ColdDocs.Writers.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(types),
                $"types must be between 1 and {ColdDocs.Writers.Length}");
        }

        var cold = new double[rounds];
        var warm = new double[rounds];

        for (var round = 0; round < rounds; round++)
        {
            // A fresh file and a fresh store per round: cold means cold.
            await using var database = TemporaryDatabase.Create("bench-cold-start");
            await using var store = Harness.BuildStore(database);

            // The base (event) schema is applied up front, so the measurement below is the
            // per-document-type on-demand ensure and nothing else.
            await store.ApplyAllConfiguredChangesToDatabaseAsync();

            cold[round] = await Harness.TimeAsync(() => SaveOneOfEachAsync(store, types));
            warm[round] = await Harness.TimeAsync(() => SaveOneOfEachAsync(store, types));
        }

        Array.Sort(cold);
        Array.Sort(warm);
        var medianCold = cold[rounds / 2];
        var medianWarm = warm[rounds / 2];

        return new ScenarioReport($"cold-start ({types} document types, {rounds} rounds)",
        [
            new Metric("median first commit (table ensure)", Harness.Ms(medianCold)),
            new Metric("median second commit (warm)", Harness.Ms(medianWarm)),
            new Metric("table-ensure overhead", Harness.Ms(medianCold - medianWarm)),
            new Metric("overhead per type", Harness.Ms((medianCold - medianWarm) / types))
        ]);
    }

    private static async Task SaveOneOfEachAsync(DocumentStore store, int types)
    {
        await using var session = store.LightweightSession();

        for (var i = 0; i < types; i++)
        {
            ColdDocs.Writers[i](session);
        }

        await session.SaveChangesAsync();
    }
}
