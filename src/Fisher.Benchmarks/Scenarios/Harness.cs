using System.Diagnostics;
using Fisher.TestUtils;
using JasperFx;

namespace Fisher.Benchmarks.Scenarios;

/// <summary>One measured line of a scenario's output.</summary>
public sealed record Metric(string Name, string Value);

/// <summary>What a scenario hands back to the console for the summary table.</summary>
public sealed record ScenarioReport(string Scenario, IReadOnlyList<Metric> Metrics)
{
    public void Print()
    {
        Console.WriteLine();
        Console.WriteLine($"== {Scenario} ==");
        foreach (var metric in Metrics)
        {
            Console.WriteLine($"  {metric.Name,-46} {metric.Value,18}");
        }
    }
}

/// <summary>
///     Shared plumbing for the timed scenarios: a throwaway database file, a store built the way the
///     tests build one, and stopwatch helpers.
/// </summary>
public static class Harness
{
    public static DocumentStore BuildStore(TemporaryDatabase database, Action<StoreOptions>? configure = null)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            configure?.Invoke(options);
        });

    public static async Task<double> TimeAsync(Func<Task> work)
    {
        var stopwatch = Stopwatch.StartNew();
        await work().ConfigureAwait(false);
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    public static string Ms(double milliseconds) => $"{milliseconds:n1} ms";

    public static string PerSecond(double count, double milliseconds)
        => milliseconds <= 0 ? "n/a" : $"{count / (milliseconds / 1000d):n0}/s";
}
