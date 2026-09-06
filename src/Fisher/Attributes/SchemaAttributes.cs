namespace Fisher.Attributes;

/// <summary>
///     Index this member, so a predicate against it is served by an index rather than by computing
///     <c>json_extract</c> for every row (fisher#39).
/// </summary>
/// <remarks>
///     <para>
///         The declarative form of <c>Schema.For&lt;T&gt;().Index(x =&gt; x.Member)</c>, and it produces
///         exactly that — a SQLite expression index over the member's own locator, adding no column.
///         See <c>DocumentIndex</c> for why the locator rather than a hand-written <c>json_extract</c>
///         is what makes the index usable at all.
///     </para>
///     <para>
///         <b>Deliberately narrower than Polecat's, which carries <c>SortOrder</c>, <c>Casing</c> and
///         <c>SqlType</c>.</b> All three describe a <em>computed column</em>, which is what a
///         Polecat index is built over; a Fisher index is over an expression and has no column to
///         type, no casing to apply (SQLite's default collation is case-sensitive and the LINQ layer's
///         string operators are ordinal to match), and no direction worth naming — an index serves a
///         range scan in either direction. Carrying them here would be three knobs that silently do
///         nothing.
///     </para>
///     <para>
///         Applied when the mapping is created, after the JasperFx metadata interfaces and before the
///         DSL, so <c>Schema.For&lt;T&gt;()</c> still wins.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class IndexAttribute : Attribute
{
    /// <summary>
    ///     An explicit index name. Members sharing a name are grouped into one composite index, in
    ///     declaration order.
    /// </summary>
    public string? IndexName { get; set; }
}

/// <summary>
///     Index this member and require its values to be distinct — the declarative form of
///     <c>Schema.For&lt;T&gt;().UniqueIndex(x =&gt; x.Member)</c>.
/// </summary>
/// <remarks>
///     <b>A <c>UNIQUE</c> index does not constrain documents that lack the member</b>, because
///     <c>json_extract</c> yields SQL NULL for an absent key and SQLite treats NULLs in a unique index
///     as distinct. Same on both siblings; worth repeating here because the attribute reads like a
///     stronger promise than it is.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class UniqueIndexAttribute : IndexAttribute
{
}

/// <summary>
///     Lift this member into a generated column of its own and index it — the declarative form of
///     <c>Schema.For&lt;T&gt;().Duplicate(x =&gt; x.Member)</c>.
/// </summary>
/// <remarks>
///     Prefer <see cref="IndexAttribute" /> unless something needs the member to <em>be</em> a column:
///     an expression index adds nothing to the table, where this adds a <c>VIRTUAL</c> generated
///     column. Both are indexed, and a query against the member uses the index either way.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class DuplicateFieldAttribute : Attribute
{
    /// <summary>An explicit column name. Defaults to the member in snake case.</summary>
    public string? ColumnName { get; set; }

    /// <summary>An explicit SQLite type. Defaults to one derived from the member's CLR type.</summary>
    public string? ColumnType { get; set; }
}

/// <summary>
///     Configure the Hi-Lo sequence that assigns this document type's <c>int</c> or <c>long</c>
///     identity — the declarative form of <c>Schema.For&lt;T&gt;().Mapping.HiloSettings</c>.
/// </summary>
/// <remarks>
///     Ignored for a Guid or string identity, which needs no sequence — as the DSL form is, and for
///     the same reason. <see cref="SequenceName" /> is what makes two document types share one
///     allocation rather than each holding a private lo range.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class HiloSequenceAttribute : Attribute
{
    /// <summary>How many identities one allocation hands out. Zero leaves the store's default.</summary>
    public int MaxLo { get; set; }

    /// <summary>The sequence to draw from. Defaults to one per document type.</summary>
    public string? SequenceName { get; set; }
}

/// <summary>
///     The short name used to build this type's table name — the declarative form of
///     <c>Schema.For&lt;T&gt;().DocumentAlias("…")</c> (fisher#218).
/// </summary>
/// <remarks>
///     <para>
///         Marten's <c>[DocumentAlias]</c>, same shape and same job. The alias is what the table is
///         named from <em>and</em>, in a hierarchy, what a row's <c>doc_type</c> discriminator holds —
///         so it is stored data rather than presentation. Changing it on a populated store renames the
///         table and orphans the rows already written under the old discriminator.
///     </para>
///     <para>
///         The DSL still wins, being the layer that names the type in this store's own configuration.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
public sealed class DocumentAliasAttribute : Attribute
{
    public DocumentAliasAttribute(string alias) => Alias = alias;

    /// <summary>The alias.</summary>
    public string Alias { get; }
}

/// <summary>
///     Store this document type with a <c>tenant_id</c> column — the declarative form of
///     <c>Schema.For&lt;T&gt;().MultiTenanted()</c> (fisher#218).
/// </summary>
/// <remarks>
///     Conjoined tenancy: one table, a tenant column in the primary key, and every read and write
///     scoped to the session's tenant. Orthogonal to database-per-tenant, which is a store-level
///     choice (<c>StoreOptions.MultiTenantedDatabases</c>) and needs no per-type declaration.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
public sealed class MultiTenantedAttribute : Attribute;

/// <summary>
///     Guard writes to this document type with the <c>guid_version</c> column — the declarative form
///     of <c>Schema.For&lt;T&gt;().UseOptimisticConcurrency()</c> (fisher#218).
/// </summary>
/// <remarks>
///     Implementing <c>JasperFx.Metadata.IVersioned</c> does the same and additionally maps the
///     version onto a member; this is for a type that wants the guard without carrying the member.
///     The numeric alternative is <c>UseNumericRevisions()</c> / <c>JasperFx.IRevisioned</c>, and the
///     two are refused together.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
public sealed class UseOptimisticConcurrencyAttribute : Attribute;

/// <summary>
///     A real, enforced foreign key from this member to another document type's identity — the
///     declarative form of <c>Schema.For&lt;T&gt;().ForeignKey&lt;TOther&gt;(x =&gt; x.Member)</c>
///     (fisher#218).
/// </summary>
/// <remarks>
///     <para>
///         SQLite supports this completely — the constraint, <c>ON DELETE CASCADE</c> and
///         <c>ON DELETE SET NULL</c> — and Weasel's default profile turns <c>PRAGMA foreign_keys</c>
///         on for every connection Fisher opens, so a key declared here bites immediately.
///     </para>
///     <para>
///         <b>Declaring one duplicates the member as a side effect</b>, because a constraint needs a
///         real column and a document member lives in <c>data</c>. That column is a <c>VIRTUAL</c>
///         generated one, so it costs index space rather than row space and cannot drift.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class ForeignKeyAttribute : Attribute
{
    public ForeignKeyAttribute(Type referenceType) => ReferenceType = referenceType;

    /// <summary>The document type whose identity this member references.</summary>
    public Type ReferenceType { get; }

    /// <summary>What happens to this row when the referenced one is deleted.</summary>
    public Weasel.Core.CascadeAction OnDelete { get; set; } = Weasel.Core.CascadeAction.NoAction;

    /// <summary>An explicit name for the generated child column. Defaults to the member in snake case.</summary>
    public string? ColumnName { get; set; }
}
