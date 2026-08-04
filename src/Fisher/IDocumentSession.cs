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
    ///     Load a document by its identity, or null when there is none.
    /// </summary>
    Task<T?> LoadAsync<T>(Guid id, CancellationToken token = default) where T : class;

    /// <inheritdoc cref="LoadAsync{T}(Guid,CancellationToken)" />
    Task<T?> LoadAsync<T>(string id, CancellationToken token = default) where T : class;

    /// <summary>
    ///     Load several documents by identity. Missing ids are absent from the result rather than
    ///     null entries, so it is not necessarily as long as the input.
    /// </summary>
    Task<IReadOnlyList<T>> LoadManyAsync<T>(params Guid[] ids) where T : class;

    /// <inheritdoc cref="LoadManyAsync{T}(Guid[])" />
    Task<IReadOnlyList<T>> LoadManyAsync<T>(params string[] ids) where T : class;
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
    ///     Queue a document for deletion.
    /// </summary>
    void Delete<T>(T document) where T : notnull;

    /// <summary>
    ///     Queue the document with this identity for deletion, whether or not it has been loaded.
    /// </summary>
    void Delete<T>(Guid id) where T : notnull;

    /// <inheritdoc cref="Delete{T}(Guid)" />
    void Delete<T>(string id) where T : notnull;

    /// <summary>
    ///     Commit every queued operation in a single transaction.
    /// </summary>
    Task SaveChangesAsync(CancellationToken token = default);
}
