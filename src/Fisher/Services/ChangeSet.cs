using JasperFx.Events;
using Weasel.Core;

namespace Fisher.Services;

/// <summary>
///     The <see cref="IChangeSet" /> a committed unit of work reports, built once from the operations
///     the session took and the streams it appended.
/// </summary>
/// <remarks>
///     <para>
///         Classified eagerly in the constructor rather than lazily off the operation list, so the
///         change set holds no reference to anything the session goes on to mutate. That is what makes
///         <see cref="Clone" /> able to return <c>this</c>.
///     </para>
///     <para>
///         The operations it reads are the snapshot <c>TakePendingOperations</c> produced, which is the
///         same snapshot the transaction wrote from. Re-reading the session's queue after the commit
///         would report a unit of work that had already been drained — and, under a retried
///         <c>SQLITE_BUSY</c>, one that had been drained twice.
///     </para>
/// </remarks>
internal sealed class ChangeSet : IChangeSet
{
    private readonly List<object> _updated = [];
    private readonly List<object> _inserted = [];
    private readonly List<IDocumentDeletion> _deleted = [];
    private readonly IReadOnlyList<StreamAction> _streams;

    internal ChangeSet(IReadOnlyList<Weasel.Storage.IStorageOperation> operations,
        IReadOnlyList<StreamAction> streams)
    {
        _streams = streams;

        foreach (var operation in operations)
        {
            Classify(operation);
        }
    }

    private void Classify(Weasel.Storage.IStorageOperation operation)
    {
        // A by-id deletion carries its identity, and Weasel's IDeletion is what exposes it. Tested
        // before the role rather than inside the switch: every deletion has OperationRole.Deletion,
        // including the soft form, whose statement is an UPDATE — so a role-first switch would report
        // by-id deletions through the predicate branch and lose every identity.
        if (operation is Weasel.Storage.IDeletion deletion)
        {
            _deleted.Add(new DocumentDeletionRecord(operation.DocumentType!, deletion.Id));
            return;
        }

        switch (operation.Role())
        {
            case OperationRole.Insert when operation is Weasel.Storage.IDocumentStorageOperation inserted:
                _inserted.Add(inserted.Document);
                break;

            case OperationRole.Update or OperationRole.Upsert
                when operation is Weasel.Storage.IDocumentStorageOperation updated:
                _updated.Add(updated.Document);
                break;

            // A predicate-based delete: no document was loaded and no id was named, so the type is all
            // there is to report. Reporting it with a null id beats leaving DeleteWhere invisible to a
            // listener that is watching for deletions of that type.
            case OperationRole.Deletion when operation.DocumentType is not null:
                _deleted.Add(new DocumentDeletionRecord(operation.DocumentType, null));
                break;
        }
    }

    public IEnumerable<object> Updated => _updated;

    public IEnumerable<object> Inserted => _inserted;

    public IEnumerable<IDocumentDeletion> Deleted => _deleted;

    public IEnumerable<IEvent> GetEvents() => _streams.SelectMany(x => x.Events);

    public IEnumerable<StreamAction> GetStreams() => _streams;

    /// <inheritdoc />
    public IChangeSet Clone() => this;

    private sealed record DocumentDeletionRecord(Type DocumentType, object? Id) : IDocumentDeletion;
}
