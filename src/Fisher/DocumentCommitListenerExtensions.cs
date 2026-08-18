using Fisher.Services;
using JasperFx.Events.Documents;

namespace Fisher;

/// <summary>
///     Registers a store-agnostic <see cref="IDocumentCommitListener" /> (jasperfx#679) with Fisher
///     (fisher#104).
/// </summary>
/// <remarks>
///     <para>
///         <b>An adapter onto the existing listener list, deliberately, rather than a second list.</b>
///         The shape this could have taken instead — a <c>CommitListeners</c> collection on
///         <see cref="StoreOptions" /> and another on <see cref="SessionOptions" />, with a second
///         loop beside the one in <c>SaveChangesAsync</c> — would have doubled the registration
///         surface, doubled the per-session composition <c>FisherSession.Listeners</c> caches, and
///         created two firing loops with an undefined order between them. Adapting instead means a
///         contract listener is a listener: one list, one cache, one loop, one order, and every
///         behaviour documented on <see cref="IDocumentSessionListener" /> applies to it unchanged
///         because it <em>is</em> one.
///     </para>
///     <para>
///         The reverse direction needs no adapter at all: <see cref="IDocumentSessionListener" />
///         derives from <see cref="IDocumentCommitListener" />, so every Fisher listener already
///         satisfies the shared type.
///     </para>
/// </remarks>
public static class DocumentCommitListenerExtensions
{
    /// <summary>
    ///     Wrap a shared-contract commit listener as a Fisher session listener, for
    ///     <see cref="StoreOptions.Listeners" /> or <see cref="SessionOptions.Listeners" />.
    /// </summary>
    /// <example>
    ///     <code>options.Listeners.Add(myCommitListener.AsSessionListener());</code>
    /// </example>
    public static IDocumentSessionListener AsSessionListener(this IDocumentCommitListener listener)
        => listener as IDocumentSessionListener ?? new CommitListenerAdapter(listener);

    /// <summary>
    ///     A commit listener seen as a session listener: the commit hook forwards, the other three do
    ///     nothing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The session is widened and the change set is not converted.</b>
    ///         <see cref="IDocumentSession" /> already derives from
    ///         <see cref="IDocumentSessionOperations" />, so the first argument is an implicit
    ///         upcast; the second is a cast only because <see cref="IChangeSet" /> and
    ///         <see cref="IDocumentChangeSet" /> are siblings rather than relatives — Fisher's
    ///         <c>ChangeSet</c> implements both, and it is the only thing that ever constructs one.
    ///     </para>
    ///     <para>
    ///         <see cref="BeforeSaveChangesAsync" /> is a no-op rather than absent because it has no
    ///         default: the shared contract is post-commit only, on purpose, and a consumer wanting
    ///         a pre-commit hook implements Fisher's interface instead. The two synchronous members
    ///         are left to their defaults.
    ///     </para>
    /// </remarks>
    private sealed class CommitListenerAdapter(IDocumentCommitListener inner) : IDocumentSessionListener
    {
        public Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
            => Task.CompletedTask;

        public Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
            => inner.AfterCommitAsync(session, (IDocumentChangeSet)commit, token);
    }
}
