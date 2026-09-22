namespace Fisher.Exceptions;

/// <summary>
///     A unit of work that appended events could not take SQLite's single write lock for this
///     database file (fisher#306).
/// </summary>
/// <remarks>
///     <para>
///         <b>Subclasses the shared <see cref="JasperFx.Events.StreamLockedException" /></b>, which
///         Marten and Polecat both throw when an append loses a lock, so store-agnostic code and a
///         Wolverine <c>OnException&lt;StreamLockedException&gt;().RetryWithCooldown(...)</c> policy
///         — matched with <c>ex is T</c> — behave the same on all three. Before this the caller got a
///         raw <c>SqliteException</c> saying "database is locked", which is true and says nothing
///         about which write lost. Subclassing rather than replacing is the compatible choice, for
///         the reasons <see cref="ExistingStreamIdCollisionException" /> records.
///     </para>
///     <para>
///         <b>The lock is the database file's, not a row's, and that is the one place Fisher's
///         meaning differs from its siblings'.</b> Marten takes an advisory lock and Polecat an
///         <c>UPDLOCK, HOLDLOCK</c> read on the stream row, so their version of this exception really
///         is about one stream. SQLite permits one writer per file, so the thing that was held is the
///         file and every stream in the unit of work lost it together — hence
///         <see cref="StreamIds" /> beside the base's single
///         <see cref="JasperFx.Events.StreamLockedException.StreamId" />, which carries the first of
///         them so the aggregate-handler shape (one stream, one command) reads exactly as it does on
///         the other two stores.
///     </para>
///     <para>
///         <b>A translation at the boundary, not a change in policy.</b> Contention is still retried
///         by <c>StoreOptions.ResiliencePipeline</c> with jittered exponential backoff and still
///         recorded on the current span and on <c>fisher.write_lock.retries</c>; this is what a caller
///         sees once those retries were not enough. <c>TrackWriteLockContention()</c> keeps meaning
///         "retries that happened", and this means "retries that were not enough".
///     </para>
///     <para>
///         <b>A documents-only unit of work keeps its raw <c>SqliteException</c>.</b> A
///         <c>StreamLockedException</c> naming no stream would be a worse answer than the driver's,
///         and there is no stream to name — the same judgement the issue asked for.
///     </para>
///     <para>
///         <b>An enlisted session gets it too, and that is deliberate rather than incidental.</b>
///         <c>SessionOptions.ForTransaction</c> runs no resilience pipeline — a retry would rewrite
///         everything the first attempt already wrote into the caller's still-open transaction — so
///         its contention surfaces on the first statement with no retries at all. That is the
///         documented rough edge of a deferred caller transaction, where the loser gets
///         <c>SQLITE_BUSY</c> rather than a clean concurrency failure; the condition is identical and
///         so is the vocabulary it is reported in.
///     </para>
/// </remarks>
public class StreamLockedException : JasperFx.Events.StreamLockedException
{
    internal StreamLockedException(IReadOnlyList<object> streamIds, Exception? innerException)
        : base(BuildMessage(streamIds), streamIds[0], innerException)
    {
        StreamIds = streamIds;
    }

    /// <summary>
    ///     Every stream the failed unit of work was appending to, in the order it queued them.
    /// </summary>
    /// <remarks>
    ///     The base's <c>StreamId</c> is the first of these. One stream is overwhelmingly the common
    ///     case and is the only one the siblings can express, so the base property is the portable
    ///     answer and this is the complete one.
    /// </remarks>
    public IReadOnlyList<object> StreamIds { get; }

    private static string BuildMessage(IReadOnlyList<object> streamIds)
    {
        var subject = streamIds.Count == 1
            ? $"Event stream '{streamIds[0]}'"
            : $"Event streams '{string.Join("', '", streamIds)}'";

        return $"{subject} could not be written: SQLite permits one writer per database file, and "
               + "this unit of work still lost the write lock after StoreOptions.ResiliencePipeline's "
               + "retries. Retry the unit of work, or reduce the number of writers contending for "
               + "this file — the connection string's Default Timeout bounds how long each attempt "
               + "waits for the lock.";
    }
}
