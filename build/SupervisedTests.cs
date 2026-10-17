using System.Text;
using Bobcat.Ledger;
using Bobcat.Resilience;
using Bobcat.Supervisor;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

// The supervised test runner (JasperFx/bobcat#565). Each suite's executable — an xUnit v3 Microsoft
// Testing Platform host, see UseMicrosoftTestingPlatformRunner in Directory.Build.props — is driven
// by Bobcat's Supervisor over the platform's JSON-RPC server mode instead of being invoked directly.
// What that buys over `Fisher.Tests --report-trx`:
//
//   - Worker processes, partitioned by test CLASS (never by test: a class is the unit every xUnit
//     isolation contract is written against, and Wolverine measured per-test splitting as 1-4
//     non-deterministic failures where per-class was clean).
//   - A temp directory per lane. Every Fisher test database is a file under Path.GetTempPath()
//     (TemporaryDatabase, and the tenancy tests' directories), so pointing TMPDIR/TMP/TEMP at a lane's
//     own directory gives each worker its own SQLite files and its own place to leak them — a leak is
//     then attributable to the lane, and so to the classes that ran in it.
//   - Honest reporting: a crashed worker is Indeterminate with its exit code and stderr, never a
//     silent pass; a test in flight past the stall threshold is named while the run is still going.
//
// Retries are OPT-IN ONLY. The harness this replaces ran each executable once, so its real retry
// ceiling was one attempt per test; matching that is the Wolverine rollout's lesson 8. A failure is
// retried only when the test itself carries Bobcat's `Retry` / `Isolated` / `RecycleOnRetry` trait
// (DefaultFailurePolicy) — no Fisher test does today — so nothing here can launder red into green.
partial class Build
{
    /// <summary>
    /// Overrides every suite's worker count (<c>--test-workers 4</c>) and bypasses the
    /// <c>ProcessorCount / 2</c> clamp: measuring past the ceiling is a legitimate thing to ask for.
    /// </summary>
    [Parameter("Worker processes per suite - overrides the committed counts and the per-machine clamp")]
    readonly int? TestWorkers;

    /// <summary>
    /// Write this run into the committed ledger (build/test-ledger/). Always on under CI, where the
    /// workflow uploads the result as an artifact; off by default locally so an ordinary test run does
    /// not dirty a committed file. Committing a ledger is a deliberate act.
    /// </summary>
    [Parameter("Record this run's observations into build/test-ledger/ (always on under CI)")]
    readonly bool RecordLedger;

    /// <summary>
    /// The suites, in the order fisher.yml always ran them, with their committed worker counts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fisher.Tests' count is tuned on a developer machine and clamped on the machine that runs it
    /// (see <see cref="workersFor"/>). The two companion suites are a few seconds each: one worker,
    /// since a second process would cost more to launch than it could save.
    /// </para>
    /// <para>
    /// Two, not more, because xUnit already runs Fisher.Tests' collections in parallel inside one
    /// process: measured on an M-series laptop (3 interleaved rounds, net10.0), 1 worker 15.5-17.6s,
    /// 2 workers 13.3-16.0s, 4 workers 12.5-16.2s. The processes buy a little isolation and a little
    /// time; a third and fourth buy neither, and on a shared runner they only add SQLite writers.
    /// </para>
    /// </remarks>
    static readonly SupervisedSuite[] Suites =
    [
        new("Fisher.Tests", Workers: 2),
        new("Fisher.AspNetCore.Tests", Workers: 1),
        new("Fisher.EntityFrameworkCore.Tests", Workers: 1)
    ];

    record SupervisedSuite(string Project, int Workers);

