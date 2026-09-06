using Microsoft.Data.Sqlite;
using Polly;
using Polly.Retry;

namespace Fisher.Storage;

/// <summary>
///     The default Polly pipeline wrapped around every command Fisher executes.
/// </summary>
/// <remarks>
///     <para>
///         Unlike Polecat's (which is an empty pass-through, because SqlClient retries transient
///         faults itself), this one carries real work. SQLite serializes writers at the database-file
///         level, so a second connection attempting to write while another holds the write lock gets
///         <c>SQLITE_BUSY</c> back rather than waiting — the <c>busy_timeout</c> PRAGMA covers only
///         some of those cases, and notably does not cover <c>SQLITE_BUSY_SNAPSHOT</c> under WAL.
///         Retrying with backoff is what makes a concurrent session and async daemon coexist.
///     </para>
/// </remarks>
internal static class FisherResilienceDefaults
{
    /// <summary>SQLITE_BUSY — the database file is locked by another connection.</summary>
    private const int SqliteBusy = 5;

    /// <summary>SQLITE_LOCKED — a table in the database is locked.</summary>
    private const int SqliteLocked = 6;

    /// <param name="options">
    ///     The store whose <c>OpenTelemetry</c> counters a retry is reported to, or null for a
    ///     pipeline built with no store behind it. Read at retry time rather than captured as a
    ///     counter, so an application that opts in after the pipeline is built is still served.
    /// </param>
    public static ResiliencePipelineBuilder AddFisherDefaults(this ResiliencePipelineBuilder builder,
        StoreOptions? options = null)
    {
        return builder.AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<SqliteException>(IsTransient),
            MaxRetryAttempts = 5,
            Delay = TimeSpan.FromMilliseconds(50),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,

            // fisher#48. A SQLITE_BUSY retry is the single most useful thing Fisher can report and
            // was the one thing nothing could see: a request that spent its time queued behind
            // another writer looked exactly like a request that was slow. Recorded as an event on
            // whatever span is current rather than as a span of its own — a retry is the same
            // operation happening again, not a nested one.
            OnRetry = arguments =>
            {
                Internal.FisherTracing.RecordRetry(
                    arguments.AttemptNumber + 1, arguments.RetryDelay, arguments.Outcome.Exception);

                // fisher#208. The metric beside the span event, and the two are not redundant: a span
                // event is per-trace and answers "why was *this* call slow", where the counter is what
                // an alert can be hung off. Deliberately NOT the only contention instrument — see
                // OpenTelemetryOptions.TrackWriteLockContention for the measurement that says a retry
                // counter alone reads zero through real contention.
                options?.OpenTelemetry.RecordWriteLockRetry(arguments.Outcome.Exception);

                return default;
            }
        });
    }

    /// <summary>
    ///     Whether a SQLite error is a contention failure worth retrying, as opposed to a genuine
    ///     schema or constraint error that will fail identically no matter how often it is retried.
    /// </summary>
    internal static bool IsTransient(SqliteException exception)
        => exception.SqliteErrorCode is SqliteBusy or SqliteLocked;
}
