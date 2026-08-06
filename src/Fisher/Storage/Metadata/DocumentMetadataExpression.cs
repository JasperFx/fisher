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
    }

    /// <inheritdoc cref="DocumentMetadata.Version" />
    public MetadataColumnExpression<T, Guid> Version { get; }

    /// <inheritdoc cref="DocumentMetadata.LastModified" />
    public MetadataColumnExpression<T, DateTimeOffset> LastModified { get; }

    /// <inheritdoc cref="DocumentMetadata.IsSoftDeleted" />
    public MetadataColumnExpression<T, bool> IsSoftDeleted { get; }

    /// <inheritdoc cref="DocumentMetadata.DeletedAt" />
    public MetadataColumnExpression<T, DateTimeOffset?> DeletedAt { get; }
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
