using Microsoft.Data.Sqlite;

namespace Fisher;

/// <summary>
///     Something that writes inside Fisher's transaction, committed with it (fisher#50).
/// </summary>
/// <remarks>
///     <para>
///         <b>This matters more on SQLite than on either sibling, and for a structural reason.</b> One
///         writer per database file. An application using Fisher for its events and something else —
///         EF Core, Dapper, hand-written ADO.NET — for its relational tables, in the same file, which
///         is the natural thing to do with an embedded database, cannot write both atomically without
///         this. Worse, it cannot write both <em>at all</em> without contending against itself: the two
///         transactions are two writers on one file, and one of them waits or fails with
///         <c>SQLITE_BUSY</c>. On PostgreSQL the equivalent is a nicety; here it is the difference
///         between working and deadlocking against yourself.
///     </para>
///     <para>
///         <b>The inverse of enlistment, and both are worth having.</b>
///         <see cref="SessionOptions.ForTransaction" /> lets a caller hand Fisher a transaction they
///         own; this lets a participant join one Fisher owns. They serve opposite ownership models, and
///         which one fits depends on the participant: a component whose "save" is a method call rather
///         than a connection to borrow — <c>DbContext.SaveChangesAsync</c> being the obvious one — is
///         far easier to wire this way round.
///     </para>
///     <para>
///         <b>The connection is the point.</b> A participant must write on the
///         <see cref="SqliteConnection" /> it is handed, not merely to the same file. Two connections
///         to one file are two writers, and the second blocks on the first — from inside the first
///         one's transaction, which is a genuine self-deadlock rather than a slow path, and which
///         presents as a hang rather than an error. This is the single most likely way to build a
///         participant wrong.
///     </para>
/// </remarks>
public interface ITransactionParticipant
{
    /// <summary>
    ///     Write, on the supplied connection and inside the supplied transaction.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Called as the last thing inside the transaction, immediately before the commit — the same
    ///         position <c>IMessageBatch.BeforeCommitAsync</c> occupies, whose visibility semantics
    ///         fisher#4 already pinned by probing over a separate connection. So nothing this writes is
    ///         visible to anyone else until Fisher commits, and throwing here rolls back Fisher's work
    ///         along with the participant's.
    ///     </para>
    ///     <para>
    ///         <b>This may be called more than once for one unit of work</b>, and a participant has to
    ///         survive it. A retried <c>SQLITE_BUSY</c> re-executes the whole write delegate — the
    ///         property fisher#12 established for the projection batch's own input and fisher#4 for the
    ///         outbox — so whatever this writes must still be pending on the second attempt. The failed
    ///         attempt's transaction rolled back, so re-writing is correct; <em>not</em> re-writing is
    ///         the silent failure, because Fisher's own work commits either way. See
    ///         <see cref="AfterCommitAsync" /> for the other half of that.
    ///     </para>
    /// </remarks>
    Task BeforeCommitAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token);

    /// <summary>
    ///     Reconcile whatever <see cref="BeforeCommitAsync" /> left pending, now that the write is
    ///     durable. Does nothing unless a participant overrides it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is not a post-commit side-effect hook</b>, and a participant wanting one of those
    ///         should still use <c>IDocumentSessionListener</c> (fisher#32), which gets the "everyone
    ///         can see this now" semantics right and is the application's seam rather than a
    ///         participant's. This exists for the narrower job the retry rule above creates: a
    ///         participant that has to keep its writes replayable across attempts needs one place to
    ///         stop keeping them, and only Fisher knows when the commit happened.
    ///     </para>
    ///     <para>
    ///         Runs <b>outside</b> <c>StoreOptions.ResiliencePipeline</c>, so it fires once for a
    ///         transaction that committed rather than once per attempt.
    ///     </para>
    ///     <para>
    ///         <b>It does not fire for an enlisted session</b>, which is the same rule the outbox's
    ///         after-commit hook and the append observer follow: under
    ///         <c>SessionOptions.ForTransaction</c> the commit is the caller's and Fisher is never told
    ///         it happened. A participant enlisted that way is invoked exactly once — there is no retry
    ///         either — so it has nothing to reconcile until its caller commits, and reconciling before
    ///         that would claim a durability Fisher cannot see.
    ///     </para>
    /// </remarks>
    Task AfterCommitAsync(CancellationToken token) => Task.CompletedTask;
}
