using System.Linq.Expressions;
using Fisher.Events;

namespace Fisher;

/// <summary>
///     A read-only Fisher session.
/// </summary>
public interface IQuerySession : IAsyncDisposable
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
    ///     Commit every queued operation in a single transaction.
    /// </summary>
    Task SaveChangesAsync(CancellationToken token = default);
}
