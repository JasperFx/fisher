using JasperFx.Events;

namespace Fisher.Services;

/// <summary>
///     What a unit of work committed: the documents written, the documents removed, and the events
///     appended (fisher#32).
/// </summary>
/// <remarks>
///     <para>
///         Handed to <see cref="IDocumentSessionListener.AfterCommitAsync" /> so a post-commit side
///         effect — cache invalidation, an outbound message, an audit line — can see exactly what
///         changed rather than having to work it out. Mirrors Marten's
///         <c>Marten.Services.IChangeSet</c> and Polecat's, member for member, so a listener written
///         for one store compiles against the others.
///     </para>
///     <para>
///         <b>It describes a user session's unit of work only.</b> An async projection batch commits
///         through a different path and deliberately does not fire session listeners — see
///         <see cref="IDocumentSessionListener" /> for why.
///     </para>
/// </remarks>
public interface IChangeSet
{
    /// <summary>
    ///     Documents written by <c>Store</c> or <c>Update</c> — every upsert and update in the unit of
    ///     work.
    /// </summary>
    /// <remarks>
    ///     <c>Store</c> is an upsert, so a document that did not previously exist appears here rather
    ///     than in <see cref="Inserted" />. Only <c>Insert</c> means "insert".
    /// </remarks>
    IEnumerable<object> Updated { get; }

    /// <summary>
    ///     Documents written by <c>Insert</c>.
    /// </summary>
    IEnumerable<object> Inserted { get; }

    /// <summary>
    ///     Deletions, whether by document, by id, or by predicate.
    /// </summary>
    IEnumerable<IDocumentDeletion> Deleted { get; }

    /// <summary>
    ///     Every event appended across every stream in this unit of work.
    /// </summary>
    IEnumerable<IEvent> GetEvents();

    /// <summary>
    ///     Every stream started or appended to in this unit of work.
    /// </summary>
    IEnumerable<StreamAction> GetStreams();

    /// <summary>
    ///     An immutable copy, for a listener that keeps the change set past the commit boundary.
    /// </summary>
    /// <remarks>
    ///     <b>Fisher's returns itself, and that is not a shortcut.</b> On Marten the change set
    ///     <em>is</em> the live unit of work, which is reset after every commit, so a listener that
    ///     retained one without cloning would watch it empty out. Fisher builds the change set from the
    ///     operations snapshot <c>TakePendingOperations</c> has already taken — the same snapshot the
    ///     transaction wrote from, for the same reason (fisher#12) — so it is immutable by
    ///     construction and there is nothing to copy. The member is carried so a listener that clones
    ///     out of habit still compiles.
    /// </remarks>
    IChangeSet Clone();
}

/// <summary>
///     One deletion inside an <see cref="IChangeSet" />.
/// </summary>
/// <remarks>
///     <b>Named <c>IDocumentDeletion</c> where Marten and Polecat both say <c>IDeletion</c></b>, for a
///     reason local to this codebase: <c>Weasel.Storage.IDeletion</c> is already here, is the storage
///     <em>operation</em> that performs a delete, and is referenced unqualified in the very file that
///     builds one. Two same-named types one namespace apart is a collision only whoever imports the
///     wrong one ever notices — the same lesson <c>StoredDocumentMetadata</c> records. The members are
///     unchanged, so a listener body reading <c>DocumentType</c> and <c>Id</c> ports across untouched;
///     only a declaration that names the type has to be edited.
///     <para>
///         <b>It now derives from <c>JasperFx.Events.Documents.IDocumentDeletion</c></b>, which
///         jasperfx#679 added with this type's exact members and — deliberately — this type's exact
///         name, Fisher's spelling having been picked as the shared one precisely because
///         <c>IDeletion</c> was already taken next to <c>Weasel.Storage</c>. Two structurally
///         identical interfaces one namespace apart is a clash to resolve rather than a coincidence
///         to live with, and derivation resolves it in the direction that costs nothing: no member
///         is re-declared, no implementer changes, and a listener written against the shared
///         contract sees Fisher's descriptors without an adapter.
///     </para>
/// </remarks>
public interface IDocumentDeletion : JasperFx.Events.Documents.IDocumentDeletion
{
    // Both members - Type DocumentType and object? Id - are inherited from the JasperFx contract
    // rather than declared here, and the derivation is what resolves the name clash jasperfx#679
    // created. See the remarks above: re-declaring them would hide the base pair behind an identical
    // pair, which is the exact near-miss shape the contract's own remarks warn about, and it would
    // cost every implementer an explicit implementation for no gain. Inheriting instead means every
    // Fisher deletion descriptor IS a contract deletion, so ChangeSet's List<IDocumentDeletion>
    // satisfies IReadOnlyList<JasperFx.Events.Documents.IDocumentDeletion> by variance with no copy
    // and no cast.
}
