using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Fisher.Storage.Metadata;

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
    ///     Guard writes of this type with a numeric revision instead of a Guid version.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The alternative to <see cref="UseOptimisticConcurrency" />, not an addition to it — a
    ///         document type carries <c>guid_version</c> or <c>revision</c>, and asking for both is
    ///         refused when the type's storage is built.
    ///     </para>
    ///     <para>
    ///         What a revision buys over a Guid version is that it is readable: it can cross an API
    ///         boundary, be shown to a user, and be named on the way back in through
    ///         <c>Store(document, revision)</c> or <c>UpdateRevision(document, revision)</c>. Implementing
    ///         <see cref="JasperFx.IRevisioned" /> turns this on and maps the revision onto the
    ///         document's own <c>Version</c> member.
    ///     </para>
    /// </remarks>
    public DocumentMappingExpression<T> UseNumericRevisions(bool enabled = true)
    {
        Mapping.UseNumericRevisions = enabled;
        return this;
    }

    /// <summary>
    ///     Project Fisher's metadata columns onto members of the document — <c>last_modified</c> onto a
    ///     timestamp of your own, <c>deleted_at</c> onto something other than
    ///     <see cref="JasperFx.Metadata.ISoftDeleted.DeletedAt" />, and so on.
    /// </summary>
    /// <remarks>
    ///     Runs after the interface and attribute conventions and overrides them, because this is the
    ///     one of the three that is unambiguously a decision rather than an inference.
    /// </remarks>
    public DocumentMappingExpression<T> Metadata(Action<DocumentMetadataExpression<T>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        configure(new DocumentMetadataExpression<T>(Mapping.Metadata));
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
    ///     Index a member where it lives, without lifting it into a column.
    /// </summary>
    /// <param name="member">The member, e.g. <c>x =&gt; x.Name</c> or <c>x =&gt; x.Address.City</c>.</param>
    /// <param name="name">An explicit index name. Defaults to one derived from the member chain.</param>
    /// <param name="unique">Whether to create a <c>UNIQUE</c> index.</param>
    /// <remarks>
    ///     <para>
    ///         A SQLite expression index over the member's <c>json_extract</c> locator — so unlike
    ///         <see cref="Duplicate{TValue}" /> this adds no column and does not change the table's
    ///         shape. Prefer it when all you want is the index; prefer <c>Duplicate</c> when the member
    ///         should also be a column something else can name.
    ///     </para>
    ///     <para>
    ///         Indexing a member that is <em>also</em> duplicated indexes the generated column rather
    ///         than the expression, because that is what a query against it emits — but a duplicated
    ///         field is indexed by default already, so saying both is usually redundant.
    ///     </para>
    /// </remarks>
    public DocumentMappingExpression<T> Index<TValue>(Expression<Func<T, TValue>> member,
        string? name = null, bool unique = false)
    {
        ArgumentNullException.ThrowIfNull(member);

        Mapping.Index([ChainOf(member)], name, unique);
        return this;
    }

    /// <summary>
    ///     Index several members together, as one composite index in the order given.
    /// </summary>
    /// <remarks>
    ///     Order matters exactly as it does for any B-tree index: SQLite can use a leading subset of the
    ///     indexed expressions and not a trailing one.
    /// </remarks>
    public DocumentMappingExpression<T> Index(Expression<Func<T, object?>>[] members,
        string? name = null, bool unique = false)
    {
        ArgumentNullException.ThrowIfNull(members);

        if (members.Length == 0)
        {
            throw new ArgumentException("An index needs at least one member.", nameof(members));
        }

        Mapping.Index(Array.ConvertAll(members, ChainOf), name, unique);
        return this;
    }

    /// <summary>
    ///     Index a member and require its values to be distinct.
    /// </summary>
    /// <remarks>
    ///     A <c>UNIQUE</c> index over a member that is absent from some documents does not constrain
    ///     those: <c>json_extract</c> yields SQL NULL for a missing key, and SQLite treats NULLs as
    ///     distinct from one another in a unique index. So this constrains the documents that have the
    ///     member, which is what the equivalent does on both siblings.
    /// </remarks>
    public DocumentMappingExpression<T> UniqueIndex<TValue>(Expression<Func<T, TValue>> member,
        string? name = null)
        => Index(member, name, unique: true);

    /// <inheritdoc cref="UniqueIndex{TValue}(Expression{Func{T, TValue}}, string?)" />
    public DocumentMappingExpression<T> UniqueIndex(Expression<Func<T, object?>>[] members,
        string? name = null)
        => Index(members, name, unique: true);

    /// <summary>
    ///     Register a sub-class, so <typeparamref name="T" /> and its sub-classes share one table.
    /// </summary>
    /// <param name="alias">
    ///     The value stored in <c>doc_type</c>. Defaults to the type name in snake case; name it
    ///     explicitly if the type may be renamed, because the alias is what is stored.
    /// </param>
    /// <remarks>
    ///     <c>Store(derived)</c> and <c>LoadAsync&lt;TBase&gt;(id)</c> then share a table,
    ///     <c>Query&lt;TBase&gt;()</c> returns every sub-class as its own type, and
    ///     <c>Query&lt;TDerived&gt;()</c> narrows to one. An abstract or interface base is a hierarchy
    ///     whether or not anything is registered, so its table carries the discriminator from the first
    ///     migration.
    /// </remarks>
    public DocumentMappingExpression<T> AddSubClass<TSub>(string? alias = null) where TSub : T
    {
        Mapping.AddSubClass(typeof(TSub), alias);
        return this;
    }

    /// <inheritdoc cref="AddSubClass{TSub}" />
    public DocumentMappingExpression<T> AddSubClass(Type subclassType, string? alias = null)
    {
        ArgumentNullException.ThrowIfNull(subclassType);

        Mapping.AddSubClass(subclassType, alias);
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
