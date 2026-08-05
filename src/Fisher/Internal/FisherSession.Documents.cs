namespace Fisher.Internal;

/// <summary>
///     The document half of the session: store, load, and delete.
/// </summary>
internal partial class FisherSession
{
    /// <summary>
    ///     Queue a document to be written on the next <c>SaveChangesAsync</c>, inserting or updating
    ///     as needed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The write is queued now but the document is not serialized until the batch runs, so
    ///         mutating it after this call and before the commit still takes effect. That matches
    ///         Marten, and it is why storing the same document twice is harmless rather than a
    ///         duplicate write.
    ///     </para>
    ///     <para>
    ///         The storage assigns an identity if the document has none; the flavors record the
    ///         document, and queueing the operation is this session's job because
    ///         <c>IStorageSession</c> has no queue of its own.
    ///     </para>
    /// </remarks>
    public void Store<T>(T document) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(document);

        var storage = StorageFor<T>();
        storage.Store(this, document);

        QueueOperation(storage.Upsert(document, this, TenantId));
    }

    private Linq.FisherQueryProvider? _queryProvider;

    /// <summary>
    ///     Start a LINQ query over a document type.
    /// </summary>
    /// <remarks>
    ///     The provider is cached per session because it holds the session, and the session's single
    ///     connection is what lets a query inside a unit of work see that unit of work's own writes.
    /// </remarks>
    public IQueryable<T> Query<T>() where T : notnull
        => new Linq.FisherQueryable<T>(_queryProvider ??= new Linq.FisherQueryProvider(this));

    /// <inheritdoc cref="Store{T}" />
    public void Store<T>(params T[] documents) where T : notnull
    {
        foreach (var document in documents)
        {
            Store(document);
        }
    }

    /// <summary>
    ///     Queue a document to be inserted, failing on the next <c>SaveChangesAsync</c> if a row with
    ///     the same identity already exists.
    /// </summary>
    public void Insert<T>(T document) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(document);

        var storage = StorageFor<T>();
        storage.Store(this, document);

        QueueOperation(storage.Insert(document, this, TenantId));
    }

    /// <summary>
    ///     Queue a document to be updated, failing on the next <c>SaveChangesAsync</c> if no row with
    ///     that identity exists.
    /// </summary>
    public void Update<T>(T document) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(document);

        var storage = StorageFor<T>();
        storage.Store(this, document);

        QueueOperation(storage.Update(document, this, TenantId));
    }

    /// <summary>
    ///     Queue a document for deletion.
    /// </summary>
    public void Delete<T>(T document) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(document);

        var storage = StorageFor<T>();
        QueueOperation(storage.DeleteForDocument(document, TenantId));
        storage.Eject(this, document);
    }

    /// <summary>
    ///     Queue the document with this identity for deletion, whether or not it is loaded.
    /// </summary>
    public void Delete<T>(Guid id) where T : notnull => DeleteById<T, Guid>(id);

    /// <inheritdoc cref="Delete{T}(Guid)" />
    public void Delete<T>(string id) where T : notnull => DeleteById<T, string>(id);

    /// <inheritdoc cref="Delete{T}(Guid)" />
    public void Delete<T>(int id) where T : notnull => DeleteById<T, int>(id);

    /// <inheritdoc cref="Delete{T}(Guid)" />
    public void Delete<T>(long id) where T : notnull => DeleteById<T, long>(id);

    private void DeleteById<T, TId>(TId id) where T : notnull where TId : notnull
    {
        var storage = (Weasel.Storage.IDocumentStorage<T, TId>)StorageFor<T>();

        QueueOperation(storage.DeleteForId(id, TenantId));
        storage.EjectById(this, id);
    }

    /// <summary>
    ///     Load a document by its identity, or null when there is none.
    /// </summary>
    public Task<T?> LoadAsync<T>(Guid id, CancellationToken token = default) where T : class
        => LoadByIdAsync<T, Guid>(id, token);

    /// <inheritdoc cref="LoadAsync{T}(Guid,CancellationToken)" />
    public Task<T?> LoadAsync<T>(string id, CancellationToken token = default) where T : class
        => LoadByIdAsync<T, string>(id, token);

    /// <inheritdoc cref="LoadAsync{T}(Guid,CancellationToken)" />
    public Task<T?> LoadAsync<T>(int id, CancellationToken token = default) where T : class
        => LoadByIdAsync<T, int>(id, token);

    /// <inheritdoc cref="LoadAsync{T}(Guid,CancellationToken)" />
    public Task<T?> LoadAsync<T>(long id, CancellationToken token = default) where T : class
        => LoadByIdAsync<T, long>(id, token);

    private Task<T?> LoadByIdAsync<T, TId>(TId id, CancellationToken token)
        where T : class where TId : notnull
        => ((Weasel.Storage.IDocumentStorage<T, TId>)StorageFor<T>()).LoadAsync(id, this, token);

    /// <summary>
    ///     Load several documents by identity. Missing ids are simply absent from the result, which is
    ///     therefore not necessarily as long as the input.
    /// </summary>
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(params Guid[] ids) where T : class
        => LoadManyByIdAsync<T, Guid>(ids, CancellationToken.None);

    /// <inheritdoc cref="LoadManyAsync{T}(Guid[])" />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(params string[] ids) where T : class
        => LoadManyByIdAsync<T, string>(ids, CancellationToken.None);

    /// <inheritdoc cref="LoadManyAsync{T}(Guid[])" />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(params int[] ids) where T : class
        => LoadManyByIdAsync<T, int>(ids, CancellationToken.None);

    /// <inheritdoc cref="LoadManyAsync{T}(Guid[])" />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(params long[] ids) where T : class
        => LoadManyByIdAsync<T, long>(ids, CancellationToken.None);

    /// <inheritdoc cref="LoadManyAsync{T}(Guid[])" />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(CancellationToken token, params Guid[] ids) where T : class
        => LoadManyByIdAsync<T, Guid>(ids, token);

    /// <inheritdoc cref="LoadManyAsync{T}(Guid[])" />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(CancellationToken token, params string[] ids) where T : class
        => LoadManyByIdAsync<T, string>(ids, token);

    /// <inheritdoc cref="LoadManyAsync{T}(Guid[])" />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(CancellationToken token, params int[] ids) where T : class
        => LoadManyByIdAsync<T, int>(ids, token);

    /// <inheritdoc cref="LoadManyAsync{T}(Guid[])" />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(CancellationToken token, params long[] ids) where T : class
        => LoadManyByIdAsync<T, long>(ids, token);

    private Task<IReadOnlyList<T>> LoadManyByIdAsync<T, TId>(TId[] ids, CancellationToken token)
        where T : class where TId : notnull
        => ((Weasel.Storage.IDocumentStorage<T, TId>)StorageFor<T>()).LoadManyAsync(ids, this, token);
}
