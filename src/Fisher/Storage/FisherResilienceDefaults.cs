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

    public static ResiliencePipelineBuilder AddFisherDefaults(this ResiliencePipelineBuilder builder)
    {
        return builder.AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<SqliteException>(IsTransient),
            MaxRetryAttempts = 5,
            Delay = TimeSpan.FromMilliseconds(50),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true
        });
    }

    /// <summary>
    ///     Whether a SQLite error is a contention failure worth retrying, as opposed to a genuine
    ///     schema or constraint error that will fail identically no matter how often it is retried.
    /// </summary>
    internal static bool IsTransient(SqliteException exception)
        => exception.SqliteErrorCode is SqliteBusy or SqliteLocked;
}
