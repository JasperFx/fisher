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
/// <remarks>
///     <para>
///         <b>The members are the shared contract's</b> —
///         <see cref="Weasel.Storage.ITransactionParticipant{TConnection,TTransaction}" /> (weasel#561),
///         closed over Microsoft.Data.Sqlite's connection and transaction pair. This declaration is
///         the alias, and nothing else: the two-attempt rule on <c>BeforeCommitAsync</c> and the
///         <c>AfterCommitAsync</c> default that answers it are both documented upstream, and the
///         default <em>came from here</em> — Fisher was the store that had it, so adopting the generic
///         shape is a simplification rather than a loss.
///     </para>
///     <para>
///         The generic interface is contravariant in both parameters, so a participant written against
///         the base <c>DbConnection</c> / <c>DbTransaction</c> pair satisfies this too, and a
///         participant written against the closed shape ports to Polecat or Marten by changing its
///         base declaration alone.
///     </para>
///     <para>
///         The two Fisher-specific facts the shared remarks cannot carry:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <c>BeforeCommitAsync</c> runs at the same position
///                 <c>IMessageBatch.BeforeCommitAsync</c> occupies, whose visibility semantics fisher#4
///                 pinned by probing over a separate connection.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <c>AfterCommitAsync</c> runs outside <c>StoreOptions.ResiliencePipeline</c>, and
///                 <b>does not fire at all for an enlisted session</b> — under
///                 <see cref="SessionOptions.ForTransaction" /> the commit is the caller's and Fisher is
///                 never told it happened, so a participant enlisted that way is invoked exactly once
///                 and has nothing to reconcile until its caller commits. A participant wanting a
///                 genuine post-commit side effect should still use <c>IDocumentSessionListener</c>
///                 (fisher#32).
///             </description>
///         </item>
///     </list>
/// </remarks>
public interface ITransactionParticipant
    : Weasel.Storage.ITransactionParticipant<SqliteConnection, SqliteTransaction>;
