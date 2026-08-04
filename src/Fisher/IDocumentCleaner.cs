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
