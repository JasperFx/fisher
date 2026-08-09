namespace Fisher;

/// <summary>
///     What <see cref="AdvancedOperations.BulkInsertAsync{T}" /> does about a document that is already
///     there (fisher#36).
/// </summary>
public enum BulkInsertMode
{
    /// <summary>
    ///     Insert every document, failing the batch if one already exists.
    /// </summary>
    InsertsOnly,

    /// <summary>
    ///     Insert or update, whichever the row calls for — the ordinary upsert.
    /// </summary>
    OverwriteExisting,

    /// <summary>
    ///     Insert what is new and leave what is already stored exactly as it is (fisher#53).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Implemented by filtering, not by <c>insert or ignore</c>.</b> Both siblings reach for
    ///         a statement — Marten <c>on conflict do nothing</c>, Polecat a temp table and a
    ///         <c>MERGE</c> — and Fisher cannot, because its four write statements are built by
    ///         <c>SqliteDocumentStorageDescriptorBuilder</c> and consumed by Weasel's shared
    ///         closed-shape operations <em>by name</em>. A fifth would need a slot on Weasel's own
    ///         <c>DocumentStorageDescriptor</c> and an operation to read it. So each batch reads which
    ///         of its ids are already stored and queues only the rest.
    ///     </para>
    ///     <para>
    ///         <b>The probe deliberately ignores the soft-delete and hierarchy filters an ordinary load
    ///         applies.</b> The question is not "can I read this document" but "would inserting this row
    ///         collide", and a soft-deleted row or one belonging to a sub-class still occupies the
    ///         primary key. It does scope by tenant, because a conjoined table keys on
    ///         <c>(tenant_id, id)</c> and the same id under another tenant is not a collision.
    ///     </para>
    ///     <para>
    ///         <b>The read is not inside the write transaction, and the window is not silent.</b> A
    ///         concurrent writer that inserts one of the same ids between the probe and the commit makes
    ///         the insert fail with its unique-constraint violation rather than being skipped — loud,
    ///         and the same answer <see cref="InsertsOnly" /> would have given. Closing the window would
    ///         mean holding <c>BEGIN IMMEDIATE</c> across the probe through an enlisted session, which
    ///         forfeits the <c>SQLITE_BUSY</c> retry — a worse trade for the operation most likely to
    ///         contend for the write lock.
    ///     </para>
    /// </remarks>
    IgnoreDuplicates
}
