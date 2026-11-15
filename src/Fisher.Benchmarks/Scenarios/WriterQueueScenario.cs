using System.Diagnostics;
using Fisher.TestUtils;

namespace Fisher.Benchmarks.Scenarios;

/// <summary>
///     fisher#547: K concurrent writers against one database file, committed two ways — racing for
///     <c>BEGIN IMMEDIATE</c> as Fisher does today, or funnelled through a benchmark-only
///     <see cref="SingleWriterQueue" /> — with the same total work either way.
/// </summary>
/// <remarks>
///     <para>
///         The shape is <see cref="ConcurrentWritersScenario" />'s (each writer opens a lightweight
///         session, stores N small documents and commits, in a loop), so the two modes differ only in
///         who calls <c>SaveChangesAsync</c>. Every round runs on a fresh database file, the two modes
///         alternate which goes first, and the first round is a discarded warmup — the run-to-run noise
///         on a contended SQLite file is wider than most effects worth measuring, which is what
///         fisher#171's measurement found too.
///     </para>
///     <para>
///         Commit latency is measured from just before the commit is requested to its completion: for
///         the racing mode that includes the wait inside <c>BEGIN IMMEDIATE</c>, for the queue it
///         includes the time spent queued. Those are the same quantity from the caller's side, which is
///         the point of comparing them.
///     </para>
/// </remarks>
public static class WriterQueueScenario
{
    public enum Mode
    {
        /// <summary>Today: every writer calls <c>SaveChangesAsync</c> itself and races for the lock.</summary>
        Race,

        /// <summary>fisher#547's candidate: commits handed to one <see cref="SingleWriterQueue" /> consumer.</summary>
        Queue,

        /// <summary>
        ///     A control, not a candidate: an in-process <see cref="SemaphoreSlim" /> of one around the
        ///     writer's own <c>SaveChangesAsync</c>. Same serialization as the queue, but the commit runs
        ///     on the caller's own async flow — so it separates "serializing in-process" from "a channel
        ///     and a consumer task" when reading the queue's numbers.
        /// </summary>
        Gate
    }

    private static readonly Mode[] AllModes = [Mode.Race, Mode.Queue, Mode.Gate];

    public sealed record RoundResult(
        double WallClockMs,
        double[] CommitLatenciesMs,
        double[] LockWaitsMs,
        double[] QueueWaitsMs,
        long AllocatedBytes,
        long RetryEvents,
        long LockRetries,
        int Failures,
        int Commits,
        string[] FailureDescriptions);

    public static async Task<IReadOnlyList<ScenarioReport>> RunAsync(
        int[] writerCounts, int totalCommits, int docsPerCommit, int rounds)
    {
        // Discarded warmup: JIT, the connection pool's first opens, the channel machinery.
        foreach (var mode in AllModes)
        {
            await RunRoundAsync(mode, 8, Math.Max(8, totalCommits / 4), docsPerCommit);
        }

        var results = new Dictionary<(int Writers, Mode Mode), List<RoundResult>>();

        for (var round = 0; round < rounds; round++)
        {
            foreach (var writers in writerCounts)
            {
                // Rotate the order so no mode always runs first, or always on a warmer machine.
                var order = AllModes.Skip(round % AllModes.Length).Concat(AllModes.Take(round % AllModes.Length));
                foreach (var mode in order)
                {
                    var result = await RunRoundAsync(mode, writers, totalCommits, docsPerCommit);
                    if (!results.TryGetValue((writers, mode), out var list))
                    {
                        results[(writers, mode)] = list = new List<RoundResult>();
                    }

                    list.Add(result);
                }
            }
        }

        var reports = new List<ScenarioReport>();
        foreach (var writers in writerCounts)
        {
            foreach (var mode in AllModes)
            {
                reports.Add(Summarize(writers, mode, docsPerCommit, results[(writers, mode)]));
            }
        }

        return reports;
    }

