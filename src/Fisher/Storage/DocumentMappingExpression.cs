using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Fisher.Storage;

/// <summary>
///     Configuration for one document type, typed so a member can be named with a lambda.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="DocumentMapping" /> itself is deliberately non-generic — it is reached from the
///         storage, the schema and the LINQ layer by <see cref="Type" />, none of which has a type
///         parameter to spare. But <c>Duplicate(x =&gt; x.Name)</c> cannot infer its document type from
///         a lambda alone, so the receiver has to carry it. Marten resolves this the same way, with
///         <c>MartenRegistry.DocumentMappingExpression&lt;T&gt;</c>.
///     </para>
///     <para>
///         Everything not needing the type parameter stays on <see cref="Mapping" />, which is public,
///         rather than being mirrored here — one place to configure a thing, and it is the mapping.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2070:UnrecognizedReflectionPattern",
    Justification =
        "Class-level: resolves the members named by a caller's own lambda. The document type and the members reached through it are preserved at the registration boundary on the caller side.")]
public class DocumentMappingExpression<T> where T : notnull
{
    internal DocumentMappingExpression(DocumentMapping mapping)
    {
        Mapping = mapping;
    }

    /// <summary>The mapping this configures.</summary>
    public DocumentMapping Mapping { get; }

    /// <summary>
    ///     Flag this document type for soft deletion — <c>Delete</c> sets <c>is_deleted</c> rather than
    ///     removing the row.
    /// </summary>
    public DocumentMappingExpression<T> SoftDeleted()
    {
        Mapping.SoftDeleted();
        return this;
    }

    /// <summary>
    ///     Guard writes of this type with an optimistic concurrency check against its
    ///     <c>guid_version</c> column.
    /// </summary>
    public DocumentMappingExpression<T> UseOptimisticConcurrency(bool enabled = true)
    {
        Mapping.UseOptimisticConcurrency = enabled;
        return this;
    }

    /// <summary>
    ///     Give each row a tenant id, and make the primary key the tenant/id pair.
    /// </summary>
    public DocumentMappingExpression<T> MultiTenanted()
    {
        Mapping.TenancyStyle = JasperFx.MultiTenancy.TenancyStyle.Conjoined;
        return this;
    }

    /// <summary>
    ///     Override the short name the table is built from — <c>fi_doc_&lt;alias&gt;</c>.
    /// </summary>
    public DocumentMappingExpression<T> DocumentAlias(string alias)
    {
        Mapping.Alias = alias;
        return this;
    }

    /// <summary>
    ///     Lift a member out of the JSON body into a column of its own, and index it, so that queries
    ///     comparing against it can use that index.
    /// </summary>
    /// <param name="member">The member, e.g. <c>x =&gt; x.Name</c> or <c>x =&gt; x.Address.City</c>.</param>
    /// <param name="columnName">
    ///     An explicit column name. Defaults to the member chain lowercased and joined with
    ///     underscores.
    /// </param>
    /// <param name="columnType">
    ///     An explicit SQLite type. Defaults to one derived from the member's CLR type — see
    ///     <see cref="DuplicatedField.SqliteTypeFor" /> for why the default matters more than it looks.
    /// </param>
    /// <param name="index">Whether to create an index over the column. On by default; that is the point.</param>
    /// <remarks>
    ///     The column is a <c>VIRTUAL</c> generated column computed from <c>data</c>, so this can be
    ///     added to a document type that already has rows and every one of them is correct at once —
    ///     there is nothing to backfill. See <see cref="DuplicatedField" /> for the rest of that
    ///     decision.
    /// </remarks>
    public DocumentMappingExpression<T> Duplicate<TValue>(Expression<Func<T, TValue>> member,
        string? columnName = null, string? columnType = null, bool index = true)
    {
        ArgumentNullException.ThrowIfNull(member);

        Mapping.Duplicate(ChainOf(member), columnName, columnType, index);
        return this;
    }

    /// <summary>
    ///     Walk a member-access lambda back to its parameter, outermost member last.
    /// </summary>
    /// <remarks>
    ///     A value-typed member arrives wrapped in a <c>Convert</c> when the lambda's return type is
    ///     wider than the member's, which is why the boxing conversion is stripped before the walk.
    /// </remarks>
    private static MemberInfo[] ChainOf<TValue>(Expression<Func<T, TValue>> expression)
    {
        var body = expression.Body;

        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression member)
        {
            throw new ArgumentException(
                $"'{expression}' is not a member of {typeof(T).Name}. Duplicate a property or field, "
                + "e.g. x => x.Name.", nameof(expression));
        }

        var chain = new List<MemberInfo>();
        MemberExpression? current = member;

        while (current is not null)
        {
            chain.Insert(0, current.Member);

            if (current.Expression is ParameterExpression)
            {
                return chain.ToArray();
            }

            current = current.Expression as MemberExpression;
        }

        throw new ArgumentException(
            $"'{expression}' does not resolve to a member of the document itself.", nameof(expression));
    }
}
