using System.Linq.Expressions;
using Fisher.Storage;
using Fisher.Storage.ClosedShape;
using JasperFx;
using Weasel.Core;
using Weasel.Core.SqlGeneration;

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
    ///     Queue a document for deletion. Whether that removes the row or flags it is the mapping's
    ///     <c>DeleteStyle</c>; the storage answers with the right operation either way.
    /// </summary>
    /// <remarks>
    ///     Ejected from the identity map in both cases: a soft-deleted document is invisible to every
    ///     read, so a session that kept handing back the instance it just deleted would be the only
    ///     place it still existed.
    /// </remarks>
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
    public void Delete<T>(Guid id) where T : notnull => DeleteById<T, Guid>(id, hard: false);

    /// <inheritdoc cref="Delete{T}(Guid)" />
    public void Delete<T>(string id) where T : notnull => DeleteById<T, string>(id, hard: false);

    /// <inheritdoc cref="Delete{T}(Guid)" />
    public void Delete<T>(int id) where T : notnull => DeleteById<T, int>(id, hard: false);

    /// <inheritdoc cref="Delete{T}(Guid)" />
    public void Delete<T>(long id) where T : notnull => DeleteById<T, long>(id, hard: false);

    /// <summary>
    ///     Queue a document to be removed outright, even if its type is soft-deleted.
    /// </summary>
    public void HardDelete<T>(T document) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(document);

        var storage = StorageFor<T>();
        QueueOperation(storage.HardDeleteForDocument(document, TenantId));
        storage.Eject(this, document);
    }

    /// <inheritdoc cref="HardDelete{T}(T)" />
    public void HardDelete<T>(Guid id) where T : notnull => DeleteById<T, Guid>(id, hard: true);

    /// <inheritdoc cref="HardDelete{T}(T)" />
    public void HardDelete<T>(string id) where T : notnull => DeleteById<T, string>(id, hard: true);

    /// <inheritdoc cref="HardDelete{T}(T)" />
    public void HardDelete<T>(int id) where T : notnull => DeleteById<T, int>(id, hard: true);

    /// <inheritdoc cref="HardDelete{T}(T)" />
    public void HardDelete<T>(long id) where T : notnull => DeleteById<T, long>(id, hard: true);

    private void DeleteById<T, TId>(TId id, bool hard) where T : notnull where TId : notnull
    {
        var storage = (Weasel.Storage.IDocumentStorage<T, TId>)StorageFor<T>();

        QueueOperation(hard ? storage.HardDeleteForId(id, TenantId) : storage.DeleteForId(id, TenantId));
        storage.EjectById(this, id);
    }

    /// <summary>
    ///     Queue every document matching the predicate for deletion, without loading any of them.
    /// </summary>
    public void DeleteWhere<T>(Expression<Func<T, bool>> predicate) where T : notnull
    {
        var storage = StorageFor<T>();
        var fisher = (IFisherDocumentStorage)storage;

        // A soft delete guards on is_deleted = 0 for the same reason the by-id form does: deleting an
        // already-deleted document must not move its deleted_at forward.
        QueueOperation(new DocumentWhereOperation(storage.DeleteFragment, typeof(T),
            ParsePredicate(predicate), TenantArgument(fisher), GuardFor(fisher, SoftDelete.NotDeletedSql),
            OperationRole.Deletion));
    }

    /// <inheritdoc cref="DeleteWhere{T}" />
    public void HardDeleteWhere<T>(Expression<Func<T, bool>> predicate) where T : notnull
    {
        var storage = StorageFor<T>();

        QueueOperation(new DocumentWhereOperation(storage.HardDeleteFragment, typeof(T),
            ParsePredicate(predicate), TenantArgument((IFisherDocumentStorage)storage), guard: null,
            OperationRole.Deletion));
    }

    /// <summary>
    ///     Bring every soft-deleted document matching the predicate back.
    /// </summary>
    public void UndoDeleteWhere<T>(Expression<Func<T, bool>> predicate) where T : notnull
    {
        var storage = StorageFor<T>();
        var fisher = (IFisherDocumentStorage)storage;

        if (fisher.DeleteStyle != DeleteStyle.SoftDelete)
        {
            throw new InvalidOperationException(
                $"'{typeof(T).Name}' is not configured for soft deletes, so a delete removed the row "
                + "and there is nothing to undo. Mark it with [SoftDeleted], implement ISoftDeleted, or "
                + "call StoreOptions.Schema.For<T>().SoftDeleted().");
        }

        // Guarded on the rows that are actually deleted, so an undelete never touches a live row's
        // last_modified or races a concurrent write to one.
        QueueOperation(new DocumentWhereOperation(fisher.UndeleteFragment, typeof(T),
            ParsePredicate(predicate), TenantArgument(fisher), SoftDelete.DeletedSql,
            OperationRole.Update));
    }

    /// <summary>
    ///     Translate a criteria-based operation's predicate through the same LINQ layer
    ///     <c>Query&lt;T&gt;()</c> uses, so the two agree about what a predicate means.
    /// </summary>
    private ISqlFragment ParsePredicate<T>(Expression<Func<T, bool>> predicate) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var mapping = Options.Schema.For<T>().Mapping;

        return new Linq.Parsing.WhereClauseParser(new Linq.Members.MemberFactory(Options, mapping))
            .Parse(predicate.Body);
    }

    /// <summary>The tenant to scope by, or null on a single-tenant table that has no column for it.</summary>
    private string? TenantArgument(IFisherDocumentStorage storage) => storage.IsConjoined ? TenantId : null;

    private static string? GuardFor(IFisherDocumentStorage storage, string guard)
        => storage.DeleteStyle == DeleteStyle.SoftDelete ? guard : null;

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
