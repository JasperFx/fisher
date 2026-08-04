using System.Data.Common;
using JasperFx.MultiTenancy;
using Weasel.Core;
using Weasel.Core.SqlGeneration;
using Weasel.Storage;

namespace Fisher.Storage.ClosedShape;

/// <summary>
///     The base of Fisher's four closed-shape document storages. Owns everything that does not vary
///     with concurrency mode: the read layout, the load SQL, identity, and deletions.
/// </summary>
/// <remarks>
///     <para>
///         Weasel.Storage supplies the selectors and the write operations; what it does not supply is
///         an <see cref="IDocumentStorage{T,TId}" /> implementation to hold them together, which is
///         what this is. Marten and Polecat each write their own for the same reason.
///     </para>
///     <para>
///         The read layout is a contract with the shared selectors, not a free choice: the writeable
///         flavors read <c>id</c> at column 0 and <c>data</c> at column 1 with metadata from 2, while
///         the query-only selectors omit <c>id</c> entirely and read <c>data</c> at column 0. That is
///         why <see cref="IncludeIdInSelect" /> and <see cref="ReadBinders" /> are both overridable.
///     </para>
/// </remarks>
internal abstract class FisherDocumentStorage<TDoc, TId> : IDocumentStorage<TDoc, TId>
    where TDoc : notnull
    where TId : notnull
{
    protected readonly DocumentStorageDescriptor<TDoc, TId> _descriptor;
    protected readonly DocumentMapping _mapping;

    private readonly IOperationFragment _deleteFragment;
    private readonly string _loaderSql;
    private readonly string _loadManySql;
    private readonly string[] _selectFields;

    protected FisherDocumentStorage(DocumentMapping mapping, DocumentStorageDescriptor<TDoc, TId> descriptor)
    {
        _mapping = mapping;
        _descriptor = descriptor;

        var readColumns = new List<string>();

        if (IncludeIdInSelect)
        {
            readColumns.Add("id");
        }

        readColumns.Add("data");
        readColumns.AddRange(ReadBinders().Select(x => x.ColumnName));
        _selectFields = readColumns.ToArray();

        var select = $"select {string.Join(", ", _selectFields)} from {_mapping.QuotedTableName}";

        // Only a conjoined table has a tenant_id column to filter on. The dialect still binds a
        // tenant parameter on the load path; on a single-tenant table the SQL simply never mentions it.
        var tenantFilter = _mapping.IsConjoined ? " and tenant_id = @tenant_id" : string.Empty;

        _loaderSql = $"{select} where id = @id{tenantFilter}";

        // No array parameters in SQLite — ids arrive as a JSON array that json_each unpacks. The
        // dialect writes that array; see SqliteStorageDialect.CreateIdArrayParameter.
        _loadManySql = $"{select} where id in (select value from json_each(@ids)){tenantFilter}";

        _deleteFragment = new DocumentDeleteFragment($"delete from {_mapping.QuotedTableName}");
    }

    /// <summary>The read binders backing this flavor's SELECT. Query-only narrows the set.</summary>
    protected virtual IDocumentMetadataBinder<TDoc>[] ReadBinders() => _descriptor.ReadBinders;

    /// <summary>The query-only selectors read <c>data</c> at column 0 and have no id column.</summary>
    protected virtual bool IncludeIdInSelect => true;

    // ---- identity ----

    public Type SourceType => typeof(TDoc);
    public Type IdType => typeof(TId);
    public Type DocumentType => typeof(TDoc);
    public Type SelectedType => typeof(TDoc);

    public TId Identity(TDoc document) => _descriptor.Identification.Identity(document);

    public object IdentityFor(TDoc document) => Identity(document);

    public TId AssignIdentity(TDoc document, string tenantId, IStorageDatabase database)
        => _descriptor.Identification.AssignIfMissing(document, database);

    public void SetIdentity(TDoc document, TId identity) => _mapping.SetRawId(document, identity);

    public object RawIdentityValue(object id) => _descriptor.Identification.ToRawSqlValue((TId)id);

    public object RawIdentityValue(TId id) => _descriptor.Identification.ToRawSqlValue(id);

    public void SetIdentityFromString(TDoc document, string identityString)
        => SetIdentity(document, ConvertIdentity(identityString));

    public void SetIdentityFromGuid(TDoc document, Guid identityGuid)
        => SetIdentity(document, typeof(TId) == typeof(Guid)
            ? (TId)(object)identityGuid
            : ConvertIdentity(identityGuid.ToString()));

    private static TId ConvertIdentity(string raw)
    {
        if (typeof(TId) == typeof(string)) return (TId)(object)raw;
        if (typeof(TId) == typeof(Guid)) return (TId)(object)Guid.Parse(raw);
        if (typeof(TId) == typeof(int)) return (TId)(object)int.Parse(raw);
        if (typeof(TId) == typeof(long)) return (TId)(object)long.Parse(raw);

        throw new NotSupportedException($"Cannot convert an identity string to {typeof(TId).FullName}.");
    }

    // ---- storage facts ----

    public bool UseOptimisticConcurrency => _descriptor.ConcurrencyMode == ConcurrencyMode.Optimistic;

    /// <summary>
    ///     Always false — numeric revisions are not implemented. See CLAUDE.md for the slice boundary.
    /// </summary>
    public bool UseNumericRevisions => false;

    public TenancyStyle TenancyStyle => _mapping.TenancyStyle;

    public DbObjectName TableName => _mapping.TableName;

    public IOperationFragment DeleteFragment => _deleteFragment;

    public IOperationFragment HardDeleteFragment => _deleteFragment;

    /// <summary>
    ///     Empty — Fisher has no duplicated fields, so nothing is projected out of the JSON body into
    ///     its own column.
    /// </summary>
    public IReadOnlyList<IDuplicatedField> DuplicatedFields => Array.Empty<IDuplicatedField>();

    // ---- select clause ----

    public string FromObject => _mapping.QuotedTableName;

    public string[] SelectFields() => _selectFields;

    public void Apply(ICommandBuilder builder)
    {
        builder.Append("select ");
        builder.Append(string.Join(", ", _selectFields));
        builder.Append(" from ");
        builder.Append(_mapping.QuotedTableName);
    }

    public abstract ISelector BuildSelector(IStorageSession session);

    public bool Contains(string sqlText) => false;

    public ISqlFragment FilterDocuments(ISqlFragment query, IStorageSession session)
        => _mapping.IsConjoined
            ? new AllOfFragments([query, new TenantFilterFragment(session.TenantId)])
            : query;

    /// <summary>
    ///     Null — with no soft delete there is no row a query should implicitly exclude.
    /// </summary>
    public ISqlFragment? DefaultWhereFragment() => null;

    public ISqlFragment ByIdFilter(TId id) => _descriptor.Dialect.ByIdFilter(RawIdentityValue(id));

    // ---- loads ----

    public DbCommand BuildLoadCommand(TId id, string tenantId)
        => _descriptor.Dialect.BuildLoadCommand(_loaderSql, RawIdentityValue(id), TenantArgument(tenantId));

    public DbCommand BuildLoadManyCommand(TId[] ids, string tenantId)
        => _descriptor.Dialect.BuildLoadManyCommand(_loadManySql, BuildManyIdParameter(ids),
            TenantArgument(tenantId));

    /// <summary>
    ///     The tenant to bind, or null on a single-tenant table where the SQL has no tenant predicate
    ///     and binding one would leave an unreferenced parameter behind.
    /// </summary>
    private string? TenantArgument(string tenantId) => _mapping.IsConjoined ? tenantId : null;

    private DbParameter BuildManyIdParameter(TId[] ids)
        => _descriptor.Dialect.CreateIdArrayParameter(
            Array.ConvertAll(ids, RawIdentityValue), _descriptor.Identification.RawSqlType);

    public abstract Task<TDoc?> LoadAsync(TId id, IStorageSession session, CancellationToken token);

    public abstract Task<IReadOnlyList<TDoc>> LoadManyAsync(TId[] ids, IStorageSession session,
        CancellationToken token);

    protected async Task<TDoc?> QueryOneAsync(TId id, IStorageSession session, CancellationToken token)
    {
        var command = BuildLoadCommand(id, session.TenantId);
        var selector = (ISelector<TDoc>)BuildSelector(session);

        await using var reader = await session.ExecuteReaderAsync(command, token).ConfigureAwait(false);

        return await reader.ReadAsync(token).ConfigureAwait(false)
            ? await selector.ResolveAsync(reader, token).ConfigureAwait(false)
            : default;
    }

    protected async Task<IReadOnlyList<TDoc>> QueryManyAsync(TId[] ids, IStorageSession session,
        CancellationToken token)
    {
        if (ids.Length == 0)
        {
            return Array.Empty<TDoc>();
        }

        var command = BuildLoadManyCommand(ids, session.TenantId);
        var selector = (ISelector<TDoc>)BuildSelector(session);
        var results = new List<TDoc>();

        await using var reader = await session.ExecuteReaderAsync(command, token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            results.Add(await selector.ResolveAsync(reader, token).ConfigureAwait(false));
        }

        return results;
    }

    public Task<TDoc?> LoadProjectedAsync(TId id, IStorageDatabase database, string tenantId,
        CancellationToken token)
        => ClosedShapeProjectionLoader<TDoc, TId>.LoadAsync(
            BuildLoadCommand(id, tenantId), _descriptor, _descriptor.Serializer, database, token);

    public Task<IReadOnlyList<TDoc>> LoadManyProjectedAsync(TId[] ids, IStorageDatabase database, string tenantId,
        CancellationToken token)
        => ClosedShapeProjectionLoader<TDoc, TId>.LoadManyAsync(
            BuildLoadManyCommand(ids, tenantId), _descriptor, _descriptor.Serializer, database, token);

    // ---- writes ----

    public Guid? VersionFor(TDoc document, IStorageSession session)
        => session.Versions.VersionFor<TDoc, TId>(Identity(document));

    public abstract void Store(IStorageSession session, TDoc document);
    public abstract void Store(IStorageSession session, TDoc document, Guid? version);
    public abstract void Store(IStorageSession session, TDoc document, long revision);

    public abstract Weasel.Storage.IStorageOperation Update(TDoc document, IStorageSession session, string tenantId);
    public abstract Weasel.Storage.IStorageOperation Insert(TDoc document, IStorageSession session, string tenantId);
    public abstract Weasel.Storage.IStorageOperation Upsert(TDoc document, IStorageSession session, string tenantId);
    public abstract Weasel.Storage.IStorageOperation Overwrite(TDoc document, IStorageSession session, string tenantId);

    public abstract Weasel.Storage.IStorageOperation OverwriteProjected(TDoc document, string tenantId);
    public abstract Weasel.Storage.IStorageOperation UpsertProjected(TDoc document, string tenantId);
    public abstract Weasel.Storage.IStorageOperation InsertProjected(TDoc document, string tenantId);
    public abstract Weasel.Storage.IStorageOperation UpdateProjected(TDoc document, string tenantId);

    // ---- ejection ----

    public virtual void Eject(IStorageSession session, TDoc document) => EjectById(session, Identity(document));

    public virtual void EjectById(IStorageSession session, object id)
    {
        if (session.ItemMap.TryGetValue(typeof(TDoc), out var raw) && raw is Dictionary<TId, TDoc> map)
        {
            map.Remove((TId)id);
        }
    }

    /// <summary>
    ///     A no-op — Fisher has no dirty tracking, mirroring Polecat.
    /// </summary>
    public void RemoveDirtyTracker(IStorageSession session, object id)
    {
    }

    // ---- deletions ----

    public IDeletion DeleteForDocument(TDoc document, string tenantId)
        => BuildDeletion(Identity(document), tenantId);

    public IDeletion HardDeleteForDocument(TDoc document, string tenantId)
        => BuildDeletion(Identity(document), tenantId);

    public IDeletion DeleteForId(TId id, string tenantId) => BuildDeletion(id, tenantId);

    public IDeletion HardDeleteForId(TId id, string tenantId) => BuildDeletion(id, tenantId);

    private IDeletion BuildDeletion(TId id, string tenantId)
    {
        var sql = _mapping.IsConjoined
            ? $"delete from {_mapping.QuotedTableName} where id = ? and tenant_id = ?"
            : $"delete from {_mapping.QuotedTableName} where id = ?";

        return new DocumentDeletion<TDoc, TId>(sql, id, tenantId, _descriptor, _mapping.IsConjoined);
    }

    public async Task TruncateDocumentStorageAsync(IStorageDatabase database, CancellationToken ct = default)
        => await database.RunSqlAsync($"delete from {_mapping.QuotedTableName}", ct).ConfigureAwait(false);
}
