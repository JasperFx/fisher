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
