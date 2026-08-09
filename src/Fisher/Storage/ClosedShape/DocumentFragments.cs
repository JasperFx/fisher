using System.Data.Common;
using JasperFx;
using Weasel.Core;
using Weasel.Core.SqlGeneration;
using Weasel.Storage;

namespace Fisher.Storage.ClosedShape;

/// <summary>
///     What the storage knows about a document type that the shared
///     <see cref="IDocumentStorage{T,TId}" /> contract has no slot for.
/// </summary>
/// <remarks>
///     Reached by casting, because a session holds an <c>IDocumentStorage&lt;T&gt;</c> — open in
///     <c>TId</c> — and the criteria-based operations need the undelete SQL and the tenancy style
///     without closing over an identity type they never bind. Polecat solves the same problem with its
///     own storage interface.
/// </remarks>
internal interface IFisherDocumentStorage
{
    /// <summary>Whether <c>Delete</c> flags the row or removes it.</summary>
    DeleteStyle DeleteStyle { get; }

    /// <summary><c>update … set is_deleted = 0, deleted_at = null</c>.</summary>
    IOperationFragment UndeleteFragment { get; }

    /// <summary>Whether the table carries a <c>tenant_id</c> column to scope an operation by.</summary>
    bool IsConjoined { get; }
}

/// <summary>
///     A fixed piece of operation SQL — the <c>delete from …</c> or <c>update … set</c> head that the
///     criteria-based operations append a filter to.
/// </summary>
internal sealed class DocumentSqlFragment : IOperationFragment
{
    private readonly OperationRole _role;
    private readonly string _sql;

    public DocumentSqlFragment(string sql, OperationRole role)
    {
        _sql = sql;
        _role = role;
    }

    public void Apply(ICommandBuilder builder) => builder.Append(_sql);

    public OperationRole Role() => _role;
}

/// <summary>
///     One statement against one document, identified by its id.
/// </summary>
/// <remarks>
///     The SQL is supplied whole by the storage; all this owns is the binding order, which is id and
///     then the tenant. A trailing guard such as <c>and is_deleted = 0</c> contributes no parameter, so
///     it can be part of that SQL without shifting a slot.
/// </remarks>
internal class DocumentByIdCommand<TDoc, TId> : Weasel.Storage.IStorageOperation
    where TDoc : notnull
    where TId : notnull
{
    private readonly DocumentStorageDescriptor<TDoc, TId> _descriptor;
    private readonly bool _isConjoined;
    private readonly OperationRole _role;
    private readonly string _sql;
    private readonly string _tenantId;

    public DocumentByIdCommand(string sql, TId id, string tenantId,
        DocumentStorageDescriptor<TDoc, TId> descriptor, bool isConjoined, OperationRole role)
    {
        _sql = sql;
        _tenantId = tenantId;
        _descriptor = descriptor;
        _isConjoined = isConjoined;
        _role = role;

        Id = id;
    }

    protected object Id { get; set; }

    public Type DocumentType => typeof(TDoc);

    public OperationRole Role() => _role;

    public void ConfigureCommand(ICommandBuilder builder, IStorageSession session)
    {
        var parameters = builder.AppendWithDbParameters(_sql, '?');
        var slot = 0;

        parameters[slot].Value = _descriptor.Identification.ToRawSqlValue((TId)Id);
        _descriptor.Dialect.SetIdParameterType(parameters[slot], _descriptor.Identification.RawSqlType);
        slot++;

        if (_isConjoined)
        {
            parameters[slot].Value = _tenantId;
            _descriptor.Dialect.SetParameterType(parameters[slot], StorageColumnType.String);
        }
    }

    public Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
        => Task.CompletedTask;
}

