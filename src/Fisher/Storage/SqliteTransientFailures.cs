using JasperFx.Events.Daemon;
using Microsoft.Data.Sqlite;

namespace Fisher.Storage;

/// <summary>
///     Tells JasperFx which SQLite failures are expected to clear on their own, for
///     <see cref="ShardFailure.IsTransient" /> (wolverine#4986, jasperfx#1016).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why Fisher needs one at all.</b> <see cref="TransientFailures" /> trusts
///         <see cref="System.Data.Common.DbException.IsTransient" />, which Npgsql and SqlClient implement
///         from the server's error code. Microsoft.Data.Sqlite does not override it, so it is false for
///         every <see cref="SqliteException" />: probed on 10.0.9, <c>SQLITE_BUSY</c> (5) reads exactly
///         like a missing column (1). SQLite takes one writer per file, so <c>SQLITE_BUSY</c> is not an
///         edge case for Fisher — it is the normal transient fault, and without this a supervisor would
///         treat a shard that lost the write lock as one that needs an operator.
///     </para>
///     <para>
///         <b>The code set</b>, by primary result code (https://www.sqlite.org/rescode.html). An extended
///         code is <c>primary | (n &lt;&lt; 8)</c>, so the low byte is masked off first:
///         <c>SQLITE_BUSY_SNAPSHOT</c> (517), which <c>busy_timeout</c> does not cover under WAL, is
///         still <c>SQLITE_BUSY</c>.
///     </para>
///     <list type="bullet">
///         <item><c>SQLITE_BUSY</c> (5) — another connection holds the file's lock. Transient.</item>
///         <item><c>SQLITE_LOCKED</c> (6) — a conflict inside one connection or a shared cache.
///         Transient.</item>
///         <item><c>SQLITE_PROTOCOL</c> (15) — transient. rescode.html describes it as the WAL-mode race
///         between two connections
///         starting a transaction at once, returned only after the loser has backed off and lost
///         repeatedly for several seconds: contention, the same family as <c>SQLITE_BUSY</c>, and WAL is
///         Fisher's default.</item>
///         <item><c>SQLITE_IOERR</c> (10) — <b>not</b> transient. The operating system failed a read
///         or write; its extended codes span short reads, fsync and mmap failures, which are a disk or
///         a file system in trouble far more often than a blip, and restarting a shard onto it in a
///         loop is the outcome this label exists to prevent.</item>
///         <item><c>SQLITE_FULL</c> (13) — <b>not</b> transient. The disk is full; it clears when
///         somebody frees space, which is an operator's job.</item>
///         <item><c>SQLITE_CANTOPEN</c> (14) — <b>not</b> transient. A bad path, a missing directory or
///         a permission problem, none of which changes on its own.</item>
///     </list>
///     <para>
///         Every other <see cref="SqliteException" /> is answered <c>false</c>, and anything that is not
///         one is answered <c>null</c> so the built-in rules still decide it. A <c>false</c> here does
///         not hide a transient cause underneath: <see cref="TransientFailures.IsTransient" /> walks on
///         into the inner exception whenever a classifier says no.
///     </para>
///     <para>
///         <b>This is a label, not a retry.</b> Retrying inside a command is
///         <see cref="FisherResilienceDefaults" />' job, but its predicate reads <see cref="IsTransientCode" />
///         too, so the codes Fisher retries and the codes it labels transient are one list.
///     </para>
/// </remarks>
internal static class SqliteTransientFailures
{
    private const int PrimaryCodeMask = 0xFF;

    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteProtocol = 15;

    // One delegate instance for the life of the process, which is what lets Register() be called from
    // every store's constructor: AddClassifier ignores an instance it already holds.
    private static readonly Func<Exception, bool?> _classifier = classify;

    /// <summary>
    ///     Add the classifier to <see cref="TransientFailures" />. Idempotent and cheap; called by every
    ///     <see cref="DocumentStore" />.
    /// </summary>
    internal static void Register() => TransientFailures.AddClassifier(_classifier);

    /// <summary>Whether a SQLite result code, primary or extended, names a fault that clears on its own.</summary>
    internal static bool IsTransientCode(int errorCode)
        => (errorCode & PrimaryCodeMask) is SqliteBusy or SqliteLocked or SqliteProtocol;

    private static bool? classify(Exception exception)
        => exception is SqliteException sqlite ? IsTransientCode(sqlite.SqliteErrorCode) : null;
}