    /// <summary>
    /// A test in flight this long is named while the run is still going. Above the longest wait any
    /// Fisher test is allowed — DaemonWait.Timeout is two minutes under CI — so a stall is a hang, not a
    /// slow daemon catch-up. Report only: nothing is killed or retried for it.
    /// </summary>
    static readonly TimeSpan StallThreshold = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Runs kept per test in the committed ledger. Bobcat's default is 20; here it is 3, because the
    /// ledger's JSON costs ~525 bytes per test per run (indented, the uid written twice) and Fisher.Tests
    /// has ~2,630 tests per target framework — 1.4 MB a run, so the default would commit ~28 MB per
    /// framework. Three runs are enough for what the ledger feeds today, lane balancing by known
    /// duration; flakiness evidence lives in the per-run TRX and job summaries.
    /// </summary>
    const int LedgerRunsPerTest = 3;

    static AbsolutePath LedgerDirectory => RootDirectory / "build" / "test-ledger";
    static AbsolutePath LanesRoot => RootDirectory / ".nuke" / "temp" / "lanes";

    void RunSuites(IEnumerable<string> frameworks)
    {
        var failed = new List<string>();

        foreach (var framework in frameworks)
        foreach (var suite in Suites)
        {
            if (!runSupervised(suite, framework)) failed.Add($"{suite.Project} ({framework})");
        }

        if (failed.Count > 0)
            throw new InvalidOperationException($"Tests failed: {string.Join(", ", failed)}");
    }

    int workersFor(SupervisedSuite suite)
    {
        if (TestWorkers is { } asked) return Math.Max(1, asked);

        // The committed count is tuned on a developer machine; the machine running decides what it can
        // carry (Wolverine rollout, lesson 3: four hosts on a 4-vCPU hosted runner killed the runner).
        var ceiling = Math.Max(1, Environment.ProcessorCount / 2);
        if (suite.Workers <= ceiling) return suite.Workers;

        Log.Information("Clamping {Asked} workers to {Ceiling} for this machine's {Cores} core(s)",
            suite.Workers, ceiling, Environment.ProcessorCount);
        return ceiling;
    }

    bool runSupervised(SupervisedSuite suite, string framework)
    {
        var workers = workersFor(suite);
        var executable = testHostFor(suite.Project, framework);
        var lanes = prepareLaneDirectories(suite.Project, framework, workers);
        var ledgerPath = LedgerDirectory / $"{suite.Project}.{framework}.json";
        var ledger = TestLedger.Load(ledgerPath, LedgerRunsPerTest);
        var known = ledger.KnownDurations();

        Log.Information("=== {Project} ({Framework}): supervised, {Workers} worker(s), {Known} known duration(s) ===",
            suite.Project, framework, workers, known.Count);

        var factory = new MtpWorkerFactory(executable)
        {
            // One temp directory per lane. Discovery, isolated and recycled workers all report lane 0,
            // so the directories created equal the workers asked for, not the processes launched.
            EnvironmentFor = context => tempDirectoryEnvironment(lanes[Math.Min(context.Lane, lanes.Count - 1)])
        };

        var supervisor = new Supervisor(factory)
        {
            MaxParallelWorkers = workers,

            // By class, from the discovery node's own location.type. The display-name heuristic is the
            // fallback only; xUnit v3 hands over the real type on every node.
            PartitionKey = test => test.TestClass ?? WorkPlan.ClassOf(test),

            // Opt-in only — see the header. The ceiling is generous because nothing reaches it unless a
            // test asks to be retried, and the run-wide cap stops a broken environment turning every
            // tagged test into three slow failures.
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 3, MaxRetriesPerRun = 10 },

            // A retry the policy asks for runs in a fresh process; idle lanes are released first so a
            // retry pass never has workers + 1 hosts resident (Wolverine rollout, lesson 4).
            ReleaseIdleLanes = true,

            StallThreshold = StallThreshold,
            StallAction = StallAction.Report,
            HeartbeatInterval = TimeSpan.FromSeconds(30),
            ResourceSampleInterval = TimeSpan.FromSeconds(15),

            // Longest-processing-time lane balancing from the committed ledger. Empty on a first run,
            // which degrades to balancing by test count.
            KnownTestDurations = known.Count > 0 ? known : null,

            Log = message => Log.Information("  {Message}", message)
        };

