using System.Collections.Concurrent;
using System.Diagnostics;
using Fisher.TestUtils;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Fisher.Benchmarks.Scenarios;

/// <summary>
///     fisher#547: K concurrent writers against one database file, committed in different ways — racing
///     for <c>BEGIN IMMEDIATE</c> as Fisher does today, funnelled through a benchmark-only
///     <see cref="SingleWriterQueue" />, a <see cref="SemaphoreSlim" /> control, and (round 2) racing with a
///     different busy handler, a bounded queue, or group commit — with the same total work every time.
/// </summary>
/// <remarks>
///     <para>
///         The shape is <see cref="ConcurrentWritersScenario" />'s (each writer opens a lightweight
///         session, builds a unit of work and commits, in a loop), so the modes differ only in who calls
///         <c>SaveChangesAsync</c> and how a waiter waits. Every round runs on a fresh database file, the
///         modes rotate which goes first, and the first round is a discarded warmup — the run-to-run noise
///         on a contended SQLite file is wider than most effects worth measuring, which is what fisher#171's
///         measurement found too.
///     </para>
///     <para>
///         Commit latency is measured from just before the commit is requested to its completion: for
///         the racing modes that includes the wait inside <c>BEGIN IMMEDIATE</c>, for the queues it
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
        ///     on the caller's own async flow.
        /// </summary>
        Gate,

        /// <summary>Racing, with <see cref="BusyHandlers.Policy.Mimic" /> (a counting copy of SQLite's default handler).</summary>
        BhMimic,

        /// <summary>Racing, with <see cref="BusyHandlers.Policy.Yield" />.</summary>
        BhYield,

        /// <summary>Racing, with <see cref="BusyHandlers.Policy.Spin50Us" />.</summary>
        BhSpin50Us,

        /// <summary>Racing, with <see cref="BusyHandlers.Policy.Sleep1Ms" />.</summary>
        BhSleep1Ms,

        /// <summary>Racing, with <see cref="BusyHandlers.Policy.None" /> (busy_timeout 0, in effect).</summary>
        BhNone,

        /// <summary>The queue on a bounded channel of capacity 1, <c>FullMode.Wait</c>.</summary>
        Bounded1,

        /// <summary>The queue on a bounded channel of capacity 4, <c>FullMode.Wait</c>.</summary>
        Bounded4,

        /// <summary>Group commit: <see cref="GroupCommitQueue" />.</summary>
        Group
    }

    public enum Workload
    {
        /// <summary>Round 1's shape: N small <see cref="BenchDoc" />s per commit.</summary>
        Docs,

        /// <summary>N new streams per commit, one event each. No two commits touch the same stream.</summary>
        EventsMany,

        /// <summary>
        ///     One hot stream: each writer reads the stream's version, then appends one event expecting it.
        ///     A commit that lost the race fails with <c>EventStreamUnexpectedMaxEventIdException</c> and is
        ///     counted as a conflict, not retried.
        /// </summary>
        EventsHot
    }

    public static readonly Mode[] RoundOneModes = [Mode.Race, Mode.Queue, Mode.Gate];

    public static string Name(Mode mode) => mode switch
    {
        Mode.BhMimic => "bh-mimic",
        Mode.BhYield => "bh-yield",
        Mode.BhSpin50Us => "bh-spin50us",
        Mode.BhSleep1Ms => "bh-sleep1ms",
        Mode.BhNone => "bh-none",
        _ => mode.ToString().ToLowerInvariant()
    };

    public static Mode ParseMode(string name)
    {
        foreach (var mode in Enum.GetValues<Mode>())
        {
            if (Name(mode) == name.Trim().ToLowerInvariant()) return mode;
        }

        throw new ArgumentException(
            $"Unknown mode '{name}'. Known: {string.Join(", ", Enum.GetValues<Mode>().Select(Name))}");
    }

    private static BusyHandlers.Policy? PolicyFor(Mode mode) => mode switch
    {
        Mode.BhMimic => BusyHandlers.Policy.Mimic,
        Mode.BhYield => BusyHandlers.Policy.Yield,
        Mode.BhSpin50Us => BusyHandlers.Policy.Spin50Us,
        Mode.BhSleep1Ms => BusyHandlers.Policy.Sleep1Ms,
        Mode.BhNone => BusyHandlers.Policy.None,
        _ => null
    };

    public sealed record Settings(
        Mode[] Modes,
        int[] WriterCounts,
        int TotalCommits,
        int ItemsPerCommit,
        int Rounds,
        Workload Workload = Workload.Docs,
        bool WithDaemon = false,
        int GroupSize = 64);

    public sealed record RoundResult(
        double WallClockMs,
        double[] CommitLatenciesMs,
        double[] LockWaitsMs,
        double[] DaemonLockWaitsMs,
        double[] QueueWaitsMs,
        long AllocatedBytes,
        double CpuMs,
        long RetryEvents,
        long LockRetries,
        int Failures,
        int Conflicts,
        int Commits,
        string[] FailureDescriptions,
        long HandlerInvocations,
        long HandlerEpisodes,
        long HandlerGaveUp,
        double HandlerMs,
        double DaemonCatchUpMs,
        double[] GroupSizes,
        double[] GroupBeginWaitsMs,
        int GroupsFailed);

    public static async Task<IReadOnlyList<ScenarioReport>> RunAsync(Settings settings)
    {
        // Discarded warmup: JIT, the connection pool's first opens, the channel machinery.
        foreach (var mode in settings.Modes)
        {
            await RunRoundAsync(settings, mode, 8, Math.Max(8, settings.TotalCommits / 4));
        }

        var results = new Dictionary<(int Writers, Mode Mode), List<RoundResult>>();
        var modes = settings.Modes;

        for (var round = 0; round < settings.Rounds; round++)
        {
            foreach (var writers in settings.WriterCounts)
            {
                // Rotate the order so no mode always runs first, or always on a warmer machine.
                var shift = round % modes.Length;
                foreach (var mode in modes.Skip(shift).Concat(modes.Take(shift)))
                {
                    var result = await RunRoundAsync(settings, mode, writers, settings.TotalCommits);
                    if (!results.TryGetValue((writers, mode), out var list))
                    {
                        results[(writers, mode)] = list = new List<RoundResult>();
                    }

                    list.Add(result);
                }
            }
        }

        var reports = new List<ScenarioReport>();
        foreach (var writers in settings.WriterCounts)
        {
            foreach (var mode in modes)
            {
                reports.Add(Summarize(settings, writers, mode, results[(writers, mode)]));
            }
        }

        return reports;
    }

    public static async Task<RoundResult> RunRoundAsync(Settings settings, Mode mode, int writers, int totalCommits)
    {
        var commitsPerWriter = totalCommits / writers;
        var commits = commitsPerWriter * writers;
        var itemsPerCommit = settings.ItemsPerCommit;
        var storeName = $"bench-{Guid.NewGuid():n}";
        var policy = PolicyFor(mode);

        await using var database = TemporaryDatabase.Create("bench-writer-queue");
        using var lockRecorder = new WriteLockRecorder(storeName);
        await using var store = Harness.BuildStore(database, options =>
        {
            options.StoreName = storeName;
            options.OpenTelemetry.TrackWriteLockContention();
            if (settings.WithDaemon)
            {
                options.Projections.Snapshot<BenchTally>(SnapshotLifecycle.Async);
            }

            if (policy is { } p)
            {
                // Fisher installs its own 1 ms handler after every open since fisher#547, which would
                // replace this one. Turn it off so a bh-* mode measures its own policy; plain "race" is
                // now Fisher's handler, and bh-mimic is SQLite's default.
                options.UseFixedSleepBusyHandler = false;
                BusyHandlers.InstallOn(options, p);
            }
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync();

        // Prewarm the tables so no writer pays a first-use migration mid-race, and create the hot stream.
        var hotStream = Guid.NewGuid();
        await using (var warm = store.LightweightSession())
        {
            warm.Store(new BenchDoc { Name = "warm" });
            warm.Events.StartStream<BenchTally>(hotStream, new BenchCheckIn(0));
            await warm.SaveChangesAsync();
        }

        IProjectionDaemon? daemon = null;
        if (settings.WithDaemon)
        {
            daemon = await store.BuildProjectionDaemonAsync();
            await daemon.StartAllAsync();
            await store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromMinutes(1));
        }

        try
        {
            // The warmup commits took the lock; start the lock-wait series from zero.
            var warmupWaits = lockRecorder.WaitsByHolder.Count;

            using var retries = new RetryCounter();
            var queue = mode switch
            {
                Mode.Queue => new SingleWriterQueue(),
                Mode.Bounded1 => new SingleWriterQueue(1),
                Mode.Bounded4 => new SingleWriterQueue(4),
                _ => null
            };
            var group = mode == Mode.Group
                ? await GroupCommitQueue.StartAsync(store, database.ConnectionString, settings.GroupSize)
                : null;
            using var gate = new SemaphoreSlim(1, 1);
            var latencies = new double[commits];
            var failures = 0;
            var conflicts = 0;
            var failureDescriptions = new ConcurrentBag<string>();

            if (policy is { } p) BusyHandlers.StatsFor(p).Reset();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;

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
                            var session = store.LightweightSession();
                            Action<IDocumentSession> apply;
                            try
                            {
                                apply = await PrepareAsync(settings.Workload, session, hotStream, writer, c,
                                    itemsPerCommit);
                            }
                            catch
                            {
                                await session.DisposeAsync();
                                throw;
                            }

                            var started = 0L;
                            try
                            {
                                if (mode == Mode.Group)
                                {
                                    // The unit of work is replayed onto the consumer's enlisted session, so
                                    // the writer's session only did the reading.
                                    await session.DisposeAsync();
                                    started = Stopwatch.GetTimestamp();
                                    await group!.CommitAsync(s =>
                                    {
                                        apply(s);
                                        return Task.CompletedTask;
                                    });
                                }
                                else
                                {
                                    apply(session);
                                    started = Stopwatch.GetTimestamp();
                                    switch (mode)
                                    {
                                        case Mode.Queue:
                                        case Mode.Bounded1:
                                        case Mode.Bounded4:
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

                                        default:
                                            await session.SaveChangesAsync();
                                            break;
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                if (IsConflict(e))
                                {
                                    Interlocked.Increment(ref conflicts);
                                }
                                else
                                {
                                    var root = e;
                                    while (root.InnerException is not null) root = root.InnerException;
                                    failureDescriptions.Add(
                                        $"{e.GetType().Name} -> {root.GetType().Name}: {root.Message.Split('\n')[0]}");

                                    // Counted rather than rethrown, as ConcurrentWritersScenario does, so one
                                    // loss under extreme contention doesn't void the round.
                                    Interlocked.Increment(ref failures);
                                }
                            }
                            finally
                            {
                                if (mode != Mode.Group) await session.DisposeAsync();
                            }

                            latencies[writer * commitsPerWriter + c] =
                                Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        }
                    });
                }

                await Task.WhenAll(tasks);
            });

            var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds;
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

            var catchUpMs = 0d;
            if (daemon is not null)
            {
                catchUpMs = await Harness.TimeAsync(
                    () => store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromMinutes(5)));
            }

            double[] queueWaits = [];
            if (queue is not null)
            {
                await queue.DisposeAsync();
                queueWaits = queue.QueueWaits.ToArray();
            }

            double[] groupSizes = [];
            double[] beginWaits = [];
            var groupsFailed = 0;
            if (group is not null)
            {
                await group.DisposeAsync();
                queueWaits = group.QueueWaits.ToArray();
                groupSizes = group.GroupSizes.Select(x => (double)x).ToArray();
                beginWaits = group.BeginWaits.ToArray();
                groupsFailed = group.FailedGroups;
            }

            var stats = policy is { } q ? BusyHandlers.StatsFor(q) : null;
            var waits = lockRecorder.WaitsByHolder.Skip(warmupWaits).ToArray();

            return new RoundResult(
                elapsed,
                latencies,
                waits.Where(x => x.Holder != "daemon").Select(x => x.Wait).ToArray(),
                waits.Where(x => x.Holder == "daemon").Select(x => x.Wait).ToArray(),
                queueWaits,
                allocated,
                cpu,
                retries.RetryEvents,
                lockRecorder.Retries,
                failures,
                conflicts,
                commits,
                failureDescriptions.ToArray(),
                stats?.Invocations ?? 0,
                stats?.Episodes ?? 0,
                stats?.GaveUp ?? 0,
                stats?.MillisecondsInHandler ?? 0,
                catchUpMs,
                groupSizes,
                beginWaits,
                groupsFailed);
        }
        finally
        {
            if (daemon is not null)
            {
                await daemon.StopAllAsync();
                daemon.Dispose();
            }
        }
    }

    private static async Task<Action<IDocumentSession>> PrepareAsync(Workload workload, IDocumentSession session,
        Guid hotStream, int writer, int commit, int items)
    {
        switch (workload)
        {
            case Workload.EventsMany:
                return s =>
                {
                    for (var i = 0; i < items; i++)
                    {
                        s.Events.StartStream<BenchTally>(Guid.NewGuid(), new BenchCheckIn(commit));
                    }
                };

            case Workload.EventsHot:
            {
                var state = await session.Events.FetchStreamStateAsync(hotStream);
                var expected = state!.Version + 1;
                return s => s.Events.Append(hotStream, expected, new BenchCheckIn(commit));
            }

            default:
                return s =>
                {
                    for (var d = 0; d < items; d++)
                    {
                        s.Store(new BenchDoc
                        {
                            Name = $"w{writer}-c{commit}-d{d}",
                            Number = commit,
                            Timestamp = DateTimeOffset.UtcNow
                        });
                    }
                };
        }
    }

    private static bool IsConflict(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
        {
            if (x is EventStreamUnexpectedMaxEventIdException) return true;
        }

        return false;
    }

    private static ScenarioReport Summarize(Settings settings, int writers, Mode mode, List<RoundResult> rounds)
    {
        var commits = rounds[0].Commits;
        var walls = rounds.Select(x => x.WallClockMs).Order().ToArray();
        var medianWall = Median(walls);
        var latencies = rounds.SelectMany(x => x.CommitLatenciesMs).Order().ToArray();
        var lockWaits = rounds.SelectMany(x => x.LockWaitsMs).Order().ToArray();
        var lockWaitTotals = rounds.Select(x => x.LockWaitsMs.Sum()).Order().ToArray();
        var queueWaits = rounds.SelectMany(x => x.QueueWaitsMs).Order().ToArray();
        var allocPerCommit = rounds.Select(x => (double)x.AllocatedBytes / x.Commits).Order().ToArray();
        var cpu = rounds.Select(x => x.CpuMs).Order().ToArray();

        var metrics = new List<Metric>
        {
            new("wall clock, median", Harness.Ms(medianWall)),
            new("wall clock, min .. max", $"{walls[0]:n1} .. {walls[^1]:n1} ms"),
            new("commits/sec at median", Harness.PerSecond(commits, medianWall)),
            new("commit latency p50", Harness.Ms(Percentile(latencies, 0.50))),
            new("commit latency p95", Harness.Ms(Percentile(latencies, 0.95))),
            new("commit latency p99", Harness.Ms(Percentile(latencies, 0.99))),
            new("commit latency max", Harness.Ms(latencies[^1])),
            new("process CPU per round, median", Harness.Ms(Median(cpu))),
            new("process CPU / wall clock, median", $"{Median(rounds.Select(x => x.CpuMs / x.WallClockMs).Order().ToArray()):n2} cores"),
            new("allocated per commit, median", $"{Median(allocPerCommit) / 1024d:n1} KB"),
            new("write_lock.wait p50", Harness.Ms(Percentile(lockWaits, 0.50))),
            new("write_lock.wait p99", Harness.Ms(Percentile(lockWaits, 0.99))),
            new("write_lock.wait summed per round, median", Harness.Ms(Median(lockWaitTotals))),
            new("write_lock.wait > 1 ms (commits that slept)",
                $"{(lockWaits.Length == 0 ? 0 : lockWaits.Count(x => x > 1d) * 100d / lockWaits.Length):n1} %"),
            new("slept waits, busy-handler buckets", SleepSteps(lockWaits)),
            new("write_lock.wait samples / commits", $"{lockWaits.Length:n0} / {commits * rounds.Count:n0}")
        };

        if (PolicyFor(mode) is not null)
        {
            metrics.Add(new Metric("busy handler: invocations per round, median",
                $"{Median(rounds.Select(x => (double)x.HandlerInvocations).Order().ToArray()):n0}"));
            metrics.Add(new Metric("busy handler: waits (count==0) per round, median",
                $"{Median(rounds.Select(x => (double)x.HandlerEpisodes).Order().ToArray()):n0}"));
            metrics.Add(new Metric("busy handler: time inside per round, median",
                Harness.Ms(Median(rounds.Select(x => x.HandlerMs).Order().ToArray()))));
            metrics.Add(new Metric("busy handler: gave up (all rounds)", rounds.Sum(x => x.HandlerGaveUp).ToString("n0")));
        }

        if (queueWaits.Length > 0)
        {
            metrics.Add(new Metric("queue wait p50", Harness.Ms(Percentile(queueWaits, 0.50))));
            metrics.Add(new Metric("queue wait p99", Harness.Ms(Percentile(queueWaits, 0.99))));
        }

        if (mode == Mode.Group)
        {
            var sizes = rounds.SelectMany(x => x.GroupSizes).Order().ToArray();
            var begins = rounds.SelectMany(x => x.GroupBeginWaitsMs).Order().ToArray();
            metrics.Add(new Metric("group size p50 / max", $"{Percentile(sizes, 0.5):n0} / {sizes.LastOrDefault():n0}"));
            metrics.Add(new Metric("groups per round, median",
                $"{Median(rounds.Select(x => (double)x.GroupSizes.Length).Order().ToArray()):n0}"));
            metrics.Add(new Metric("group BEGIN IMMEDIATE wait p50 / max",
                $"{Percentile(begins, 0.5):n2} / {begins.LastOrDefault():n2} ms"));
            metrics.Add(new Metric("groups whose COMMIT failed (all rounds)", rounds.Sum(x => x.GroupsFailed).ToString("n0")));
        }

        if (settings.WithDaemon)
        {
            var daemonWaits = rounds.SelectMany(x => x.DaemonLockWaitsMs).Order().ToArray();
            metrics.Add(new Metric("daemon catch-up after writes, median",
                Harness.Ms(Median(rounds.Select(x => x.DaemonCatchUpMs).Order().ToArray()))));
            metrics.Add(new Metric("daemon catch-up, min .. max",
                $"{rounds.Min(x => x.DaemonCatchUpMs):n1} .. {rounds.Max(x => x.DaemonCatchUpMs):n1} ms"));
            metrics.Add(new Metric("daemon write_lock.wait p50 / p99 / max",
                $"{Percentile(daemonWaits, 0.5):n1} / {Percentile(daemonWaits, 0.99):n1} / {daemonWaits.LastOrDefault():n1} ms"));
            metrics.Add(new Metric("daemon write_lock.wait summed per round, median",
                Harness.Ms(Median(rounds.Select(x => x.DaemonLockWaitsMs.Sum()).Order().ToArray()))));
            metrics.Add(new Metric("daemon batches per round, median",
                $"{Median(rounds.Select(x => (double)x.DaemonLockWaitsMs.Length).Order().ToArray()):n0}"));
        }

        if (settings.Workload == Workload.EventsHot)
        {
            var conflicts = rounds.Sum(x => x.Conflicts);
            metrics.Add(new Metric("version conflicts (all rounds)",
                $"{conflicts:n0} of {commits * rounds.Count:n0} ({conflicts * 100d / (commits * rounds.Count):n1} %)"));
        }

        metrics.Add(new Metric("fisher.retry events (all rounds)", rounds.Sum(x => x.RetryEvents).ToString("n0")));
        metrics.Add(new Metric("fisher.write_lock.retries (all rounds)",
            rounds.Sum(x => x.LockRetries).ToString("n0")));
        metrics.Add(new Metric("failed commits (all rounds)", rounds.Sum(x => x.Failures).ToString("n0")));
        foreach (var failure in rounds.SelectMany(x => x.FailureDescriptions).Distinct())
        {
            metrics.Add(new Metric("failure", failure));
        }

        var workload = settings.Workload switch
        {
            Workload.EventsMany => $"{commits} commits x {settings.ItemsPerCommit} new streams",
            Workload.EventsHot => $"{commits} commits x 1 event on one hot stream",
            _ => $"{commits} commits x {settings.ItemsPerCommit} docs"
        };

        return new ScenarioReport(
            $"writer-queue {Name(mode)} ({writers} writers, {workload}{(settings.WithDaemon ? ", async daemon running" : "")}, {rounds.Count} rounds)",
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
