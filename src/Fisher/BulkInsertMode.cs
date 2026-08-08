namespace Fisher;

/// <summary>
///     What <see cref="AdvancedOperations.BulkInsertAsync{T}" /> does about a document that is already
///     there (fisher#36).
/// </summary>
/// <remarks>
///     <b>Polecat's third mode, <c>IgnoreDuplicates</c>, is deliberately absent rather than throwing.</b>
///     It needs <c>insert or ignore</c>, and the insert SQL is built by
///     <c>SqliteDocumentStorageDescriptorBuilder</c> and consumed by Weasel's shared closed-shape
///     operations — so adding it means a fourth statement in the descriptor and an operation to go with
///     it, not a flag. Shipping an enum value that throws would be worse than not shipping the value;
///     see <see href="https://github.com/JasperFx/fisher/issues/53">fisher#53</see>.
/// </remarks>
public enum BulkInsertMode
{
    /// <summary>
    ///     Insert every document, failing the batch if one already exists.
    /// </summary>
    InsertsOnly,

    /// <summary>
    ///     Insert or update, whichever the row calls for — the ordinary upsert.
    /// </summary>
    OverwriteExisting
}
