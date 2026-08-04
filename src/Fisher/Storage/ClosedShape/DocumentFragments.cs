using System.Data.Common;
using JasperFx;
using Weasel.Core;
using Weasel.Core.SqlGeneration;
using Weasel.Storage;

namespace Fisher.Storage.ClosedShape;

/// <summary>
///     A fixed piece of operation SQL — the <c>delete from …</c> head that the LINQ-side delete-where
///     machinery appends a filter to.
/// </summary>
internal sealed class DocumentDeleteFragment : IOperationFragment
{
    private readonly string _sql;

    public DocumentDeleteFragment(string sql)
    {
        _sql = sql;
    }

    public void Apply(ICommandBuilder builder) => builder.Append(_sql);

    public OperationRole Role() => OperationRole.Deletion;
}

/// <summary>
///     Deletes one document by identity.
/// </summary>
/// <remarks>
///     Hard and soft deletion are the same operation in Fisher, because there is no soft delete: a
///     document row is removed outright. <c>DeleteForId</c> and <c>HardDeleteForId</c> therefore both
///     land here.
/// </remarks>
internal sealed class DocumentDeletion<TDoc, TId> : IDeletion
    where TDoc : notnull
    where TId : notnull
{
    private readonly DocumentStorageDescriptor<TDoc, TId> _descriptor;
    private readonly bool _isConjoined;
    private readonly string _sql;
    private readonly string _tenantId;

    public DocumentDeletion(string sql, TId id, string tenantId,
        DocumentStorageDescriptor<TDoc, TId> descriptor, bool isConjoined)
    {
        _sql = sql;
        _tenantId = tenantId;
        _descriptor = descriptor;
        _isConjoined = isConjoined;

        Id = id;
        Document = null!;
    }

    public object Id { get; set; }

    public object Document { get; set; }

    public Type DocumentType => typeof(TDoc);

    public OperationRole Role() => OperationRole.Deletion;

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
///     Restricts a query to the session's tenant.
/// </summary>
internal sealed class TenantFilterFragment : ISqlFragment
{
    private readonly string _tenantId;

    public TenantFilterFragment(string tenantId)
    {
        _tenantId = tenantId;
    }

    public void Apply(ICommandBuilder builder)
    {
        builder.Append(StorageConstants.TenantIdColumn);
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
