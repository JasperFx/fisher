using System.Reflection;

namespace Fisher.Storage;

/// <summary>
///     A user-declared index over one or more document members, created as a SQLite <b>expression
///     index</b> rather than as an index over a column.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is the divergence from Marten and Polecat, and it makes the feature cheaper here
///         than on either.</b> Neither can index into a JSON body directly in the general case, so an
///         index over an undulicated member means first materialising a column to index — Marten's
///         computed index, Polecat's <c>JSON_VALUE</c> index. SQLite has indexed expressions since
///         3.9, with the single restriction that the expression be deterministic, and
///         <c>json_extract</c> is. So Fisher indexes the member where it lives and adds nothing to the
///         table's shape at all.
///     </para>
///     <para>
///         That makes <see cref="DuplicatedField" /> and this two genuinely different things rather
///         than near-duplicates:
///     </para>
///     <list type="bullet">
///         <item>
///             <b><c>Duplicate</c></b> materialises a <c>VIRTUAL</c> generated column <em>and</em>
///             indexes it. Use it when the member should also be a column something else can name.
///         </item>
///         <item>
///             <b><c>Index</c></b> indexes the expression only. No column, no change to the table's
///             shape, and nothing to declare an affinity for.
///         </item>
///     </list>
///     <para>
///         <b>The indexed expression is the member's own <c>TypedLocator</c></b>, taken from the same
///         <see cref="Linq.Members.MemberFactory" /> a query goes through — never a hand-written
///         <c>json_extract</c>. SQLite's planner only uses an expression index when the query's
///         expression matches the index's, so an index built any other way is created successfully,
///         never used, and reports nothing. A timestamp is the case that proves it: its locator is
///         fisher#1's <c>strftime</c> wrapper, so an index over the bare <c>json_extract</c> would
///         never serve the very predicates a timestamp index exists for.
///     </para>
/// </remarks>
internal sealed class DocumentIndex
{
    internal DocumentIndex(MemberInfo[][] memberChains, string? name, bool isUnique)
    {
        MemberChains = memberChains;
        Name = name;
        IsUnique = isUnique;
    }

    /// <summary>One member chain per indexed member, in index order.</summary>
    internal MemberInfo[][] MemberChains { get; }

    /// <summary>An explicit index name, or null to derive one from the members.</summary>
    internal string? Name { get; }

    internal bool IsUnique { get; }

    /// <summary>
    ///     The member names this index is derived from, which is how two registrations of the same
    ///     index are told apart from a genuine name collision.
    /// </summary>
    /// <remarks>
    ///     Compared by name rather than <see cref="MemberInfo" /> identity, for the same reason
    ///     <see cref="DuplicatedField.MemberNames" /> is: one property reached through a derived type
    ///     and through its declaring type yields two <c>MemberInfo</c>s that need not be equal, while
    ///     the JSON path — all either is used for — is the same.
    /// </remarks>
    internal string[] MemberNames
        => Array.ConvertAll(MemberChains, chain => string.Join(".", chain.Select(x => x.Name)));

    /// <summary>
    ///     The default index-name suffix — <c>Name</c> becomes <c>name</c>, and
    ///     <c>Address.City</c> plus <c>Name</c> becomes <c>address_city_name</c>.
    /// </summary>
    /// <remarks>
    ///     Snake case joined with underscores, so a user-declared index reads like the duplicated-column
    ///     indexes it sits beside in <c>sqlite_master</c> rather than announcing which of the two
    ///     mechanisms created it.
    /// </remarks>
    internal string DefaultNameSuffix()
        => string.Join("_", MemberChains.Select(DuplicatedField.DefaultColumnNameFor));
}
