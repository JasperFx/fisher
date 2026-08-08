using System.Linq.Expressions;
using Fisher.Events;

namespace Fisher;

/// <summary>
///     A read-only Fisher session.
/// </summary>
public interface IQuerySession : IAsyncDisposable, IDisposable
{
    /// <summary>
    ///     The tenant every operation in this session is scoped to.
    /// </summary>
    string TenantId { get; }

    /// <summary>
    ///     Start a LINQ query over a document type.
    /// </summary>
    /// <remarks>
    ///     Terminal operators are the async ones in <see cref="Linq.QueryableExtensions" /> —
    ///     <c>ToListAsync</c> and friends. Synchronous enumeration throws rather than blocking on the
    ///     async path; see <see cref="Linq.FisherQueryable{T}.GetEnumerator" />.
    /// </remarks>
    IQueryable<T> Query<T>() where T : notnull;

    /// <summary>
    ///     Load a document by its identity, or null when there is none.
    /// </summary>
    Task<T?> LoadAsync<T>(Guid id, CancellationToken token = default) where T : class;

    /// <inheritdoc cref="LoadAsync{T}(Guid,CancellationToken)" />
    Task<T?> LoadAsync<T>(string id, CancellationToken token = default) where T : class;

    /// <inheritdoc cref="LoadAsync{T}(Guid,CancellationToken)" />
    Task<T?> LoadAsync<T>(int id, CancellationToken token = default) where T : class;

    /// <inheritdoc cref="LoadAsync{T}(Guid,CancellationToken)" />
    Task<T?> LoadAsync<T>(long id, CancellationToken token = default) where T : class;

    /// <summary>
    ///     Load a document by a strong-typed identity, or null when there is none.
    /// </summary>
    /// <remarks>
    ///     Both type parameters are explicit, which is what keeps this from being ambiguous with the
    ///     four single-parameter overloads above. Use it for a document whose identity is a wrapper —
    ///     <c>LoadAsync&lt;Payment, PaymentId&gt;(id)</c>. The four canonical types are reachable
    ///     through here too, and reach exactly the same code.
    /// </remarks>
    Task<T?> LoadAsync<T, TId>(TId id, CancellationToken token = default)
        where T : class where TId : notnull;

    /// <summary>
    ///     Run your own SQL through this session and get typed results back.
    /// </summary>
    /// <remarks>
    ///     On the session's own connection, so it sees the session's uncommitted writes and joins an
    ///     open transaction. The write counterpart is
    ///     <see cref="IDocumentSession.QueueSqlCommand(string,object?[])" />.
    /// </remarks>
    IAdvancedSql AdvancedSql { get; }

    /// <summary>
    ///     Load several documents by identity. Missing ids are absent from the result rather than
    ///     null entries, so it is not necessarily as long as the input.
    /// </summary>
    Task<IReadOnlyList<T>> LoadManyAsync<T>(params Guid[] ids) where T : class;

    /// <inheritdoc cref="LoadManyAsync{T}(Guid[])" />
    Task<IReadOnlyList<T>> LoadManyAsync<T>(params string[] ids) where T : class;

    /// <inheritdoc cref="LoadManyAsync{T}(Guid[])" />
    Task<IReadOnlyList<T>> LoadManyAsync<T>(params int[] ids) where T : class;

    /// <inheritdoc cref="LoadManyAsync{T}(Guid[])" />
    Task<IReadOnlyList<T>> LoadManyAsync<T>(params long[] ids) where T : class;
}

/// <summary>
///     A writable Fisher session: a unit of work over the event store, flushed by
///     <see cref="SaveChangesAsync" />.
/// </summary>
/// <remarks>
///     The <see cref="JasperFx.Events.IStorageOperations" /> half is what lets Fisher's session types
///     close JasperFx's aggregation and projection generics, which constrain the write session to be
///     both the read session and a storage-operations surface. Its members are the projection write
///     path — see <c>Fisher.Internal.FisherSession</c> for which of them are live today.
/// </remarks>
public interface IDocumentSession : IQuerySession, JasperFx.Events.IStorageOperations
{
    /// <summary>
    ///     The event store write surface for this session.
    /// </summary>
    EventOperations Events { get; }

    /// <summary>
    ///     Queue a document to be written on the next <see cref="SaveChangesAsync" />, inserting or
    ///     updating as needed.
    /// </summary>
    void Store<T>(T document) where T : notnull;

    /// <summary>
    ///     Queue a document to be written, requiring the stored row's revision to be below
    ///     <paramref name="revision" />.
    /// </summary>
    /// <remarks>
    ///     For a document type configured for numeric revisions — by implementing
    ///     <see cref="JasperFx.IRevisioned" /> or through <c>Schema.For&lt;T&gt;().UseNumericRevisions()</c>.
    ///     A miss fails the whole unit of work with a <c>ConcurrencyException</c>; use
    ///     <see cref="TryUpdateRevision{T}" /> to drop the stale write instead.
    /// </remarks>
    void Store<T>(T document, int revision) where T : notnull;

    /// <inheritdoc cref="Store{T}(T,int)" />
    void UpdateRevision<T>(T document, int revision) where T : notnull;

    /// <summary>
    ///     Queue a document to be written at an explicit revision, dropping the write rather than
    ///     failing the unit of work if the stored row has already moved past it.
    /// </summary>
    void TryUpdateRevision<T>(T document, int revision) where T : notnull;

