using System.Diagnostics;

namespace Fisher.Benchmarks.Scenarios;

/// <summary>
///     Counts Fisher's <c>fisher.retry</c> activity events — the telemetry the resilience pipeline
///     records when a call waited on the write lock (SQLITE_BUSY / SQLITE_LOCKED) and was retried.
/// </summary>
/// <remarks>
///     <para>
///         <c>FisherTracing.RecordRetry</c> only records when <c>Activity.Current</c> exists and has
///         <c>IsAllDataRequested</c>, so this listener must sample <c>AllDataAndRecorded</c> for the
///         <c>Fisher</c> source — merely being subscribed is not enough.
///     </para>
///     <para>
///         An <see cref="ActivityListener" /> is process-wide; this one filters to the Fisher source
///         and is disposed with the scenario, which is fine for a single-purpose console process
///         (the same reason the tracing tests filter by tag).
///     </para>
/// </remarks>
public sealed class RetryCounter : IDisposable
{
    private readonly ActivityListener _listener;
    private long _retryEvents;
    private long _retriedActivities;
    private int _maxAttempt;

    public RetryCounter()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Fisher",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _)
                => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = OnStopped
        };

        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>Total <c>fisher.retry</c> events seen — one per retried attempt.</summary>
    public long RetryEvents => Interlocked.Read(ref _retryEvents);

    /// <summary>How many spans (commits, batches, …) recorded at least one retry.</summary>
    public long RetriedActivities => Interlocked.Read(ref _retriedActivities);

    /// <summary>The highest <c>fisher.retry.attempt</c> observed.</summary>
    public int MaxAttempt => _maxAttempt;

    private void OnStopped(Activity activity)
    {
        var sawRetry = false;

        foreach (var activityEvent in activity.Events)
        {
            if (activityEvent.Name != "fisher.retry")
            {
                continue;
            }

            sawRetry = true;
            Interlocked.Increment(ref _retryEvents);

            foreach (var tag in activityEvent.Tags)
            {
                if (tag is { Key: "fisher.retry.attempt", Value: int attempt })
                {
                    int seen;
                    while (attempt > (seen = _maxAttempt)
                           && Interlocked.CompareExchange(ref _maxAttempt, attempt, seen) != seen)
                    {
                    }
                }
            }
        }

        if (sawRetry)
        {
            Interlocked.Increment(ref _retriedActivities);
        }
    }

    public void Dispose() => _listener.Dispose();
}
