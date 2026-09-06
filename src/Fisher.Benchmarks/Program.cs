using System.Runtime.InteropServices;
using BenchmarkDotNet.Running;
using Fisher.Benchmarks;
using Fisher.Benchmarks.Scenarios;

// Fisher.Benchmarks — the perf harness described in the repo README under src/Fisher.Benchmarks.
//
// Timed scenarios (the EventAppenderPerfTester shape) for long-running/contended work:
//   doc-save | event-append | daemon-rebuild | concurrent-writers | cold-start | all
// BenchmarkDotNet (MemoryDiagnoser) for the allocation-shaped micro-benchmarks:
//   bdn [BenchmarkDotNet args...]
//
// Run `dotnet run -c Release --project src/Fisher.Benchmarks -- help` for the options.

var arguments = new Queue<string>(args);
var command = arguments.Count > 0 ? arguments.Dequeue() : "help";

var options = ParseOptions(arguments);

switch (command)
{
    case "doc-save":
    {
        PrintEnvironment();
        var report = await DocSaveScenario.RunAsync(
            options.GetValueOrDefault("docs", 1000),
            options.GetValueOrDefault("rounds", 5));
        report.Print();
        break;
    }

    case "event-append":
    {
        PrintEnvironment();
        var many = await EventAppendScenario.RunManyStreamsAsync(
            options.GetValueOrDefault("streams", 1000),
            options.GetValueOrDefault("events-per-stream", 3));
        many.Print();

        var single = await EventAppendScenario.RunSingleStreamAsync(
            options.GetValueOrDefault("events", 1000),
            options.GetValueOrDefault("events-per-commit", 10));
        single.Print();
        break;
    }

    case "daemon-rebuild":
    {
        PrintEnvironment();
        var report = await DaemonRebuildScenario.RunAsync(options.GetValueOrDefault("events", 10_000));
        report.Print();
        break;
    }

    case "concurrent-writers":
    {
        PrintEnvironment();
        var report = await ConcurrentWritersScenario.RunAsync(
            options.GetValueOrDefault("writers", 8),
            options.GetValueOrDefault("commits", 50),
            options.GetValueOrDefault("docs-per-commit", 5));
        report.Print();

        // The single-writer contrast: the same total work with no contention, so the delta is what
        // the file's one write lock costs.
        var solo = await ConcurrentWritersScenario.RunAsync(
            1,
            options.GetValueOrDefault("writers", 8) * options.GetValueOrDefault("commits", 50),
            options.GetValueOrDefault("docs-per-commit", 5));
        solo.Print();
        break;
    }

    case "cold-start":
    {
        PrintEnvironment();
        var report = await ColdStartScenario.RunAsync(
            options.GetValueOrDefault("types", 20),
            options.GetValueOrDefault("rounds", 5));
        report.Print();
        break;
    }

    case "all":
    {
        PrintEnvironment();
        var reports = new List<ScenarioReport>
        {
            await DocSaveScenario.RunAsync(100, options.GetValueOrDefault("rounds", 5)),
            await DocSaveScenario.RunAsync(1000, options.GetValueOrDefault("rounds", 5)),
            await EventAppendScenario.RunManyStreamsAsync(1000, 3),
            await EventAppendScenario.RunSingleStreamAsync(1000, 10),
            await DaemonRebuildScenario.RunAsync(10_000),
            await ConcurrentWritersScenario.RunAsync(8, 50, 5),
            await ConcurrentWritersScenario.RunAsync(1, 400, 5),
            await ColdStartScenario.RunAsync(20, options.GetValueOrDefault("rounds", 5))
        };

        foreach (var report in reports)
        {
            report.Print();
        }

        break;
    }

    case "bdn":
        // Everything after `bdn` is handed to BenchmarkDotNet unaltered, so its own filters and
        // job overrides work: `-- bdn --filter *DocSave*`, `-- bdn --job medium`.
        BenchmarkSwitcher
            .FromTypes([typeof(DocSaveBenchmarks), typeof(EventAppendBenchmarks)])
            .Run(args.Skip(1).ToArray());
        break;

    default:
        Console.WriteLine("""
            Fisher.Benchmarks — perf harness for the Fisher SQLite store.

            Usage: dotnet run -c Release --project src/Fisher.Benchmarks -- <command> [--option value ...]

            Timed scenarios:
              doc-save            [--docs 1000] [--rounds 5]
              event-append        [--streams 1000] [--events-per-stream 3]
                                  [--events 1000] [--events-per-commit 10]
              daemon-rebuild      [--events 10000]
              concurrent-writers  [--writers 8] [--commits 50] [--docs-per-commit 5]
              cold-start          [--types 20] [--rounds 5]
              all                 every scenario at its checked-in defaults

            Micro-benchmarks (BenchmarkDotNet + MemoryDiagnoser):
              bdn [BenchmarkDotNet args...]     e.g. bdn --filter *DocSave*

            See src/Fisher.Benchmarks/README.md and Results.md.
            """);
        break;
}

return;

static Dictionary<string, int> ParseOptions(Queue<string> arguments)
{
    var options = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    while (arguments.Count > 0)
    {
        var flag = arguments.Dequeue();
        if (!flag.StartsWith("--", StringComparison.Ordinal) || arguments.Count == 0)
        {
            continue;
        }

        if (int.TryParse(arguments.Dequeue(), out var value))
        {
            options[flag[2..]] = value;
        }
    }

    return options;
}

static void PrintEnvironment()
{
    Console.WriteLine($"Fisher.Benchmarks — {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}");
    Console.WriteLine($"  OS:        {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
    Console.WriteLine($"  .NET:      {RuntimeInformation.FrameworkDescription}");
    Console.WriteLine($"  CPUs:      {Environment.ProcessorCount}");
    Console.WriteLine($"  Config:    {(IsRelease() ? "Release" : "DEBUG — numbers are not comparable")}");
}

static bool IsRelease()
{
#if DEBUG
    return false;
#else
    return true;
#endif
}