    public static async Task<RoundResult> RunRoundAsync(Mode mode, int writers, int totalCommits, int docsPerCommit)
    {
        var commitsPerWriter = totalCommits / writers;
        var commits = commitsPerWriter * writers;
        var storeName = $"bench-{Guid.NewGuid():n}";

        await using var database = TemporaryDatabase.Create("bench-writer-queue");
        using var lockRecorder = new WriteLockRecorder(storeName);
        await using var store = Harness.BuildStore(database, options =>
        {
            options.StoreName = storeName;
            options.OpenTelemetry.TrackWriteLockContention();
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync();

        // Prewarm the document table so no writer pays the first-use migration mid-race.
        await using (var warm = store.LightweightSession())
        {
            warm.Store(new BenchDoc { Name = "warm" });
            await warm.SaveChangesAsync();
        }

        // The warmup commit took the lock once; start the lock-wait series from zero.
        var warmupWaits = lockRecorder.Waits.Count;

        using var retries = new RetryCounter();
        var queue = mode == Mode.Queue ? new SingleWriterQueue() : null;
        using var gate = new SemaphoreSlim(1, 1);
        var latencies = new double[commits];
        var failures = 0;
        var failureDescriptions = new System.Collections.Concurrent.ConcurrentBag<string>();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

        var elapsed = await Harness.TimeAsync(async () =>
        {
            var tasks = new Task[writers];
            for (var w = 0; w < writers; w++)
            {
                var writer = w;
                tasks[w] = Task.Run(async () =>
                {
                    for (var c = 0; c < commitsPerWriter; c++)
                    {
                        await using var session = store.LightweightSession();
                        for (var d = 0; d < docsPerCommit; d++)
                        {
                            session.Store(new BenchDoc
                            {
                                Name = $"w{writer}-c{c}-d{d}",
                                Number = c,
                                Timestamp = DateTimeOffset.UtcNow
                            });
                        }

                        var started = Stopwatch.GetTimestamp();
                        try
                        {
                            switch (mode)
                            {
                                case Mode.Race:
                                    await session.SaveChangesAsync();
                                    break;

                                case Mode.Queue:
                                    await queue!.CommitAsync(() => session.SaveChangesAsync());
                                    break;

                                case Mode.Gate:
                                    await gate.WaitAsync();
                                    try
                                    {
                                        await session.SaveChangesAsync();
                                    }
                                    finally
                                    {
                                        gate.Release();
                                    }

                                    break;
                            }
                        }
                        catch (Exception e)
                        {
                            var root = e;
                            while (root.InnerException is not null) root = root.InnerException;
                            failureDescriptions.Add(
                                $"{e.GetType().Name} -> {root.GetType().Name}: {root.Message.Split('\n')[0]}");

                            // Counted rather than rethrown, as ConcurrentWritersScenario does, so one
                            // loss under extreme contention doesn't void the round.
                            Interlocked.Increment(ref failures);
                        }

                        latencies[writer * commitsPerWriter + c] =
                            Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    }
                });
            }

            await Task.WhenAll(tasks);
        });

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        double[] queueWaits = [];
        if (queue is not null)
        {
            await queue.DisposeAsync();
            queueWaits = queue.QueueWaits.ToArray();
        }

        return new RoundResult(
            elapsed,
            latencies,
            lockRecorder.Waits.Skip(warmupWaits).ToArray(),
            queueWaits,
            allocated,
            retries.RetryEvents,
            lockRecorder.Retries,
            failures,
            commits,
            failureDescriptions.ToArray());
    }

    private static ScenarioReport Summarize(int writers, Mode mode, int docsPerCommit,
        List<RoundResult> rounds)
    {
        var commits = rounds[0].Commits;
        var walls = rounds.Select(x => x.WallClockMs).Order().ToArray();
        var medianWall = Median(walls);
        var latencies = rounds.SelectMany(x => x.CommitLatenciesMs).Order().ToArray();
        var lockWaits = rounds.SelectMany(x => x.LockWaitsMs).Order().ToArray();
        var lockWaitTotals = rounds.Select(x => x.LockWaitsMs.Sum()).Order().ToArray();
        var queueWaits = rounds.SelectMany(x => x.QueueWaitsMs).Order().ToArray();
        var allocPerCommit = rounds.Select(x => (double)x.AllocatedBytes / x.Commits).Order().ToArray();

        var metrics = new List<Metric>
        {
            new("wall clock, median", Harness.Ms(medianWall)),
            new("wall clock, min .. max", $"{walls[0]:n1} .. {walls[^1]:n1} ms"),
            new("commits/sec at median", Harness.PerSecond(commits, medianWall)),
            new("commit latency p50", Harness.Ms(Percentile(latencies, 0.50))),
            new("commit latency p95", Harness.Ms(Percentile(latencies, 0.95))),
            new("commit latency p99", Harness.Ms(Percentile(latencies, 0.99))),
            new("commit latency max", Harness.Ms(latencies[^1])),
            new("allocated per commit, median", $"{Median(allocPerCommit) / 1024d:n1} KB"),
            new("write_lock.wait p50", Harness.Ms(Percentile(lockWaits, 0.50))),
            new("write_lock.wait p99", Harness.Ms(Percentile(lockWaits, 0.99))),
            new("write_lock.wait summed per round, median", Harness.Ms(Median(lockWaitTotals))),
            new("write_lock.wait > 1 ms (commits that slept)",
                $"{(lockWaits.Length == 0 ? 0 : lockWaits.Count(x => x > 1d) * 100d / lockWaits.Length):n1} %"),
            new("slept waits, busy-handler buckets", SleepSteps(lockWaits)),
            new("write_lock.wait samples / commits", $"{lockWaits.Length:n0} / {commits * rounds.Count:n0}")
        };

        if (mode == Mode.Queue)
        {
            metrics.Add(new Metric("queue wait p50", Harness.Ms(Percentile(queueWaits, 0.50))));
            metrics.Add(new Metric("queue wait p99", Harness.Ms(Percentile(queueWaits, 0.99))));
        }

        metrics.Add(new Metric("fisher.retry events (all rounds)", rounds.Sum(x => x.RetryEvents).ToString("n0")));
        metrics.Add(new Metric("fisher.write_lock.retries (all rounds)",
            rounds.Sum(x => x.LockRetries).ToString("n0")));
        metrics.Add(new Metric("failed commits (all rounds)", rounds.Sum(x => x.Failures).ToString("n0")));
        foreach (var failure in rounds.SelectMany(x => x.FailureDescriptions).Distinct())
        {
            metrics.Add(new Metric("failure", failure));
        }

        return new ScenarioReport(
            $"writer-queue {mode.ToString().ToLowerInvariant()} ({writers} writers, {commits} commits x {docsPerCommit} docs, {rounds.Count} rounds)",
            metrics);
    }

    // SQLite's default busy handler (sqliteDefaultBusyCallback, used for PRAGMA busy_timeout) sleeps
    // 1, 2, 5, 10, 15, 20, 25, 25, 25, 50, 50, 100 ms between polls of the lock, so a waiter that is
    // still waiting has slept a cumulative 1, 3, 8, 18, 33, 53, 78, 103, 128 ... ms. Counting the slept
    // waits up to each cumulative step says whether the wait is the lock being held or the waiter asleep.
    private static readonly double[] BusyHandlerSteps = [3, 8, 18, 33, 53, 78, 103, 128];

    private static string SleepSteps(double[] sorted)
    {
        var slept = sorted.Where(x => x > 1d).ToArray();
        var counts = new int[BusyHandlerSteps.Length + 1];
        foreach (var wait in slept)
        {
            var i = Array.FindIndex(BusyHandlerSteps, step => wait <= step + 2d);
            counts[i < 0 ? BusyHandlerSteps.Length : i]++;
        }

        return string.Join(" ", counts);
    }

    private static double Median(double[] sorted)
        => sorted.Length == 0 ? 0 : sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2d;

    // Nearest-rank on an already sorted array.
    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var rank = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}
