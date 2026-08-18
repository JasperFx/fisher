using Fisher.Services;
using JasperFx.Events.Documents;

namespace Fisher;

/// <summary>
///     Hooks into a session's unit of work: before it is written, after it commits, and as documents
///     pass through it (fisher#32).
/// </summary>
/// <remarks>
///     <para>
///         Registered store-wide through <see cref="StoreOptions.Listeners" /> or for one session
///         through <see cref="SessionOptions.Listeners" />; a session runs the store's list and then
///         its own. The member names are Marten's, so a listener ports between the stores unchanged —
///         the same argument that made the messaging type names match exactly.
///     </para>
///     <para>
///         <b>The two synchronous members are default-implemented</b>, so a listener that only cares
///         about the commit boundary writes two methods and stops — which is also what makes a
///         listener written against Polecat's two-member interface compile here unaltered. Marten
///         supplies a <c>DocumentSessionListenerBase</c> for the same purpose; that class predates
///         default interface members, and Fisher has no need of one.
///     </para>
///     <para>
///         <b>Where the two async hooks sit is already settled and already pinned.</b> Fisher brackets
///         both of its commit paths the same way (fisher#4), and a session listener is simply a second
///         client of that seam: <see cref="BeforeSaveChangesAsync" /> runs before the batch is taken,
///         so a listener may queue further work; <see cref="AfterCommitAsync" /> runs after the commit
///         and <em>outside</em> <see cref="StoreOptions.ResiliencePipeline" />, because a retried
///         <c>SQLITE_BUSY</c> re-executes the whole delegate and a hook invoked inside it would fire
///         twice for a transaction that had already committed.
///     </para>
///     <para>
///         <b>Two cases where a hook does not fire, both deliberate:</b>
///     </para>
///     <list type="bullet">
///         <item>
///             <b>A unit of work with nothing in it.</b> <c>SaveChangesAsync</c> returns early when
///             there is nothing queued, so neither hook runs. Marten does the same; without it every
///             no-op save would run every listener.
///         </item>
///         <item>
///             <b>A session enlisted in a transaction it does not own.</b>
///             <see cref="AfterCommitAsync" /> claims "everyone can see this now", and Fisher is not
///             told when the caller commits — so it does not fire, exactly as the outbox's own
///             after-commit hook and the event store's append observer do not. See
///             <see cref="SessionOptions.ForTransaction" />. <see cref="BeforeSaveChangesAsync" />
///             still runs.
///         </item>
///     </list>
///     <para>
///         <b>An async projection batch does not fire session listeners</b>, and that is a decision
///         rather than an omission. A projection batch is the daemon's unit of work, not the
///         application's; firing user listeners for it would run an application's
///         <see cref="AfterCommitAsync" /> on the daemon's threads for every batch of every shard.
///         JasperFx's <c>IDaemonChangeListener</c> is the hook for that side, and Fisher already
///         supports it — see the subscriptions notes.
///     </para>
///     <para>
///         An exception from <see cref="BeforeSaveChangesAsync" /> fails the unit of work and nothing
///         is written. An exception from <see cref="AfterCommitAsync" /> reaches the caller of
///         <c>SaveChangesAsync</c> but the transaction has already committed — a post-commit hook
///         cannot un-commit anything, on any store.
///     </para>
/// </remarks>
public interface IDocumentSessionListener : IDocumentCommitListener
{
    /// <summary>
    ///     Called at the start of <c>SaveChangesAsync</c>, before anything is written.
    /// </summary>
    /// <remarks>
    ///     Queued work added here joins the same unit of work and commits in the same transaction —
    ///     including appended events, which are collected after this hook runs so a listener can start
    ///     or extend a stream. That is a small divergence from Marten, where the events of the unit of
    ///     work have already been processed by this point.
    /// </remarks>
    Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token);

    /// <summary>
    ///     Called after the transaction has committed, with a snapshot of what it wrote.
    /// </summary>
    Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token);

    /// <summary>
    ///     The store-agnostic spelling of the member above (fisher#104 / jasperfx#679), forwarded to
    ///     it so a listener implements one method rather than two.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A default implementation, and it has to be one.</b> The contract's member differs
    ///         from Fisher's by both of its interesting parameter types, so it is a separate
    ///         signature that Fisher's does not satisfy — deriving without supplying a body would
    ///         make this interface's contract member unimplemented and every existing listener a
    ///         CS0535. That is the one mercy of jasperfx#679 over jasperfx#669: the contract declares
    ///         no default of its own, so the near-miss is a build error rather than a member that
    ///         silently binds to a throwing default. It still is not evidence of anything — see
    ///         below.
    ///     </para>
    ///     <para>
    ///         <b>This forward is the outbound half only, and nothing inside Fisher calls it.</b>
    ///         It exists so that a listener written for Fisher <em>is</em> an
    ///         <see cref="IDocumentCommitListener" /> — store-agnostic code that collects the shared
    ///         type picks Fisher's listeners up unchanged. The inbound half, registering a listener
    ///         that only implements the shared type, is <c>AsSessionListener()</c>; a listener
    ///         registered that way is invoked through Fisher's own member, not through this one. So
    ///         a green build proves neither direction, which is what
    ///         <c>DocumentCommitListenerCompliance</c> is for.
    ///     </para>
    ///     <para>
    ///         The two casts are safe on every path that reaches here from Fisher — the session is
    ///         always a Fisher session and the change set always Fisher's — and would only fail for
    ///         a caller synthesising another store's arguments, which is not a case this interface
    ///         claims to serve.
    ///     </para>
    /// </remarks>
    Task IDocumentCommitListener.AfterCommitAsync(IDocumentSessionOperations session,
        IDocumentChangeSet commit, CancellationToken token)
        => AfterCommitAsync((IDocumentSession)session, (IChangeSet)commit, token);

    /// <summary>
    ///     Called as each document is materialised from the database.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Fires for loads by id and for <c>Query&lt;T&gt;()</c>, on every tracking mode. It does
    ///         not fire for a raw-SQL read: those go through the query-only storage flavour, whose
    ///         SELECT has no identity column, so there is no id to report.
    ///     </para>
    ///     <para>
    ///         Synchronous and on the read path, so keep it cheap — it runs once per row.
    ///     </para>
    /// </remarks>
    void DocumentLoaded(object id, object document)
    {
    }

    /// <summary>
    ///     Called as each document is queued for writing by <c>Store</c>, <c>Insert</c> or
    ///     <c>Update</c> — at the call, not at the commit.
    /// </summary>
    void DocumentAddedForStorage(object id, object document)
    {
    }
}
