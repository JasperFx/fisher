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

        QueueOperation(CaptureExpectedRevision(storage.Upsert(document, this, TenantId), document));
    }

    /// <summary>
    ///     Copy the revision the document carries onto the operation that is about to write it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Under <c>ConcurrencyMode.Numeric</c> the revision is an <em>expectation</em>, and it
    ///         travels on the document rather than as a parameter — which is why
    ///         <see cref="Store{T}(T,int)" /> is just "set the member, then store". Zero means auto, so
    ///         a document that carries no revision (a new one, or a type that does not implement
    ///         <see cref="JasperFx.IRevisioned" />) is written unguarded.
    ///     </para>
    ///     <para>
    ///         A no-op for every other concurrency mode: only the numeric operations implement
    ///         <see cref="Weasel.Storage.IRevisionedOperation" />.
    ///     </para>
    /// </remarks>
    private static Weasel.Storage.IStorageOperation CaptureExpectedRevision(
        Weasel.Storage.IStorageOperation operation, object document)
    {
        if (operation is Weasel.Storage.IRevisionedOperation revisioned)
        {
            revisioned.Revision = document is JasperFx.IRevisioned carried ? carried.Version : 0;
        }

        return operation;
    }

    /// <summary>
    ///     Store a document and require the stored row's revision to be below
    ///     <paramref name="revision" /> — the readable alternative to a Guid version guard.
    /// </summary>
    /// <remarks>
    ///     Fails the unit of work with a <c>ConcurrencyException</c> when the row has already moved to
    ///     that revision or beyond. The document type has to be configured for numeric revisions, by
    ///     implementing <see cref="JasperFx.IRevisioned" /> or through
    ///     <c>Schema.For&lt;T&gt;().UseNumericRevisions()</c>; on any other type the revision is
    ///     recorded on the document and then ignored, because there is no column to guard against.
    /// </remarks>
    public void Store<T>(T document, int revision) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document is JasperFx.IRevisioned revisioned)
        {
            revisioned.Version = revision;
        }

        Store(document);
    }

    /// <summary>
    ///     Store a document at an explicit revision. Marten spells the same operation this way.
    /// </summary>
    /// <inheritdoc cref="Store{T}(T,int)" path="/remarks" />
    public void UpdateRevision<T>(T document, int revision) where T : notnull
        => Store(document, revision);

    /// <summary>
    ///     Store a document at an explicit revision, ignoring a revision miss rather than failing the
    ///     unit of work.
    /// </summary>
    /// <remarks>
    ///     The "last writer loses quietly" variant: a stale write is dropped and everything else in the
    ///     unit of work still commits. Marten's <c>TryUpdateRevision</c>.
    /// </remarks>
    public void TryUpdateRevision<T>(T document, int revision) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document is JasperFx.IRevisioned revisioned)
        {
            revisioned.Version = revision;
        }

        var storage = StorageFor<T>();
        storage.Store(this, document);

        var operation = CaptureExpectedRevision(storage.Upsert(document, this, TenantId), document);

        if (operation is Weasel.Storage.IRevisionedOperation revisionedOperation)
        {
            revisionedOperation.IgnoreConcurrencyViolation = true;
        }

        QueueOperation(operation);
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

        QueueOperation(CaptureExpectedRevision(storage.Insert(document, this, TenantId), document));
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

        QueueOperation(CaptureExpectedRevision(storage.Update(document, this, TenantId), document));
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

        // Both, and they are not the same thing: the map decides what a later load hands back, the
        // tracker decides what the next commit writes. A tracker left behind would detect the deleted
        // document as unchanged, which is harmless, or as changed if the caller mutates it — and then
        // resurrect the row the caller just deleted.
        storage.RemoveDirtyTracker(this, id);
    }

    // ---- ejection (fisher#31) ----

    /// <summary>
    ///     Remove a document from this session entirely: out of the identity map, out of the change
    ///     trackers, and out of the queued writes it has not committed yet.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Worth having with or without an identity map, because of the third clause: without it
    ///         there is no way to unqueue one document's write once <c>Store</c> has been called. The
    ///         match is by <b>reference</b>, so ejecting one instance does not unqueue a different
    ///         instance of the same document — which is the whole distinction the identity map exists
    ///         to make.
    ///     </para>
    ///     <para>
    ///         An eject does not touch the database, so a document already committed stays committed.
    ///     </para>
    /// </remarks>
    public void Eject<T>(T document) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(document);

        StorageFor<T>().Eject(this, document);

        RemoveOperations(operation => operation is Weasel.Storage.IDocumentStorageOperation stored
                                      && ReferenceEquals(stored.Document, document));

        RemoveChangeTrackers(tracker => ReferenceEquals(tracker.Document, document));
    }

    /// <inheritdoc cref="Eject{T}" />
    /// <remarks>
    ///     <para>
    ///         The identity map is keyed by the type its storage was resolved for, which for a document
    ///         hierarchy is the <em>base</em> — every sub-class shares one table and one map. So this
    ///         cannot simply drop the entry under <paramref name="type" />: ejecting one sub-class has
    ///         to leave its siblings mapped. Entries are therefore tested individually against the type
    ///         wherever the map's own key is not exactly it, which handles a hierarchy without knowing
    ///         it is one.
    ///     </para>
    /// </remarks>
    public void EjectAllOfType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        foreach (var (mapped, raw) in ItemMap.ToArray())
        {
            if (mapped == type)
            {
                ItemMap.Remove(mapped);
                continue;
            }

            if (raw is not System.Collections.IDictionary entries)
            {
                continue;
            }

            foreach (var key in entries.Keys.Cast<object>().Where(x => type.IsInstanceOfType(entries[x])).ToArray())
            {
                entries.Remove(key);
            }
        }

        RemoveOperations(operation => operation is Weasel.Storage.IDocumentStorageOperation stored
                                      && type.IsInstanceOfType(stored.Document));

        RemoveChangeTrackers(tracker => type.IsInstanceOfType(tracker.Document));
    }

    /// <summary>
    ///     Drop every queued operation — document writes, deletions, raw SQL, appended events and the
    ///     DCB boundaries guarding them — without touching the identity map.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The identity map deliberately survives, as it does on Marten: this abandons a unit of
    ///         work, not the session's knowledge of what it has read.
    ///     </para>
    ///     <para>
    ///         <b>The change trackers do not survive</b>, which looks like an exception to that and is
    ///         not: a tracker is a queued write that has not been asked for yet. Leaving them would make
    ///         "drop every pending change" write the loaded documents anyway at the next commit.
    ///     </para>
    ///     <para>
    ///         Pending DCB boundaries go too. A boundary asserts that no matching event has landed since
    ///         it was read, and it is only meaningful as a guard on the appends being dropped here —
    ///         keeping it would fail a later commit on behalf of a unit of work that no longer exists.
    ///     </para>
    /// </remarks>
    public void EjectAllPendingChanges()
    {
        TakePendingOperations();
        Events.ClearPendingStreams();
        ClearBoundaries();
        _changeTrackers?.Clear();
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
    public Task<T?> LoadAsync<T>(Guid id, CancellationToken token = default) where T : notnull
        => LoadByIdAsync<T, Guid>(id, token);

    /// <inheritdoc cref="LoadAsync{T}(Guid,CancellationToken)" />
    public Task<T?> LoadAsync<T>(string id, CancellationToken token = default) where T : notnull
        => LoadByIdAsync<T, string>(id, token);

    /// <inheritdoc cref="LoadAsync{T}(Guid,CancellationToken)" />
    public Task<T?> LoadAsync<T>(int id, CancellationToken token = default) where T : notnull
        => LoadByIdAsync<T, int>(id, token);

    /// <inheritdoc cref="LoadAsync{T}(Guid,CancellationToken)" />
    public Task<T?> LoadAsync<T>(long id, CancellationToken token = default) where T : notnull
        => LoadByIdAsync<T, long>(id, token);

    /// <inheritdoc cref="IDocumentSession.LoadAsync{T,TId}" />
    public Task<T?> LoadAsync<T, TId>(TId id, CancellationToken token = default)
        where T : notnull where TId : notnull
        => LoadByIdAsync<T, TId>(id, token);

    private Task<T?> LoadByIdAsync<T, TId>(TId id, CancellationToken token)
        where T : notnull where TId : notnull
        => ((Weasel.Storage.IDocumentStorage<T, TId>)StorageFor<T>()).LoadAsync(id, this, token);

    /// <summary>
    ///     Load several documents by identity. Missing ids are simply absent from the result, which is
    ///     therefore not necessarily as long as the input.
    /// </summary>
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(params Guid[] ids) where T : notnull
        => LoadManyByIdAsync<T, Guid>(ids, CancellationToken.None);

    /// <inheritdoc cref="LoadManyAsync{T}(Guid[])" />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(params string[] ids) where T : notnull
        => LoadManyByIdAsync<T, string>(ids, CancellationToken.None);

    /// <inheritdoc cref="LoadManyAsync{T}(Guid[])" />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(params int[] ids) where T : notnull
        => LoadManyByIdAsync<T, int>(ids, CancellationToken.None);

    /// <inheritdoc cref="LoadManyAsync{T}(Guid[])" />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(params long[] ids) where T : notnull
        => LoadManyByIdAsync<T, long>(ids, CancellationToken.None);

    /// <inheritdoc cref="LoadManyAsync{T}(Guid[])" />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(CancellationToken token, params Guid[] ids) where T : notnull
        => LoadManyByIdAsync<T, Guid>(ids, token);

    /// <inheritdoc cref="LoadManyAsync{T}(Guid[])" />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(CancellationToken token, params string[] ids) where T : notnull
        => LoadManyByIdAsync<T, string>(ids, token);

    /// <inheritdoc cref="LoadManyAsync{T}(Guid[])" />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(CancellationToken token, params int[] ids) where T : notnull
        => LoadManyByIdAsync<T, int>(ids, token);

    /// <inheritdoc cref="LoadManyAsync{T}(Guid[])" />
    public Task<IReadOnlyList<T>> LoadManyAsync<T>(CancellationToken token, params long[] ids) where T : notnull
        => LoadManyByIdAsync<T, long>(ids, token);

    /// <inheritdoc cref="IDocumentSession.LoadManyAsync{T,TId}" />
    public Task<IReadOnlyList<T>> LoadManyAsync<T, TId>(TId[] ids, CancellationToken token = default)
        where T : notnull where TId : notnull
        => LoadManyByIdAsync<T, TId>(ids, token);

    private Task<IReadOnlyList<T>> LoadManyByIdAsync<T, TId>(TId[] ids, CancellationToken token)
        where T : notnull where TId : notnull
        => ((Weasel.Storage.IDocumentStorage<T, TId>)StorageFor<T>()).LoadManyAsync(ids, this, token);

    // ---- existence, plans and diagnostics (fisher#37) ----

    public Task<bool> CheckExistsAsync<T>(Guid id, CancellationToken token = default) where T : notnull
        => CheckExistsAsync<T, Guid>(id, token);

    public Task<bool> CheckExistsAsync<T>(string id, CancellationToken token = default) where T : notnull
        => CheckExistsAsync<T, string>(id, token);

    public Task<bool> CheckExistsAsync<T>(int id, CancellationToken token = default) where T : notnull
        => CheckExistsAsync<T, int>(id, token);

    public Task<bool> CheckExistsAsync<T>(long id, CancellationToken token = default) where T : notnull
        => CheckExistsAsync<T, long>(id, token);

    /// <remarks>
    ///     Routed through the same LINQ path <c>Query&lt;T&gt;()</c> uses rather than through a
    ///     hand-written <c>select 1 from … where id = ?</c>. That is what makes it carry the tenant
    ///     filter, the soft-delete filter and a hierarchy discriminator without any of them being
    ///     restated here — the three implicit filters fisher#51 established have to be applied in one
    ///     place, and this is a fourth caller that would otherwise have to remember all of them.
    /// </remarks>
    private Task<bool> CheckExistsAsync<T, TId>(TId id, CancellationToken token)
        where T : notnull where TId : notnull
        => Linq.QueryableExtensions.AnyAsync(Query<T>().Where(ByIdPredicate<T, TId>(id)), token);

    /// <summary>
    ///     <c>x =&gt; x.Id == id</c>, built for whichever member the mapping calls the identity.
    /// </summary>
    private System.Linq.Expressions.Expression<Func<T, bool>> ByIdPredicate<T, TId>(TId id)
        where T : notnull where TId : notnull
    {
        var mapping = Options.Schema.MappingFor(typeof(T));
        var parameter = System.Linq.Expressions.Expression.Parameter(typeof(T), "x");
        var idMember = System.Linq.Expressions.Expression.PropertyOrField(parameter, mapping.IdMember.Name);

        return System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(
            System.Linq.Expressions.Expression.Equal(idMember,
                System.Linq.Expressions.Expression.Constant(id, idMember.Type)),
            parameter);
    }

    public Task<string?> LoadJsonAsync<T>(Guid id, CancellationToken token = default) where T : notnull
        => LoadJsonAsync<T, Guid>(id, token);

    public Task<string?> LoadJsonAsync<T>(string id, CancellationToken token = default) where T : notnull
        => LoadJsonAsync<T, string>(id, token);

    public Task<string?> LoadJsonAsync<T>(int id, CancellationToken token = default) where T : notnull
        => LoadJsonAsync<T, int>(id, token);

    public Task<string?> LoadJsonAsync<T>(long id, CancellationToken token = default) where T : notnull
        => LoadJsonAsync<T, long>(id, token);

    /// <remarks>
    ///     Through the LINQ path for the same reason <see cref="CheckExistsAsync{T,TId}" /> is: it
    ///     inherits the tenant, soft-delete and hierarchy filters rather than restating them.
    /// </remarks>
    private async Task<string?> LoadJsonAsync<T, TId>(TId id, CancellationToken token)
        where T : notnull where TId : notnull
    {
        var rows = await ((Linq.FisherQueryProvider)Query<T>().Provider)
            .JsonRowsAsync<T>(Query<T>().Where(ByIdPredicate<T, TId>(id)).Expression, "data", 1, token)
            .ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    // ---- patching (fisher#35) ----

    public Patching.IPatchExpression<T> Patch<T>(Guid id) where T : notnull => PatchById<T, Guid>(id);

    public Patching.IPatchExpression<T> Patch<T>(string id) where T : notnull => PatchById<T, string>(id);

    public Patching.IPatchExpression<T> Patch<T>(int id) where T : notnull => PatchById<T, int>(id);

    public Patching.IPatchExpression<T> Patch<T>(long id) where T : notnull => PatchById<T, long>(id);

    public Patching.IPatchExpression<T> Patch<T>(Expression<Func<T, bool>> predicate) where T : notnull
        => PatchWhere<T>(ParsePredicate(predicate));

    private Patching.IPatchExpression<T> PatchById<T, TId>(TId id)
        where T : notnull where TId : notnull
        => PatchWhere<T>(new Linq.SqlGeneration.ComparisonFilter("id",
            "=", Storage.SqliteStorageDialect<TId>.ToDatabaseValue(id)));

    private Patching.IPatchExpression<T> PatchWhere<T>(Weasel.Core.SqlGeneration.ISqlFragment where)
        where T : notnull
    {
        var mapping = Options.Schema.MappingFor(typeof(T));
        var operation = new Patching.PatchOperation(mapping, where,
            mapping.IsConjoined ? TenantId : null);

        QueueOperation(operation);

        return new Patching.PatchExpression<T>(
            new Linq.Members.MemberFactory(Options, mapping), FisherSerializer, operation);
    }

    public Task<T> QueryByPlanAsync<T>(Batching.IQueryPlan<T> plan, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return plan.Fetch(this, token);
    }

    public string ToSql<T>(IQueryable<T> queryable) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(queryable);

        return queryable.Provider is Linq.FisherQueryProvider provider
            ? provider.ToSql<T>(queryable.Expression)
            : throw new InvalidOperationException(
                "ToSql only works on a query created by this session's Query<T>().");
    }
}
