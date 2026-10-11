namespace Fisher.Tests.Documentation;

/*
 * The compiled source behind docs/events/subscriptions.md.
 *
 * See "Documentation samples" in CLAUDE.md: every sample a reader would copy lives in a
 * #region here and is pulled into the markdown by mdsnippets, so a sample that stops compiling
 * fails the build rather than going stale in a page nobody rebuilds.
 */

public static class subscription_samples
{
    public static DocumentStore stop_and_drain_timeout(string connectionString)
    {
        #region sample_subscription_stop_and_drain_timeout
        var store = DocumentStore.For(options =>
        {
            options.ConnectionString = connectionString;

            // The default is 5 seconds. A subscription whose pages can take longer than this is cancelled
            // part-way through a page on every shutdown, and that page is processed again next time.
            options.DaemonSettings.StopAndDrainTimeout = TimeSpan.FromSeconds(30);
        });
        #endregion

        return store;
    }
}
