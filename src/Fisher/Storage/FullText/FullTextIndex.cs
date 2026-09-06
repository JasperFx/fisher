using System.Reflection;
using Fisher.Linq.Members;

namespace Fisher.Storage.FullText;

/// <summary>
///     One document type's full-text index declaration
///     (<a href="https://github.com/JasperFx/fisher/issues/215">fisher#215</a>).
/// </summary>
/// <remarks>
///     <para>
///         <b>One per document type, and a second declaration is refused.</b> Marten permits several
///         and carries an <c>AmbiguousFullTextIndexException</c> for the case where a search cannot
///         tell which one it meant. Fisher takes the other branch: a search operator names no index,
///         so with one index there is nothing to disambiguate and the exception has no reason to
///         exist. Declaring a second is a configuration error and says so.
///     </para>
///     <para>
///         <b>Naming no member indexes the whole stored document</b>, which is the same thing Marten's
///         member-less <c>FullTextIndex()</c> does — its index is over <c>data::text</c>. Worth being
///         explicit that this includes the JSON's <em>key names</em>: a document with a
///         <c>"title"</c> property makes <c>title</c> a matchable term. That is noise rather than
///         wrongness, and naming the members is how you avoid it.
///     </para>
/// </remarks>
internal sealed class FullTextIndex
{
    internal FullTextIndex(MemberInfo[][] memberChains, FullTextTokenizer tokenizer)
    {
        MemberChains = memberChains;
        Tokenizer = tokenizer;
    }

    /// <summary>One member chain per indexed member, or empty for the whole document.</summary>
    internal MemberInfo[][] MemberChains { get; }

    internal FullTextTokenizer Tokenizer { get; }

    /// <summary>Whether the index covers the stored JSON rather than named members.</summary>
    internal bool IsWholeDocument => MemberChains.Length == 0;

    /// <summary>
    ///     The member names, for the equality check that makes a repeated declaration idempotent.
    /// </summary>
    internal string[] MemberNames
        => Array.ConvertAll(MemberChains, chain => string.Join(".", chain.Select(x => x.Name)));

    /// <summary>
    ///     The FTS5 column name for each indexed member — snake case, joined by underscores, the same
    ///     convention a duplicated field's column follows.
    /// </summary>
    /// <remarks>
    ///     These are the names a caller writes in a column-scoped query (<c>title:fox</c>), so they
    ///     have to be predictable from the member. The whole-document index has one column called
    ///     <c>data</c>, matching the document table's own column, so that a column-scoped query
    ///     against it reads as what it is.
    /// </remarks>
    internal string[] ColumnNames
        => IsWholeDocument
            ? ["data"]
            : Array.ConvertAll(MemberChains, DuplicatedField.DefaultColumnNameFor);

    /// <summary>
    ///     The SQL expression producing each indexed column's text, against the document table.
    /// </summary>
    /// <remarks>
    ///     <b>The member's own <c>RawLocator</c>, not a hand-written <c>json_extract</c></b> — the
    ///     same discipline <see cref="DocumentIndex" /> records, and for a related reason: the locator
    ///     is what the serializer's naming policy produced, so a hand-built path would silently index
    ///     nothing for every camelCase member. <c>RawLocator</c> rather than <c>TypedLocator</c>
    ///     because full text wants the stored text, and a typed locator wraps a timestamp in
    ///     <c>strftime</c> — which is right for ordering and meaningless to tokenize.
    /// </remarks>
    internal string[] Expressions(MemberFactory members, string dataColumn)
        => IsWholeDocument
            ? [dataColumn]
            : Array.ConvertAll(MemberChains, chain => members.ResolveMember(chain).RawLocator);
}
