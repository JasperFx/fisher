namespace Fisher.Storage.FullText;

/// <summary>
///     How an FTS5 index breaks text into the terms it stores.
/// </summary>
/// <remarks>
///     <para>
///         The tokenizer is fixed when the index is created and is what decides which searches can
///         possibly match — so a mismatch between it and the operator used against it is refused by
///         name rather than answered with an empty result. See
///         <see cref="Fisher.Linq.FullTextSearchExtensions" />.
///     </para>
///     <para>
///         SQLite's own list is longer (<c>ascii</c>, <c>unicode61</c>, <c>porter</c>, <c>trigram</c>)
///         and each takes arguments. What is offered here is the three that answer different
///         questions; the arguments are omitted because every one of them is a knob whose effect is
///         invisible until a search quietly stops matching.
///     </para>
/// </remarks>
public enum FullTextTokenizer
{
    /// <summary>
    ///     Unicode word splitting with diacritics folded away, and English stemming on top — so
    ///     <c>running</c> matches <c>run</c>. The default, and Marten's <c>english</c> text-search
    ///     configuration is the nearest equivalent.
    /// </summary>
    Porter,

    /// <summary>
    ///     Unicode word splitting with no stemming: terms match as written. Right for identifiers,
    ///     tags and any language English stemming would mangle.
    /// </summary>
    Unicode,

    /// <summary>
    ///     Three-character sliding windows, so a search matches anywhere <em>inside</em> a word.
    /// </summary>
    /// <remarks>
    ///     This is what makes <c>NgramSearch</c> work and it is the only tokenizer that does — a word
    ///     tokenizer stores whole terms and cannot match a fragment of one. It costs index space
    ///     (roughly one entry per character rather than per word) and it does not stem, so an index
    ///     declared this way is a substring index rather than a language-aware one.
    /// </remarks>
    Trigram
}

internal static class FullTextTokenizerExtensions
{
    /// <summary>
    ///     The <c>tokenize=</c> argument for a <c>CREATE VIRTUAL TABLE … USING fts5</c>.
    /// </summary>
    /// <remarks>
    ///     Rendered rather than parameterized because it is part of a DDL statement, and every value
    ///     comes from this enum rather than from a caller's string — which is the whole reason the
    ///     surface is an enum rather than the tokenizer name Marten's <c>regConfig</c> is. Marten has
    ///     to validate its equivalent against a regex precisely because it is a free string
    ///     interpolated into SQL.
    /// </remarks>
    public static string ToSql(this FullTextTokenizer tokenizer) => tokenizer switch
    {
        FullTextTokenizer.Porter => "porter unicode61",
        FullTextTokenizer.Unicode => "unicode61",
        FullTextTokenizer.Trigram => "trigram",
        _ => throw new ArgumentOutOfRangeException(nameof(tokenizer), tokenizer,
            "Unknown full-text tokenizer.")
    };
}
