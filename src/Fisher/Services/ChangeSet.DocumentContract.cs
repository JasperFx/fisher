using JasperFx.Events.Documents;

namespace Fisher.Services;

/// <summary>
///     The change set's half of the store-agnostic document contract (fisher#104 / jasperfx#679).
/// </summary>
/// <remarks>
///     <para>
///         Three members, all explicit, because all three collide by name with the
///         <see cref="IChangeSet" /> members beside them and differ from them by type:
///         <see cref="IDocumentChangeSet" /> is declared in <see cref="IReadOnlyList{T}" /> where
///         Fisher's own change set says <see cref="IEnumerable{T}" />. That is not a near-miss to
///         paper over — it is the contract insisting on a materialised snapshot, because on Marten
///         the change set <em>is</em> the live unit of work and a lazy sequence handed out of one is
///         wrong by the time a listener reads it again.
///     </para>
///     <para>
///         <b>Which makes these three the cheapest implementation of the contract of the three
///         stores, and for a reason that predates it.</b> Fisher already classifies eagerly into
///         three <see cref="List{T}" /> fields in its constructor, off the operations snapshot
///         <c>TakePendingOperations</c> produced — the property <see cref="IChangeSet.Clone" />
///         returns <c>this</c> on. So the snapshot the contract asks for already exists and each
///         member is a field read: no copy, no <c>ToList</c>, and nothing that could go stale.
///     </para>
///     <para>
///         <b><see cref="Deleted" /> costs nothing either, and that is the derivation doing the
///         work.</b> <c>_deleted</c> is a <c>List&lt;Fisher.Services.IDocumentDeletion&gt;</c>, and
///         Fisher's <see cref="IDocumentDeletion" /> derives from the contract's — so the field
///         converts to <c>IReadOnlyList&lt;JasperFx.Events.Documents.IDocumentDeletion&gt;</c> by
///         interface variance. Had the two same-named interfaces been left unrelated, this member
///         could only have been a per-call <c>Cast().ToList()</c>, which allocates on every read and
///         hands out a different list each time to a consumer told the collections are snapshots.
///     </para>
/// </remarks>
internal sealed partial class ChangeSet : IDocumentChangeSet
{
    IReadOnlyList<object> IDocumentChangeSet.Inserted => _inserted;

    IReadOnlyList<object> IDocumentChangeSet.Updated => _updated;

    IReadOnlyList<JasperFx.Events.Documents.IDocumentDeletion> IDocumentChangeSet.Deleted => _deleted;
}