        var collapsed = new CollapsedResultsGuard();
        supervisor.AddObserver(collapsed);

        var results = supervisor.Run().GetAwaiter().GetResult();

        // The TRX the old harness wrote (--report-trx), written from the supervisor's results to the
        // same place: scripts/check_scoreboard.py and the dorny/test-reporter step read it unchanged.
        var trx = SupervisedTrx.Write(results, suite.Project, framework,
            RootDirectory / "src" / suite.Project / "bin" / Configuration / framework / "TestResults");
        Log.Information("  TRX: {Path}", trx);

        if (RecordLedger || IsServerBuild)
        {
            var runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") is { Length: > 0 } id
                ? $"gha-{id}-{framework}"
                : $"local-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

            ledger.Record(SupervisorLedger.From(results, runId, DateTimeOffset.UtcNow)).Save(ledgerPath);
            Log.Information("  ledger: {Path}", ledgerPath);
        }

        return report(suite, framework, workers, results, collapsed.Collapsed());
    }

    bool report(SupervisedSuite suite, string framework, int workers, SupervisorResults results,
        IReadOnlyList<CollapsedTest> collapsed)
    {
        var text = RunReport.ToText(results);
        if (collapsed.Count > 0) text += Environment.NewLine + Environment.NewLine + describe(collapsed);

        Log.Information("=== {Project} ({Framework}) ===\n{Report}", suite.Project, framework, text);
        appendStepSummary(suite, framework, workers, results, text, green: results.ExitCode == 0
            && results.Tests.Count > 0 && collapsed.Count == 0);

        foreach (var fault in results.WorkerFaults) Log.Error("  [WORKER FAULT] {Fault}", fault);
        foreach (var test in results.Indeterminate)
            Log.Error("  [INDETERMINATE] {Test} — {Error}", test.DisplayName, test.Final.Outcome.ErrorMessage);
        foreach (var test in results.Failed.Where(x => !x.IsIndeterminate))
            Log.Error("  [FAILED] {Test} — {Error}", test.DisplayName, test.Final.Outcome.ErrorMessage);
        foreach (var flaky in results.PassedOnRetry)
            Log.Warning("  [FLAKY] {Test} — passed on attempt {Attempts}", flaky.DisplayName, flaky.AttemptCount);

        // See CollapsedResultsGuard: the results hold one row of each such theory, so they are not a
        // verdict on the others. Refused whether or not a hidden row failed — a count the suite cannot
        // report honestly is the defect, and the next hidden row may be the red one.
        foreach (var test in collapsed)
            Log.Error("  [COLLAPSED] {Test} — {Rows} rows reported under one id{Hidden}",
                test.Rows[0].DisplayName, test.Rows.Count, test.HidesAFailure ? ", and a row FAILED" : "");

        if (collapsed.Count > 0)
        {
            Log.Error("=== {Project} ({Framework}): {Count} theory(ies) reported several rows under one test id — " +
                      "make their data serializable ===", suite.Project, framework, collapsed.Count);
            return false;
        }

        if (results.AbortReason is not null)
        {
            Log.Error("=== {Project}: run ABORTED — {Reason} ===", suite.Project, results.AbortReason);
            return false;
        }

        // An empty run is a failure, never a green job: a suite that discovers nothing has been broken
        // by a rename, a missing build or a host that never connected, and "0 failed" would hide it.
        if (results.Tests.Count == 0)
        {
            Log.Error("=== {Project} ({Framework}): NO tests discovered — refusing to report green ===",
                suite.Project, framework);
            return false;
        }

        return results.ExitCode == 0;
    }

    static string describe(IReadOnlyList<CollapsedTest> collapsed)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Several results under ONE test id (a theory xUnit could not enumerate at discovery —");
        builder.AppendLine("the results above keep only the last row; make the theory's data serializable):");
        foreach (var test in collapsed)
        {
            builder.AppendLine($"  ✗ {test.Uid}{(test.HidesAFailure ? " — a row FAILED" : "")}");
            foreach (var (name, state) in test.Rows) builder.AppendLine($"      {state,-8} {name}");
        }

        return builder.ToString().TrimEnd();
    }

    void appendStepSummary(SupervisedSuite suite, string framework, int workers, SupervisorResults results,
        string text, bool green)
    {
        var summaryFile = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrEmpty(summaryFile)) return;

        try
        {
            var passed = results.Tests.Count(x => x.Final.Outcome.State == WorkerTestState.Passed);
            var skipped = results.Tests.Count(x => x.Final.Outcome.State == WorkerTestState.Skipped);
            var verdict = green ? "✅" : "❌";

            var builder = new StringBuilder();
            builder.AppendLine($"### {verdict} {suite.Project} ({framework})");
            builder.AppendLine();
            builder.AppendLine("| Tests | Passed | Skipped | Failed | Indeterminate | Passed on retry | Stalled | Worker faults | Workers | Wall clock |");
            builder.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            builder.AppendLine(
                $"| {results.Tests.Count} | {passed} | {skipped} | {results.Failed.Count - results.Indeterminate.Count} " +
                $"| {results.Indeterminate.Count} | {results.PassedOnRetry.Count} | {results.StalledTests.Count} " +
                $"| {results.WorkerFaults.Count} | {workers} | {results.Duration.TotalSeconds:F1}s |");
            builder.AppendLine();
            builder.AppendLine("<details><summary>Supervisor run report</summary>");
            builder.AppendLine();
            builder.AppendLine("```text");
            builder.AppendLine(text);
            builder.AppendLine("```");
            builder.AppendLine();
            builder.AppendLine("</details>");
            builder.AppendLine();

            File.AppendAllText(summaryFile, builder.ToString());
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not append the {Project} report to $GITHUB_STEP_SUMMARY", suite.Project);
        }
    }

    // ─── Per-lane temp directories ─────────────────────────────────────

    /// <summary>
    /// Creates .nuke/temp/lanes/&lt;project&gt;-&lt;tfm&gt;/lane-N, emptied first so a leak check over
    /// them sees only this run (scripts/check_no_leaked_databases.py --temp-dir).
    /// </summary>
    static IReadOnlyList<AbsolutePath> prepareLaneDirectories(string project, string framework, int workers)
    {
        var root = LanesRoot / $"{project}-{framework}";
        root.CreateOrCleanDirectory();

        return Enumerable.Range(0, workers)
            .Select(lane =>
            {
                var directory = root / $"lane-{lane}";
                directory.CreateDirectory();
                return directory;
            })
            .ToList();
    }

    /// <summary>
    /// TMPDIR is what Path.GetTempPath() reads on Linux and macOS, TMP (then TEMP) on Windows. Set all
    /// three so the lane directory is the temp directory wherever the worker runs.
    /// </summary>
    static IReadOnlyDictionary<string, string> tempDirectoryEnvironment(AbsolutePath directory)
    {
        // A trailing separator, because that is what GetTempPath returns and some callers concatenate.
        var path = directory.ToString().TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return new Dictionary<string, string>
        {
            ["TMPDIR"] = path,
            ["TMP"] = path,
            ["TEMP"] = path
        };
    }

    // ─── Test host resolution ──────────────────────────────────────────

    string testHostFor(string project, string framework)
    {
        var host = RootDirectory / "src" / project / "bin" / Configuration / framework / project;
        var executable = OperatingSystem.IsWindows() ? host.ToString() + ".exe" : host.ToString();

        if (!File.Exists(executable))
            throw new InvalidOperationException(
                $"{executable} does not exist — build {project} for {framework} ({Configuration}) first. A test " +
                "project builds its own MTP executable only with OutputType=Exe and the xunit.v3 package.");

        return executable;
    }
}
