using Fisher.Storage.FullText;

namespace Fisher.Attributes;

/// <summary>
///     Include this member in the document's full-text index, or — on the type — index the whole
///     stored document (fisher#215).
/// </summary>
/// <remarks>
///     <para>
///         The declarative form of <c>Schema.For&lt;T&gt;().FullTextIndex(...)</c>, and it produces
///         exactly that. Marten's attribute of the same name works on both a type and its members in
///         the same way.
///     </para>
///     <para>
///         <b>Every member carrying it joins one index</b>, because Fisher has one full-text index per
///         document type — see <see cref="Fisher.Storage.FullText.FullTextIndex" /> for why. Members
///         are indexed in declaration order, which is the order their FTS5 columns appear in and
///         therefore the order <c>bm25()</c> would weight them.
///     </para>
///     <para>
///         The tokenizer is a property rather than a constructor argument so that member-level
///         attributes can leave it alone; where two of them disagree the mapping refuses by name
///         rather than picking one.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Class)]
public sealed class FullTextIndexAttribute : Attribute
{
    /// <summary>
    ///     How the index breaks text into terms. Defaults to
    ///     <see cref="FullTextTokenizer.Porter" />.
    /// </summary>
    public FullTextTokenizer Tokenizer { get; set; } = FullTextTokenizer.Porter;
}
