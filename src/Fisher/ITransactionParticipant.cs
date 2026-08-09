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
    ///         <b>There is no after-commit hook, deliberately.</b> A participant that wants one is
    ///         asking for a post-commit side effect, which is what <c>IDocumentSessionListener</c>
    ///         (fisher#32) is for — and that one already gets the "everyone can see this now" semantics
    ///         right, including not firing for an enlisted session.
    ///     </para>
    /// </remarks>
    Task BeforeCommitAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token);
}