/// <summary>
///     Deletes one document by identity.
/// </summary>
/// <remarks>
///     Which statement that is depends on the mapping's <see cref="DeleteStyle" />: a
///     <c>delete from …</c> for an ordinary type, an <c>update … set is_deleted = 1</c> for a
///     soft-deleted one. <c>HardDeleteForId</c> always gets the former.
/// </remarks>
internal sealed class DocumentDeletion<TDoc, TId> : DocumentByIdCommand<TDoc, TId>, IDeletion
    where TDoc : notnull
    where TId : notnull
{
    public DocumentDeletion(string sql, TId id, string tenantId,
        DocumentStorageDescriptor<TDoc, TId> descriptor, bool isConjoined)
        : base(sql, id, tenantId, descriptor, isConjoined, OperationRole.Deletion)
    {
        Document = null!;
    }

    object IDeletion.Id
    {
        get => Id;
        set => Id = value;
    }

    public object Document { get; set; }
}

/// <summary>
///     One statement against every document matching a LINQ predicate — <c>DeleteWhere</c>,
///     <c>HardDeleteWhere</c> and <c>UndoDeleteWhere</c>, which differ only in the SQL head they are
///     handed and the guard they carry.
/// </summary>
/// <remarks>
///     The predicate arrives already parsed, so this holds no LINQ machinery. Ordering inside the
///     <c>where</c> is tenant, then guard, then predicate: the first two are the storage's own terms
///     and the predicate is the caller's, and keeping the caller's last means an <c>or</c> inside it
///     cannot swallow them — <c>CompoundWhereFragment</c> parenthesizes a compound predicate.
/// </remarks>
internal sealed class DocumentWhereOperation : Weasel.Storage.IStorageOperation
{
    private readonly IOperationFragment _fragment;
    private readonly string? _guard;
    private readonly OperationRole _role;
    private readonly string? _tenantId;
    private readonly ISqlFragment _where;

    public DocumentWhereOperation(IOperationFragment fragment, Type documentType, ISqlFragment where,
        string? tenantId, string? guard, OperationRole role)
    {
        _fragment = fragment;
        _where = where;
        _tenantId = tenantId;
        _guard = guard;
        _role = role;

        DocumentType = documentType;
    }

    public Type DocumentType { get; }

    public OperationRole Role() => _role;

    public void ConfigureCommand(ICommandBuilder builder, IStorageSession session)
    {
        _fragment.Apply(builder);
        builder.Append(" where ");

        // Single-tenant tables have no tenant_id column, so both the predicate and its parameter are
        // conjoined-only.
        if (_tenantId is not null)
        {
            builder.Append(StorageConstants.TenantIdColumn);
            builder.Append(" = ");
            builder.AppendParameter(_tenantId);
            builder.Append(" and ");
        }

        if (_guard is not null)
        {
            builder.Append(_guard);
            builder.Append(" and ");
        }

        _where.Apply(builder);
    }

    public Task PostprocessAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
        => Task.CompletedTask;
}

/// <summary>
///     Restricts a query to the session's tenant.
/// </summary>
internal sealed class TenantFilterFragment : ISqlFragment
{
    private readonly string _tenantId;
    private readonly string _qualifier;

    /// <param name="tenantId">The tenant to scope to.</param>
    /// <param name="qualifier">
    ///     A table alias and its dot, or empty. Non-empty only inside a join (fisher#25), where both
    ///     sides have a <c>tenant_id</c> and an unqualified one is ambiguous.
    /// </param>
    public TenantFilterFragment(string tenantId, string qualifier = "")
    {
        _tenantId = tenantId;
        _qualifier = qualifier;
    }

    public void Apply(ICommandBuilder builder)
    {
        builder.Append(_qualifier + StorageConstants.TenantIdColumn);
        builder.Append(" = ");
        builder.AppendParameter(_tenantId);
    }
}

/// <summary>
///     Joins fragments with <c>and</c>.
/// </summary>
internal sealed class AllOfFragments : ICompoundFragment
{
    private readonly IReadOnlyList<ISqlFragment> _children;

    public AllOfFragments(IReadOnlyList<ISqlFragment> children)
    {
        _children = children;
    }

    public IEnumerable<ISqlFragment> Children => _children;

    public void Apply(ICommandBuilder builder)
    {
        for (var i = 0; i < _children.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(" and ");
            }

            _children[i].Apply(builder);
        }
    }
}