    /// <inheritdoc cref="Store{T}(T)" />
    void Store<T>(params T[] documents) where T : notnull;

    /// <summary>
    ///     Queue a document to be inserted, failing at commit if one with that identity already exists.
    /// </summary>
    void Insert<T>(T document) where T : notnull;

    /// <summary>
    ///     Queue a document to be updated, failing at commit if no document with that identity exists.
    /// </summary>
    void Update<T>(T document) where T : notnull;

    /// <summary>
    ///     Queue a document for deletion. A soft-deleted type is flagged rather than removed.
    /// </summary>
    void Delete<T>(T document) where T : notnull;

    /// <summary>
    ///     Queue the document with this identity for deletion, whether or not it has been loaded.
    /// </summary>
    void Delete<T>(Guid id) where T : notnull;

    /// <inheritdoc cref="Delete{T}(Guid)" />
    void Delete<T>(string id) where T : notnull;

    /// <inheritdoc cref="Delete{T}(Guid)" />
    void Delete<T>(int id) where T : notnull;

    /// <inheritdoc cref="Delete{T}(Guid)" />
    void Delete<T>(long id) where T : notnull;

    /// <summary>
    ///     Queue a document to be removed outright, even if its type is soft-deleted. Identical to
    ///     <see cref="Delete{T}(T)" /> for every other type.
    /// </summary>
    void HardDelete<T>(T document) where T : notnull;

    /// <inheritdoc cref="HardDelete{T}(T)" />
    void HardDelete<T>(Guid id) where T : notnull;

    /// <inheritdoc cref="HardDelete{T}(T)" />
    void HardDelete<T>(string id) where T : notnull;

    /// <inheritdoc cref="HardDelete{T}(T)" />
    void HardDelete<T>(int id) where T : notnull;

    /// <inheritdoc cref="HardDelete{T}(T)" />
    void HardDelete<T>(long id) where T : notnull;

    /// <summary>
    ///     Queue every document matching the predicate for deletion, without loading any of them. A
    ///     soft-deleted type is flagged rather than removed.
    /// </summary>
    /// <remarks>
    ///     The predicate is translated by the same LINQ layer <see cref="IQuerySession.Query{T}" />
    ///     uses, so it supports what that supports and refuses the rest by name.
    /// </remarks>
    void DeleteWhere<T>(Expression<Func<T, bool>> predicate) where T : notnull;

    /// <summary>
    ///     Queue every document matching the predicate to be removed outright, even if its type is
    ///     soft-deleted.
    /// </summary>
    void HardDeleteWhere<T>(Expression<Func<T, bool>> predicate) where T : notnull;

    /// <summary>
    ///     Bring every soft-deleted document matching the predicate back. Throws for a type that is not
    ///     soft-deleted, where there is nothing to bring back.
    /// </summary>
    void UndoDeleteWhere<T>(Expression<Func<T, bool>> predicate) where T : notnull;

    /// <summary>
    ///     Queue arbitrary SQL to run in this unit of work's transaction, alongside the documents and
    ///     events.
    /// </summary>
    /// <param name="sql">
    ///     The statement, with <c>?</c> for each parameter. A trailing semicolon is trimmed.
    /// </param>
    /// <param name="parameterValues">
    ///     One value per <c>?</c>, in order. A count mismatch throws at commit, naming both counts.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         <b>This is worth more on SQLite than the same method is on Marten or Polecat.</b> An
    ///         application using Fisher keeps its own tables in the same file, and SQLite permits one
    ///         writer per file — so without this, writing your rows and Fisher's atomically is not
    ///         merely inconvenient, it means taking the write lock twice and contending with yourself.
    ///     </para>
    ///     <para>
    ///         Values are converted to the encodings Fisher stores before binding: a <see cref="Guid" />
    ///         to its lowercase canonical text, a <see cref="DateTimeOffset" /> or <see cref="DateTime" />
    ///         to Fisher's fixed-width UTC form, a <see cref="decimal" /> to REAL. Bound raw, each of the
    ///         three matches nothing Fisher has written — silently. Everything else binds unchanged.
    ///     </para>
    ///     <para>
    ///         Statements run in the order they were queued, interleaved with nothing — document and
    ///         event operations queued before and after keep their relative order too.
    ///     </para>
    /// </remarks>
    void QueueSqlCommand(string sql, params object?[] parameterValues);

    /// <summary>
    ///     <see cref="QueueSqlCommand(string,object?[])" /> with a placeholder character other than
    ///     <c>?</c>, for SQL that contains a literal one.
    /// </summary>
    /// <remarks>
    ///     The placeholder is found by splitting the text, so a <c>?</c> inside a string literal or a
    ///     JSON path would otherwise be read as a parameter and the counts would not add up. Polecat
    ///     offers the same escape on <c>IAdvancedSql</c> rather than here; Fisher offers it in both
    ///     places, because a JSON path is a much more likely thing to write against `fi_doc_*` than
    ///     against a relational table.
    /// </remarks>
    void QueueSqlCommand(char placeholder, string sql, params object?[] parameterValues);

    /// <summary>
    ///     Commit every queued operation in a single transaction.
    /// </summary>
    Task SaveChangesAsync(CancellationToken token = default);
}
