using JasperFx.Core.Reflection;
using JasperFx.MultiTenancy;
using JasperFx;
using Weasel.Core;
using Weasel.Core.SqlGeneration;
using Weasel.Storage;

namespace Fisher.Storage.ClosedShape;

/// <summary>
///     Everything about one document type's closed-shape storage that is Fisher's rather than Weasel's
///     (fisher#465): the SQLite read and delete SQL, the tenant and soft-delete filters, identity
///     assignment, the mapped version member, and undelete/restore by id.
/// </summary>
/// <remarks>
///     <para>
///         <b>One instance serves all four tracking modes of a document type</b>, which is Weasel's
///         contract for <see cref="IClosedShapeStorageRules{TDoc,TId}" />: the members that take a
///         storage are called once per storage with a different one each time, so nothing here
///         remembers a storage. The storages themselves are Weasel's tracking-mode bases, derived in
///         <c>FisherClosedShapeStorages.cs</c> only to carry <see cref="IFisherDocumentStorage" />.
///     </para>
///     <para>
///         <b>The read layout is a contract with Weasel's selectors, not a free choice</b>: the
///         writeable modes read <c>id</c> at column 0 and <c>data</c> at 1 with metadata from 2, while
///         the query-only selectors omit <c>id</c> and read <c>data</c> at 0 over the narrower
///         <see cref="DocumentStorageDescriptor{TDoc,TId}.QueryOnlyReadBinders" />.
///         <see cref="ReadSqlFor" /> is where that is decided.
///     </para>
///     <para>
///         <b>The write SQL is not here</b>: it is the descriptor's, built by
///         <see cref="SqliteDocumentStorageDescriptorBuilder" />, whose column order and <c>?</c> order are
///         one contract with the shared write operations. Weasel's bases build exactly the operations
///         Fisher's own storages used to, over that same descriptor.
///     </para>
/// </remarks>
internal sealed class FisherStorageRules<TDoc, TId> : IClosedShapeStorageRules<TDoc, TId>
    where TDoc : notnull
    where TId : notnull
{
    private readonly DocumentStorageDescriptor<TDoc, TId> _descriptor;
    private readonly DocumentMapping _mapping;

    private readonly IOperationFragment _deleteFragment;
    private readonly IOperationFragment _hardDeleteFragment;
    private readonly IOperationFragment? _undeleteFragment;
    private readonly Func<TDoc, Guid>? _versionGetter;

    public FisherStorageRules(DocumentMapping mapping, DocumentStorageDescriptor<TDoc, TId> descriptor)
    {
        _mapping = mapping;
        _descriptor = descriptor;

        _hardDeleteFragment = new DocumentSqlFragment($"delete from {_mapping.QuotedTableName}",
            OperationRole.Deletion);

        _deleteFragment = _mapping.IsSoftDeleted
            ? new DocumentSqlFragment(SoftDelete.MarkDeletedSql(_mapping.QuotedTableName), OperationRole.Deletion)
            : _hardDeleteFragment;

        _undeleteFragment = _mapping.IsSoftDeleted
            ? new DocumentSqlFragment(SoftDelete.ClearDeletedSql(_mapping.QuotedTableName), OperationRole.Update)
            : null;

        // Compiled once per document type and only when a member is mapped, so a type with no version
        // member costs one null check per call. See MappedVersionFor.
        if (_mapping.Metadata.Version.Member is { } member)
        {
            _versionGetter = LambdaBuilder.Getter<TDoc, Guid>(member);
        }
    }

    /// <summary>Whether <c>Delete</c> flags the row or removes it.</summary>
    public DeleteStyle DeleteStyle => _mapping.DeleteStyle;

    /// <summary>Whether the table carries a <c>tenant_id</c> column to scope an operation by.</summary>
    public bool IsConjoined => _mapping.IsConjoined;

    // ---- shape ----

    public DbObjectName TableName => _mapping.TableName;

    public TenancyStyle TenancyStyle => _mapping.TenancyStyle;

    /// <summary>
    ///     The members lifted into columns of their own. Read-only here in every sense: Fisher's
    ///     duplicated columns are generated from <c>data</c>, so no write operation binds one.
    /// </summary>
    public IReadOnlyList<IDuplicatedField> DuplicatedFields => _mapping.DuplicatedFields;

    public ClosedShapeReadSql ReadSqlFor(ClosedShapeTrackingMode mode)
    {
        var readColumns = new List<string>();
        IDocumentMetadataBinder<TDoc>[] binders;

        if (mode == ClosedShapeTrackingMode.QueryOnly)
        {
            binders = _descriptor.QueryOnlyReadBinders;
        }
        else
        {
            readColumns.Add("id");
            binders = _descriptor.ReadBinders;
        }

        readColumns.Add("data");
        readColumns.AddRange(binders.Select(x => x.ColumnName));

        var fields = readColumns.ToArray();
        var select = $"select {string.Join(", ", fields)} from {_mapping.QuotedTableName}";

        // Only a conjoined table has a tenant_id column to filter on. The dialect still binds a
        // tenant parameter on the load path; on a single-tenant table the SQL simply never mentions it.
        var tenantFilter = _mapping.IsConjoined ? " and tenant_id = @tenant_id" : string.Empty;

        // A soft-deleted row is invisible to a load by id, exactly as it is to a query. Baked into
        // the SQL rather than added by a caller, because LoadAsync and LoadManyAsync are reached from
        // the session, the projection loader and the daemon alike, and a filter one of them forgot
        // would present as a deleted document coming back to life on one path only.
        var deletedFilter = _mapping.IsSoftDeleted ? $" and {SoftDelete.NotDeletedSql}" : string.Empty;

        // No array parameters in SQLite: ids arrive as a JSON array that json_each unpacks. The
        // dialect writes that array; see SqliteStorageDialect.CreateIdArrayParameter.
        return new ClosedShapeReadSql(
            fields,
            select,
            $"{select} where id = @id{tenantFilter}{deletedFilter}",
            $"{select} where id in (select value from json_each(@ids)){tenantFilter}{deletedFilter}");
    }

    // ---- filters ----

    public ISqlFragment FilterDocuments(ISqlFragment query, IStorageSession session)
        => _mapping.IsConjoined
            ? new AllOfFragments([query, new TenantFilterFragment(session.TenantId)])
            : query;

    /// <summary>
    ///     The filter every read of this type carries implicitly — <c>is_deleted = 0</c> for a
    ///     soft-deleted type, and null for one whose rows are removed outright, where there is nothing
    ///     to exclude.
    /// </summary>
    /// <remarks>
    ///     The LINQ provider applies this unless the query said otherwise with <c>MaybeDeleted()</c> or
    ///     <c>IsDeleted()</c>; the load-by-id SQL carries its own copy, because it is not built through
    ///     a statement.
    /// </remarks>
    public ISqlFragment? DefaultWhereFragment()
        => _mapping.IsSoftDeleted ? new Linq.SqlGeneration.LiteralSqlFragment(SoftDelete.NotDeletedSql) : null;

    public string TruncateSql => $"delete from {_mapping.QuotedTableName}";

    // ---- identity ----

    public void SetIdentity(TDoc document, TId identity) => _mapping.SetRawId(document, identity);

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

    // ---- versions ----

    /// <summary>
    ///     The Guid version the document itself carries, or <see langword="null" /> when the mapping has
    ///     no version member to read (fisher#245, weasel#590).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is what lets a guarded write cross a session boundary.</b> Fisher's optimistic
    ///         guard is fed from <c>IStorageSession.Versions</c> — what <em>this session read</em> — so
    ///         a document loaded in one session and stored through another had no entry, and every such
    ///         write failed its guard. See <c>FisherSession.SeedExpectedVersion</c> for the other half.
    ///     </para>
    ///     <para>
    ///         <b>One member covers both ways of declaring a version</b>, because
    ///         <see cref="Metadata.DocumentMetadata" /> already maps <c>IVersioned.Version</c> onto this
    ///         column by convention — the same thing Marten's <c>VersionedPolicy</c> does. So the
    ///         interface route and <c>Metadata(m =&gt; m.Version.MapTo(...))</c> arrive here identically,
    ///         which is the asymmetry marten#5372 was and the reason this is a mapping read rather than
    ///         a type test on <c>IVersioned</c>.
    ///     </para>
    ///     <para>
    ///         Weasel's storages forward to this, and so does its sub-class storage, so a hierarchy
    ///         cannot lose it the way <c>SubClassFisherStorage</c> once did (fisher#394).
    ///     </para>
    /// </remarks>
    public Guid? MappedVersionFor(TDoc document) => _versionGetter?.Invoke(document);

    // ---- deletions ----

    public IOperationFragment BuildDeleteFragment(IDocumentStorage<TDoc, TId> storage) => _deleteFragment;

    public IOperationFragment BuildHardDeleteFragment(IDocumentStorage<TDoc, TId> storage) => _hardDeleteFragment;

    /// <summary>
    ///     <c>update … set is_deleted = 0, deleted_at = null</c>, or null for a type whose delete removes
    ///     the row and so has nothing to bring back.
    /// </summary>
    public IOperationFragment? BuildUndeleteFragment(IDocumentStorage<TDoc, TId> storage) => _undeleteFragment;

    /// <remarks>
    ///     Weasel hands over its own id filter; Fisher writes its own <c>where</c> instead, because the
    ///     binding order (id, then the tenant) is <see cref="DocumentByIdCommand{TDoc,TId}" />'s, and the
    ///     soft form adds a guard. A delete is hard when it was asked for as one, or when the type has no
    ///     soft form to offer.
    /// </remarks>
    public IDeletion BuildDeletion(IDocumentStorage<TDoc, TId> storage, IOperationFragment fragment, TId id,
        ISqlFragment byIdFilter, string tenantId)
    {
        var hard = !_mapping.IsSoftDeleted || ReferenceEquals(fragment, _hardDeleteFragment);

        // The soft form guards on is_deleted = 0 so that deleting an already-deleted document is a
        // no-op rather than a second deletion: without it a repeated Delete would push deleted_at
        // forward, and "deleted since" would be answering about the last call rather than the first.
        var sql = hard
            ? $"delete from {_mapping.QuotedTableName}{WhereIdSql(guard: null)}"
            : SoftDelete.MarkDeletedSql(_mapping.QuotedTableName) + WhereIdSql(guard: SoftDelete.NotDeletedSql);

        return new DocumentDeletion<TDoc, TId>(sql, id, tenantId, _descriptor, _mapping.IsConjoined);
    }

    /// <summary>
    ///     Bring one soft-deleted row back, or null for a type whose rows are removed outright and
    ///     therefore has nothing to bring back.
    /// </summary>
    public Weasel.Storage.IStorageOperation? BuildUndeletion(IDocumentStorage<TDoc, TId> storage, IOperationFragment? undeleteFragment,
        TId id, ISqlFragment byIdFilter, string tenantId)
    {
        if (!_mapping.IsSoftDeleted)
        {
            return null;
        }

        return new DocumentByIdCommand<TDoc, TId>(
            SoftDelete.ClearDeletedSql(_mapping.QuotedTableName) + WhereIdSql(guard: SoftDelete.DeletedSql),
            id, tenantId, _descriptor, _mapping.IsConjoined, OperationRole.Update);
    }

    /// <remarks>
    ///     <para>
    ///         <see cref="BuildUndeletion" />'s statement plus the columns a write moves, for the
    ///         diagnostics writer's <c>UndeleteDocumentAsync</c> (jasperfx#1059), whose contract restores
    ///         the document "live, with a new version". <c>UndoDeleteWhere</c> and a projection's
    ///         <c>UnDelete</c> keep the narrower statement: neither has promised a caller a new version.
    ///     </para>
    ///     <para>
    ///         <c>data</c> is not rewritten, so the body comes back byte-exact rather than through a
    ///         deserialize and re-serialize that would drop any member this deployment's type does not
    ///         have. The new <c>guid_version</c> is generated in SQL, so the statement binds exactly the
    ///         id-then-tenant slots <see cref="DocumentByIdCommand{TDoc,TId}" /> knows how to fill.
    ///     </para>
    ///     <para>
    ///         ⚠️ <b><c>last_modified</c> has to move strictly forward</b> (fisher#507). A type with no
    ///         version column gets a content hash at millisecond precision, so a restore and a re-delete
    ///         inside the millisecond of the original write and delete reproduced the old token, and a
    ///         stale expected version was accepted.
    ///     </para>
    /// </remarks>
    public Weasel.Storage.IStorageOperation? BuildRestoration(IDocumentStorage<TDoc, TId> storage, TId id, ISqlFragment byIdFilter,
        string tenantId)
    {
        if (!_mapping.IsSoftDeleted)
        {
            return null;
        }

        var metadata = _mapping.Metadata;
        var sql = SoftDelete.ClearDeletedSql(_mapping.QuotedTableName)
                  + $", {metadata.LastModified.Name} = {SqliteTimestamp.StrictlyAfter(metadata.LastModified.Name)}";

        if (_mapping.UseOptimisticConcurrency)
        {
            sql += $", {metadata.Version.Name} = (select substr(h, 1, 8) || '-' || substr(h, 9, 4) || '-' || "
                   + "substr(h, 13, 4) || '-' || substr(h, 17, 4) || '-' || substr(h, 21, 12) "
                   + "from (select hex(randomblob(16)) as h))";
        }

        if (_mapping.UseNumericRevisions)
        {
            sql += $", {metadata.Revision.Name} = coalesce({metadata.Revision.Name}, 0) + 1";
        }

        return new DocumentByIdCommand<TDoc, TId>(sql + WhereIdSql(guard: SoftDelete.DeletedSql),
            id, tenantId, _descriptor, _mapping.IsConjoined, OperationRole.Update);
    }

    /// <summary>
    ///     The trailing <c>where</c>, in the order <see cref="DocumentByIdCommand{TDoc,TId}" /> binds:
    ///     id, then the tenant when there is a column for it. A guard adds no parameter, so it can sit
    ///     at the end without moving a slot.
    /// </summary>
    private string WhereIdSql(string? guard)
    {
        var sql = " where id = ?";

        if (_mapping.IsConjoined)
        {
            sql += " and tenant_id = ?";
        }

        return guard is null ? sql : $"{sql} and {guard}";
    }
}
