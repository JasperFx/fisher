using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Fisher.Storage.Metadata;

/// <summary>
///     <see cref="DocumentMetadata" /> typed over the document, so a column is mapped with a lambda and
///     the compiler checks the member's type rather than <see cref="MetadataColumn" /> checking it at
///     run time.
/// </summary>
public class DocumentMetadataExpression<T> where T : notnull
{
    internal DocumentMetadataExpression(DocumentMetadata metadata)
    {
        Version = new MetadataColumnExpression<T, Guid>(metadata.Version);
        LastModified = new MetadataColumnExpression<T, DateTimeOffset>(metadata.LastModified);
        IsSoftDeleted = new MetadataColumnExpression<T, bool>(metadata.IsSoftDeleted);
        DeletedAt = new MetadataColumnExpression<T, DateTimeOffset?>(metadata.DeletedAt);
        CreatedAt = new OptionalMetadataColumnExpression<T, DateTimeOffset>(metadata.CreatedAt);
        CorrelationId = new OptionalMetadataColumnExpression<T, string>(metadata.CorrelationId);
        CausationId = new OptionalMetadataColumnExpression<T, string>(metadata.CausationId);
        LastModifiedBy = new OptionalMetadataColumnExpression<T, string>(metadata.LastModifiedBy);
        Headers = new OptionalMetadataColumnExpression<T, Dictionary<string, object>>(metadata.Headers);
        TenantId = new OptionalMetadataColumnExpression<T, string>(metadata.TenantId);
    }

    /// <inheritdoc cref="DocumentMetadata.Version" />
    public MetadataColumnExpression<T, Guid> Version { get; }

    /// <inheritdoc cref="DocumentMetadata.LastModified" />
    public MetadataColumnExpression<T, DateTimeOffset> LastModified { get; }

    /// <inheritdoc cref="DocumentMetadata.IsSoftDeleted" />
    public MetadataColumnExpression<T, bool> IsSoftDeleted { get; }

    /// <inheritdoc cref="DocumentMetadata.DeletedAt" />
    public MetadataColumnExpression<T, DateTimeOffset?> DeletedAt { get; }

    /// <inheritdoc cref="DocumentMetadata.CreatedAt" />
    public OptionalMetadataColumnExpression<T, DateTimeOffset> CreatedAt { get; }

    /// <inheritdoc cref="DocumentMetadata.CorrelationId" />
    public OptionalMetadataColumnExpression<T, string> CorrelationId { get; }

    /// <inheritdoc cref="DocumentMetadata.CausationId" />
    public OptionalMetadataColumnExpression<T, string> CausationId { get; }

    /// <inheritdoc cref="DocumentMetadata.LastModifiedBy" />
    public OptionalMetadataColumnExpression<T, string> LastModifiedBy { get; }

    /// <inheritdoc cref="DocumentMetadata.Headers" />
    public OptionalMetadataColumnExpression<T, Dictionary<string, object>> Headers { get; }

    /// <inheritdoc cref="DocumentMetadata.TenantId" />
    public OptionalMetadataColumnExpression<T, string> TenantId { get; }
}

/// <summary>
///     A metadata column that has to be turned on before it exists — the fisher#29 five, plus the
///     read-back of <c>tenant_id</c>.
/// </summary>
/// <remarks>
///     Separate from <see cref="MetadataColumnExpression{T,TValue}" /> so that <see cref="Enabled" />
///     appears only where it means something. Whether the other columns exist is decided by
///     <c>UseOptimisticConcurrency()</c>, <c>SoftDeleted()</c> and the like, so offering the flag there
///     too would be a knob that silently does nothing.
/// </remarks>
public sealed class OptionalMetadataColumnExpression<T, TValue> : MetadataColumnExpression<T, TValue>
    where T : notnull
{
    private readonly MetadataColumn _column;

    internal OptionalMetadataColumnExpression(MetadataColumn column) : base(column)
    {
        _column = column;
    }

    /// <summary>
    ///     Create the column and start writing it, without projecting it onto a member.
    /// </summary>
    /// <remarks>
    ///     Setting it false again is deliberately not offered: a column is created by the migration and
    ///     removing one is a schema change with data in it, so "off" means "was never turned on".
    /// </remarks>
    public bool Enabled
    {
        get => _column.Enabled;
        set
        {
            if (value)
            {
                _column.Enable();
            }
            else if (_column.Enabled)
            {
                throw new InvalidOperationException(
                    $"Metadata column '{_column.Name}' has already been enabled for {typeof(T).Name}. "
                    + "Dropping a column that may hold data is a migration, not a configuration flag.");
            }
        }
    }
}

/// <summary>
///     One metadata column, typed over both the document and the column's value.
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2070:UnrecognizedReflectionPattern",
    Justification =
        "Class-level: resolves the member named by a caller's own lambda. The document type and its members are preserved at the registration boundary on the caller side.")]
public class MetadataColumnExpression<T, TValue> where T : notnull
{
    private readonly MetadataColumn _column;

    internal MetadataColumnExpression(MetadataColumn column)
    {
        _column = column;
    }

    /// <summary>
    ///     Project this column onto a member of the document — <c>x =&gt; x.DeletedAt</c>.
    /// </summary>
    /// <remarks>
    ///     A member of the document itself, not a chain into a nested object: the value is assigned by a
    ///     setter when the row is read, and there is nothing to guarantee an intermediate object exists
    ///     to assign through.
    /// </remarks>
    public void MapTo(Expression<Func<T, TValue>> member)
    {
        ArgumentNullException.ThrowIfNull(member);

        _column.MapTo(MemberOf(member));
    }

    private static MemberInfo MemberOf(Expression<Func<T, TValue>> expression)
    {
        var body = expression.Body;

        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            body = unary.Operand;
        }

        if (body is MemberExpression { Expression: ParameterExpression } member)
        {
            return member.Member;
        }

        throw new ArgumentException(
            $"'{expression}' is not a member of {typeof(T).Name}. Map metadata onto a property or field "
            + "of the document itself, e.g. x => x.DeletedAt.", nameof(expression));
    }
}
