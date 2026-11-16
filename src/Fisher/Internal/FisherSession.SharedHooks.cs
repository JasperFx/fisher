using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Events.Documents;

namespace Fisher.Internal;

/// <summary>
///     fisher#581. The three members of JasperFx's store-agnostic document contract that a message bus
///     needs and that used to fall through to the contract's throwing defaults:
///     <see cref="IDocumentWriteOperations.StoreObjects" />,
///     <see cref="IDocumentWriteOperations.AddTransactionParticipant(IDbTransactionParticipant)" /> and
///     <see cref="IDocumentSessionOperations.AddSaveListener" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>All three are explicit</b>, which is the non-covariance trap from the other side. Fisher has
///         a public <c>AddTransactionParticipant(ITransactionParticipant)</c> of the same name, so a public
///         overload taking the shared type would make every call with a participant implementing both
///         ambiguous; and the other two are kept beside it so that the contract's whole hook surface is
///         read in one place. Deleting any of them still compiles, and only a caller holding the session
///         as the contract notices: <c>shared_session_hooks</c> is what says so.
///     </para>
///     <para>
///         <b>The shared hooks are JasperFx's <see cref="DocumentSessionHooks" />, composed rather than
///         re-implemented.</b> It carries the sequencing the contract promises and the three stores used
///         to disagree about: a shared participant is released when its commit lands rather than when the
///         session is disposed, after-commit work sees no caller token, and one failing after-commit hook
///         does not starve the next. Fisher's own listeners and participants stay in Fisher's loops, so a
///         session that never touches the shared members behaves as it did.
///     </para>
/// </remarks>
internal partial class FisherSession
{
    // Created on first use through LazilyCreate, because the daemon's projection slices reach the same
    // session concurrently and a participant can be enlisted from a projection's side effects.
    private DocumentSessionHooks? _hooks;

    /// <summary>
    ///     The shared hooks, or null when nothing has asked for one. Read without creating, so a session
    ///     that never touched the contract's hook members pays a null check per save.
    /// </summary>
    internal DocumentSessionHooks? SharedHooks => _hooks;

    private DocumentSessionHooks EnsureSharedHooks()
        => LazilyCreate(ref _hooks, static () => new DocumentSessionHooks());

    /// <summary>
    ///     Enlist a store-agnostic participant in the transaction that commits this unit of work.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Runs in the same transaction, at the same position and with the same retry semantics as
    ///         Fisher's own <see cref="ITransactionParticipant" />: after Fisher's own participants and
    ///         before the message batch, on every attempt, on both commit paths. A session taken from a
    ///         projection batch hands it to the batch, which is how a subscription's outbox joins the
    ///         daemon's transaction (jasperfx#1052).
    ///     </para>
    ///     <para>
    ///         <b>Released when its commit lands</b>, where Fisher's own participant stays enlisted until
    ///         the session is disposed. That is the shared contract's rule, and it is what stops a session
    ///         saved twice from writing an envelope row twice.
    ///     </para>
    ///     <para>
    ///         ⚠️ <b>Refused on an enlisted session.</b> The commit is the caller's and Fisher is never
    ///         told it landed, so the participant could never be released: a second save would run it a
    ///         second time. The contract names exactly that shape as one a store must refuse rather than
    ///         accept quietly.
    ///     </para>
    /// </remarks>
    void IDocumentWriteOperations.AddTransactionParticipant(IDbTransactionParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);

        // On the parent: a tenant scope shares its unit of work, so there is one transaction to join.
        if (_parent is not null)
        {
            ((IDocumentWriteOperations)_parent).AddTransactionParticipant(participant);
            return;
        }

        if (EnlistedTransaction is not null)
        {
            throw new NotSupportedException(
                "A session enlisted in a transaction the caller owns cannot take a shared "
                + "IDbTransactionParticipant. The caller commits, so Fisher never learns the commit landed and "
                + "could not release the participant: a second SaveChangesAsync would run it again. Enlist it on "
                + "a session that owns its transaction, or write those rows yourself inside the transaction you "
                + "handed to SessionOptions.ForTransaction.");
        }

