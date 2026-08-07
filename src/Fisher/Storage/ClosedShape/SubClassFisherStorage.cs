using System.Data.Common;
using JasperFx;
using JasperFx.MultiTenancy;
using Weasel.Core;
using Weasel.Core.SqlGeneration;
using Weasel.Storage;

namespace Fisher.Storage.ClosedShape;

/// <summary>
///     Storage for a registered sub-class in a document hierarchy — everything delegates to the base
///     type's storage, which owns the table, the descriptor and the hierarchical selectors, and the
///     read paths narrow to the sub-class (fisher#17).
/// </summary>
/// <remarks>
///     <para>
///         <b>Writes delegate untouched, and that is the whole reason this is a thin wrapper.</b>
///         Weasel's <c>DocumentDocTypeBinder</c> resolves the alias from <c>document.GetType()</c>, so
///         the base type's write operations already stamp a derived instance with its own alias. There
///         is nothing sub-class-specific to do on the way in.
///     </para>
///     <para>
///         Reads are where the sub-class matters, in two different ways that are easy to conflate:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Queries are narrowed in SQL</b>, through <see cref="DefaultWhereFragment" /> — so
///             <c>Query&lt;TDerived&gt;()</c> does not have to fetch and discard the rest of the table.
///         </item>
///         <item>
///             <b>Loads by id are narrowed in memory</b>, by testing what came back. A load names one
///             row; adding a discriminator predicate to it would turn "that id is a different
///             sub-class" into the same answer as "no such id", and the id is unique across the whole
///             hierarchy either way.
///         </item>
///     </list>
/// </remarks>
internal sealed class SubClassFisherStorage<TDoc, TBase, TId>
    : IDocumentStorage<TDoc, TId>, IFisherDocumentStorage
    where TDoc : notnull, TBase
    where TBase : notnull
    where TId : notnull
{
    private readonly IDocumentStorage<TBase, TId> _parent;
    private readonly DocumentMapping _mapping;

    internal SubClassFisherStorage(IDocumentStorage<TBase, TId> parent, DocumentMapping mapping)
    {
        _parent = parent;
        _mapping = mapping;
    }

    // ---- identity ----

    /// <remarks>
    ///     The <em>base</em> type, not this one. Every consumer that asks reads it to find the table and
    ///     the identity space, and both belong to the hierarchy rather than to one sub-class.
    /// </remarks>
    public Type SourceType => typeof(TBase);

    public Type IdType => typeof(TId);

    public Type DocumentType => typeof(TDoc);

    public Type SelectedType => typeof(TDoc);

    public TId Identity(TDoc document) => _parent.Identity(document);

    public object IdentityFor(TDoc document) => _parent.IdentityFor(document);

    public TId AssignIdentity(TDoc document, string tenantId, IStorageDatabase database)
        => _parent.AssignIdentity(document, tenantId, database);

    public void SetIdentity(TDoc document, TId identity) => _parent.SetIdentity(document, identity);

    public object RawIdentityValue(object id) => _parent.RawIdentityValue(id);

    public object RawIdentityValue(TId id) => _parent.RawIdentityValue(id);

    public void SetIdentityFromString(TDoc document, string identityString)
        => _parent.SetIdentityFromString(document, identityString);

    public void SetIdentityFromGuid(TDoc document, Guid identityGuid)
        => _parent.SetIdentityFromGuid(document, identityGuid);

    // ---- storage facts, all the hierarchy's ----

    public bool UseOptimisticConcurrency => _parent.UseOptimisticConcurrency;

    public bool UseNumericRevisions => _parent.UseNumericRevisions;

    public TenancyStyle TenancyStyle => _parent.TenancyStyle;

    public DbObjectName TableName => _parent.TableName;

    public IOperationFragment DeleteFragment => _parent.DeleteFragment;

    public IOperationFragment HardDeleteFragment => _parent.HardDeleteFragment;

    public IOperationFragment UndeleteFragment => ((IFisherDocumentStorage)_parent).UndeleteFragment;

    public DeleteStyle DeleteStyle => ((IFisherDocumentStorage)_parent).DeleteStyle;

    public bool IsConjoined => ((IFisherDocumentStorage)_parent).IsConjoined;

    public IReadOnlyList<IDuplicatedField> DuplicatedFields => _parent.DuplicatedFields;

    /// <remarks>
    ///     Truncates the whole hierarchy's table, not just this sub-class's rows. That is what a
    ///     rebuild's teardown needs — a projection replaying into a hierarchy replays every sub-class —
    ///     and narrowing it would leave rows a replay cannot see but the unique id space still holds.
    /// </remarks>
    public Task TruncateDocumentStorageAsync(IStorageDatabase database, CancellationToken token)
        => _parent.TruncateDocumentStorageAsync(database, token);

    // ---- select clause ----

    public string FromObject => _parent.FromObject;

    public string[] SelectFields() => _parent.SelectFields();

    public void Apply(ICommandBuilder builder) => _parent.Apply(builder);

    public bool Contains(string sqlText) => false;

    /// <remarks>
    ///     The base's hierarchical selector materialises each row as whatever its discriminator says,
    ///     then this downcasts. The cast cannot fail, because every query path through this storage
    ///     carries the <c>doc_type</c> filter below.
    /// </remarks>
    public ISelector BuildSelector(IStorageSession session)
        => new CastingSelector<TDoc, TBase>((ISelector<TBase>)_parent.BuildSelector(session));

    /// <remarks>
    ///     <b>Delegated unchanged — the discriminator deliberately does not go here.</b> This runs once
    ///     per caller predicate, so folding the filter in would both repeat it for a multi-clause query
    ///     and, worse, omit it entirely from a query that has no predicate at all. The query layer adds
    ///     it once per statement instead; see <c>FisherQueryProvider.ApplyHierarchyFilter</c>.
    /// </remarks>
    public ISqlFragment FilterDocuments(ISqlFragment query, IStorageSession session)
        => _parent.FilterDocuments(query, session);

    public ISqlFragment? DefaultWhereFragment()
        => _parent.DefaultWhereFragment() is { } parent
            ? new Linq.SqlGeneration.CompoundWhereFragment(" and ", parent, DocTypeFilter())
            : DocTypeFilter();

    /// <summary>
    ///     Matches this sub-class and anything registered below it.
    /// </summary>
    private Linq.SqlGeneration.LiteralSqlFragment DocTypeFilter()
        => new(DocumentHierarchy.FilterSqlFor(_mapping, typeof(TDoc)));

    public ISqlFragment ByIdFilter(TId id) => _parent.ByIdFilter(id);

    // ---- loads ----

    public DbCommand BuildLoadCommand(TId id, string tenantId) => _parent.BuildLoadCommand(id, tenantId);

    public DbCommand BuildLoadManyCommand(TId[] ids, string tenantId)
        => _parent.BuildLoadManyCommand(ids, tenantId);

    /// <inheritdoc cref="SubClassFisherStorage{TDoc,TBase,TId}" />
    public async Task<TDoc?> LoadAsync(TId id, IStorageSession session, CancellationToken token)
        => await _parent.LoadAsync(id, session, token).ConfigureAwait(false) is TDoc match
            ? match
            : default;

    public async Task<IReadOnlyList<TDoc>> LoadManyAsync(TId[] ids, IStorageSession session,
        CancellationToken token)
        => (await _parent.LoadManyAsync(ids, session, token).ConfigureAwait(false)).OfType<TDoc>().ToList();

    public async Task<TDoc?> LoadProjectedAsync(TId id, IStorageDatabase database, string tenantId,
        CancellationToken token)
        => await _parent.LoadProjectedAsync(id, database, tenantId, token).ConfigureAwait(false) is TDoc match
            ? match
            : default;

    public async Task<IReadOnlyList<TDoc>> LoadManyProjectedAsync(TId[] ids, IStorageDatabase database,
        string tenantId, CancellationToken token)
        => (await _parent.LoadManyProjectedAsync(ids, database, tenantId, token).ConfigureAwait(false))
            .OfType<TDoc>().ToList();

    // ---- session bookkeeping ----

    public void Store(IStorageSession session, TDoc document) => _parent.Store(session, document);

    public void Store(IStorageSession session, TDoc document, Guid? version)
        => _parent.Store(session, document, version);

    public void Store(IStorageSession session, TDoc document, long revision)
        => _parent.Store(session, document, revision);

    public Guid? VersionFor(TDoc document, IStorageSession session) => _parent.VersionFor(document, session);

    public void Eject(IStorageSession session, TDoc document) => _parent.Eject(session, document);

    public void EjectById(IStorageSession session, object id) => _parent.EjectById(session, id);

    public void RemoveDirtyTracker(IStorageSession session, object id)
        => _parent.RemoveDirtyTracker(session, id);

    // ---- writes, delegated whole ----

    public Weasel.Storage.IStorageOperation Insert(TDoc document, IStorageSession session, string tenantId)
        => _parent.Insert(document, session, tenantId);

    public Weasel.Storage.IStorageOperation Update(TDoc document, IStorageSession session, string tenantId)
        => _parent.Update(document, session, tenantId);

    public Weasel.Storage.IStorageOperation Upsert(TDoc document, IStorageSession session, string tenantId)
        => _parent.Upsert(document, session, tenantId);

    public Weasel.Storage.IStorageOperation Overwrite(TDoc document, IStorageSession session, string tenantId)
        => _parent.Overwrite(document, session, tenantId);

    public Weasel.Storage.IStorageOperation OverwriteProjected(TDoc document, string tenantId)
        => _parent.OverwriteProjected(document, tenantId);

    public Weasel.Storage.IStorageOperation UpsertProjected(TDoc document, string tenantId)
        => _parent.UpsertProjected(document, tenantId);

    public Weasel.Storage.IStorageOperation InsertProjected(TDoc document, string tenantId)
        => _parent.InsertProjected(document, tenantId);

    public Weasel.Storage.IStorageOperation UpdateProjected(TDoc document, string tenantId)
        => _parent.UpdateProjected(document, tenantId);

    // ---- deletions ----

    public IDeletion DeleteForDocument(TDoc document, string tenantId)
        => _parent.DeleteForDocument(document, tenantId);

    public IDeletion HardDeleteForDocument(TDoc document, string tenantId)
        => _parent.HardDeleteForDocument(document, tenantId);

    public IDeletion DeleteForId(TId id, string tenantId) => _parent.DeleteForId(id, tenantId);

    public IDeletion HardDeleteForId(TId id, string tenantId) => _parent.HardDeleteForId(id, tenantId);
}

/// <summary>
///     Downcasts a base-typed selector's rows to the sub-class.
/// </summary>
/// <remarks>
///     Safe because every read through <see cref="SubClassFisherStorage{TDoc,TBase,TId}" /> either
///     carries the <c>doc_type</c> filter (queries) or tests the result (loads).
/// </remarks>
internal sealed class CastingSelector<TDoc, TBase> : ISelector<TDoc>
    where TDoc : TBase
    where TBase : notnull
{
    private readonly ISelector<TBase> _inner;

    internal CastingSelector(ISelector<TBase> inner) => _inner = inner;

    public TDoc Resolve(DbDataReader reader) => (TDoc)_inner.Resolve(reader)!;

    public async Task<TDoc> ResolveAsync(DbDataReader reader, CancellationToken token)
        => (TDoc)(await _inner.ResolveAsync(reader, token).ConfigureAwait(false))!;
}
