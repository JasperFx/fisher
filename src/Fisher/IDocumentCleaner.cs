namespace Fisher;

/// <summary>
///     Per-store clean/reset surface, reached through <see cref="AdvancedOperations.Clean" /> and
///     paralleling Marten's <c>Marten.Schema.IDocumentCleaner</c> and Polecat's own.
/// </summary>
/// <remarks>
///     Every operation is scoped to this store's table prefix, which is what
///     <see cref="StoreOptions.DatabaseSchemaName" /> folds into. Two logical stores sharing one
///     SQLite file therefore clean only their own tables — the same isolation a real schema would
///     give them in Marten or Polecat.
/// </remarks>
public interface IDocumentCleaner
{
    /// <summary>
    ///     Delete every row from every document table (<c>fi_doc_*</c>) belonging to this store. The
    ///     tables themselves are kept.
    /// </summary>
    Task DeleteAllDocumentsAsync(CancellationToken token = default);

    /// <summary>
    ///     Delete every row of one document type's table, keeping the table.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A real <c>delete from</c> even for a soft-deleted type. A clean removes rows; flagging
    ///         them would leave a "cleaned" table that still answers <c>MaybeDeleted()</c> queries and
    ///         still refuses an insert on a duplicate id. Same rule
    ///         <c>TruncateDocumentStorageAsync</c> follows for a rebuild's teardown.
    ///     </para>
    ///     <para>
    ///         Silently does nothing when the type has no table yet — document tables are created on
    ///         demand at first write, so "never written to" and "already empty" are the same state and
    ///         should not be told apart by whether this throws.
    ///     </para>
    /// </remarks>
    Task CleanAsync(Type documentType, CancellationToken token = default);

    /// <inheritdoc cref="CleanAsync(Type,CancellationToken)" />
    Task CleanAsync<T>(CancellationToken token = default) where T : notnull;

    /// <summary>
    ///     Delete all event, stream and progression data belonging to this store. The tables
    ///     themselves are kept.
    /// </summary>
    Task DeleteAllEventDataAsync(CancellationToken token = default);

    /// <summary>
    ///     Drop every table belonging to this store, document and event alike — unlike the delete
    ///     operations, this removes the schema objects as well as the rows.
    /// </summary>
    Task CompletelyRemoveAllAsync(CancellationToken token = default);
}
