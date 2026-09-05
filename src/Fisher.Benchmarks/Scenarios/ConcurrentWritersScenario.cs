using Fisher.TestUtils;

namespace Fisher.Benchmarks.Scenarios;

/// <summary>
///     Scenario 4: K parallel writers against one database file — the discriminating scenario for
///     the write-lock and PRAGMA findings, since SQLite takes one writer per file and every commit
///     here contends for the same <c>BEGIN IMMEDIATE</c>.
/// </summary>
/// <remarks>
///     <para>
///         Retries are surfaced through Fisher's own telemetry: the <c>fisher.retry</c> activity
///         events the Polly pipeline records on the enclosing span (see <see cref="RetryCounter" />).
///         Two caveats when reading the numbers. First, the wait at <c>BEGIN IMMEDIATE</c> comes from
///         the connection string's <c>Default Timeout</c> (30s), so a contended commit can simply
///         wait and succeed with <em>no</em> retry — a low retry count with a high wall clock is
///         still contention. Second, the retry event only fires when a command-level
///         SQLITE_BUSY/SQLITE_LOCKED reaches the resilience pipeline.
///     </para>
///     <para>
///         Baseline comparison: the same total work single-writer (K=1) versus K-way parallel says
///         what the file's one write lock costs; the per-tenant/per-file answer to that cost is
///         database-per-tenant, which this scenario deliberately does not use.
///     </para>
/// </remarks>
public static class ConcurrentWritersScenario
{
    public static async Task<ScenarioReport> RunAsync(int writers, int commitsPerWriter, int docsPerCommit)
    {
        await using var database = TemporaryDatabase.Create("bench-concurrent");
        await using var store = Harness.BuildStore(database);
        await store.ApplyAllConfiguredChangesToDatabaseAsync();

        // Prewarm the document table so no writer pays the first-use migration mid-race.
        await using (var warm = store.LightweightSession())
        {
            warm.Store(new BenchDoc { Name = "warm" });
            await warm.SaveChangesAsync();
        }

        using var retries = new RetryCounter();
        var failures = 0;

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
                        try
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

                            await session.SaveChangesAsync();
                        }
                        catch
                        {
                            // A commit that exhausted the resilience pipeline. Counted rather than
                            // rethrown so one loss under extreme contention doesn't void the run.
                            Interlocked.Increment(ref failures);
                        }
                    }
                });
            }

            await Task.WhenAll(tasks);
        });

        var totalCommits = writers * commitsPerWriter;

        return new ScenarioReport(
            $"concurrent-writers ({writers} writers x {commitsPerWriter} commits x {docsPerCommit} docs)",
        [
            new Metric("total", Harness.Ms(elapsed)),
            new Metric("commits/sec", Harness.PerSecond(totalCommits, elapsed)),
            new Metric("docs/sec", Harness.PerSecond(totalCommits * docsPerCommit, elapsed)),
            new Metric("fisher.retry events", retries.RetryEvents.ToString("n0")),
            new Metric("commits that retried", retries.RetriedActivities.ToString("n0")),
            new Metric("max retry attempt", retries.MaxAttempt.ToString("n0")),
            new Metric("failed commits", failures.ToString("n0"))
        ]);
    }
}