        EnsureSharedHooks().AddParticipant(participant);
    }

    /// <summary>
    ///     Run a store-agnostic save listener on this session's saves, for as long as the session lives.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The before hook runs once per save, after Fisher's own listeners' before hooks and before the
    ///         unit of work is taken, so a document or participant it enlists joins the same commit. The
    ///         after hook runs after the commit and outside the resilience pipeline (fisher#454), ahead of
    ///         Fisher's own after-commit work. An empty unit of work fires neither, and an enlisted session
    ///         fires only the before hook: both are the rules Fisher's own listeners follow.
    ///     </para>
    ///     <para>
    ///         ⚠️ <b>Refused on a session taken from a projection batch.</b> The daemon owns that commit and
    ///         never runs a session's save hooks, and session listeners deliberately do not fire for the
    ///         batch. The contract names that session as the one a store must refuse, because a hook
    ///         accepted and never fired is an outbox that silently never flushes.
    ///     </para>
    /// </remarks>
    void IDocumentSessionOperations.AddSaveListener(IDocumentSaveListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        // On the parent, for the same reason as a participant: the scope's saves are the parent's.
        if (_parent is not null)
        {
            ((IDocumentSessionOperations)_parent).AddSaveListener(listener);
            return;
        }

        if (ParticipantsOwnedByBatch)
        {
            throw new NotSupportedException(
                "This session belongs to an async daemon projection batch, which commits the batch itself and "
                + "never runs a session's save hooks, so an IDocumentSaveListener added here would never fire. "
                + "Use the batch's message outbox (IMessageOutbox) or a subscription's IDaemonChangeListener for "
                + "work that follows a daemon commit.");
        }

        EnsureSharedHooks().AddListener(listener);
    }

    private static readonly ConcurrentDictionary<Type, Action<FisherSession, object>> StoreByRuntimeType = new();

    /// <summary>
    ///     Store documents of mixed types, each as its own runtime type.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Each document goes through <see cref="Store{T}(T)" /> closed over its runtime type, so a
    ///         sub-class in a registered hierarchy lands in its base's table with its own discriminator,
    ///         exactly as <c>Store(dog)</c> would. Null elements are skipped, as on Marten and Polecat; a
    ///         null collection is an <see cref="ArgumentNullException" />.
    ///     </para>
    ///     <para>
    ///         The closed delegate is cached per runtime type, so the reflection is paid once per type per
    ///         process rather than once per document.
    ///     </para>
    /// </remarks>
    void IDocumentWriteOperations.StoreObjects(IEnumerable<object> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        foreach (var document in documents)
        {
            if (document is null)
            {
                continue;
            }

            StoreByRuntimeType.GetOrAdd(document.GetType(), BuildStoreDelegate)(this, document);
        }
    }

    private static readonly MethodInfo StoreAsObjectMethod = typeof(FisherSession)
        .GetMethod(nameof(StoreAsObject), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    ///     Close <see cref="StoreAsObject{T}" /> over a runtime document type.
    /// </summary>
    /// <remarks>
    ///     The document type is only known as a value here, so there is no generic argument to carry it.
    ///     Under Native AOT a reference-type document is served by the shared canonical instantiation, the
    ///     same reason <c>FisherEventType.CompileWrapper</c>'s closing works natively; a value-type document
    ///     is not covered, and <c>Store&lt;T&gt;</c> with a static type is the AOT-safe spelling. The
    ///     store's own document mappings already root the types it can store.
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2060:MakeGenericMethod",
        Justification = "Closes the private StoreAsObject<T> over a document's runtime type; the document instance exists, so its type is rooted.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Reference-type documents use the shared canonical instantiation under Native AOT; Store<T> with a static type is the AOT-safe alternative and is documented as such.")]
    private static Action<FisherSession, object> BuildStoreDelegate(Type documentType)
        => StoreAsObjectMethod.MakeGenericMethod(documentType)
            .CreateDelegate<Action<FisherSession, object>>();

    private static void StoreAsObject<T>(FisherSession session, object document) where T : notnull
        => session.Store((T)document);
}
