using System.Linq.Expressions;
namespace Fisher.Linq;

/// <summary>
///     Full-text search operators over SQLite's FTS5
///     (<a href="https://github.com/JasperFx/fisher/issues/215">fisher#215</a>).
/// </summary>
/// <remarks>
///     <para>
///         Each is a marker: it is only meaningful inside a <c>Where</c> on a Fisher query, and
///         calling one anywhere else throws rather than returning a plausible answer. That is Marten's
///         shape for the same family, and the reason is the same — the method has no in-memory
///         semantics to fall back to, because the answer is the index's.
///     </para>
///     <para>
///         All six are called on the <em>document</em> rather than on a member —
///         <c>Where(x =&gt; x.Search("fox"))</c> — because the index covers the members its
///         declaration named and the query does not get to pick among them. To search one column,
///         name it in the query text: <c>x.Search("title: fox")</c>.
///     </para>
///     <para>
///         <b>Every one of them requires the document type to declare a full-text index</b>, and a
///         query against a type with none is refused by name at translation time. Without that check
///         the search would be valid SQL against a table that does not exist — or worse, once the
///         wrong tokenizer is involved, valid SQL returning nothing.
///     </para>
/// </remarks>
public static class FullTextSearchExtensions
{
    /// <summary>
    ///     Search using FTS5's own query syntax, passed through unaltered.
    /// </summary>
    /// <param name="document">The document being searched. Never read.</param>
    /// <param name="searchTerm">
    ///     An FTS5 query: bare terms are ANDed, <c>"…"</c> is a phrase, <c>*</c> is a trailing
    ///     wildcard, <c>OR</c> / <c>NOT</c> / <c>NEAR(…)</c> and <c>column:</c> prefixes all apply.
    /// </param>
    /// <remarks>
    ///     The counterpart of Marten's <c>Search</c>, which passes raw <c>to_tsquery</c> lexemes
    ///     through in the same way. <b>The syntax is the caller's responsibility</b>: an unbalanced
    ///     quote or a stray operator is a malformed query, and Fisher reports it as one rather than
    ///     silently matching nothing. The other five operators exist so that a caller handling user
    ///     input never has to escape anything.
    /// </remarks>
    public static bool Search<T>(this T document, string searchTerm)
        => throw OnlyInAQuery(nameof(Search));

    /// <summary>
    ///     Search for every word in the term, in any order, with no query syntax at all.
    /// </summary>
    /// <remarks>
    ///     The safe default for user input, and Marten's <c>PlainTextSearch</c>
    ///     (<c>plainto_tsquery</c>). Each word is quoted before it reaches FTS5, so a term containing
    ///     <c>OR</c>, <c>*</c> or a quotation mark is searched for literally rather than reinterpreted
    ///     as an operator.
    /// </remarks>
    public static bool PlainTextSearch<T>(this T document, string searchTerm)
        => throw OnlyInAQuery(nameof(PlainTextSearch));

    /// <summary>
    ///     Search for the words as a phrase — adjacent and in order.
    /// </summary>
    /// <remarks>Marten's <c>PhraseSearch</c> (<c>phraseto_tsquery</c>).</remarks>
    public static bool PhraseSearch<T>(this T document, string searchTerm)
        => throw OnlyInAQuery(nameof(PhraseSearch));

    /// <summary>
    ///     Search using the syntax a web search box has trained everyone to expect: quoted phrases,
    ///     <c>or</c> between alternatives, and a leading <c>-</c> to exclude.
    /// </summary>
    /// <remarks>
    ///     Marten's <c>WebStyleSearch</c> (<c>websearch_to_tsquery</c>). Unlike
    ///     <see cref="Search{T}" /> this cannot be malformed — anything it does not recognise is a
    ///     word to search for, which is the property that makes it safe to hand a search box's raw
    ///     contents.
    /// </remarks>
    public static bool WebStyleSearch<T>(this T document, string searchTerm)
        => throw OnlyInAQuery(nameof(WebStyleSearch));

    /// <summary>
    ///     Search treating each word as a prefix, so <c>Priced</c> matches
    ///     <c>PricedIdeaScreening</c>.
    /// </summary>
    /// <remarks>
    ///     Marten's <c>PrefixSearch</c>. It matches from the <em>start</em> of a term only — for a
    ///     match anywhere inside one, see <see cref="NgramSearch{T}" />.
    /// </remarks>
    public static bool PrefixSearch<T>(this T document, string searchTerm)
        => throw OnlyInAQuery(nameof(PrefixSearch));

    /// <summary>
    ///     Substring search: match the term anywhere inside a word.
    /// </summary>
    /// <remarks>
    ///     <b>Requires an index declared with <see cref="Storage.FullText.FullTextTokenizer.Trigram" />
    ///     and is refused by name against any other.</b> A word tokenizer stores whole terms and
    ///     physically cannot match a fragment of one, so this against a Porter index is not a slow
    ///     query — it is an empty result that looks like an answer. Marten reaches the same capability
    ///     through a separate ngram index and has the same requirement.
    ///     <para>
    ///         Terms shorter than three characters cannot match a trigram index and are refused too,
    ///         for the same reason.
    ///     </para>
    /// </remarks>
    public static bool NgramSearch<T>(this T document, string searchTerm)
        => throw OnlyInAQuery(nameof(NgramSearch));

    /// <summary>
    ///     Order the results by FTS5 relevance — <c>bm25()</c> over the index the query's full-text
    ///     predicate searched (<a href="https://github.com/JasperFx/fisher/issues/220">fisher#220</a>).
    /// </summary>
    /// <param name="source">A query that already carries a full-text predicate.</param>
    /// <param name="columnWeights">
    ///     Optional per-column weights, in the order the index declared its members. Omit them for
    ///     FTS5's default, which weights every column at 1.0. Supplying a different number of weights
    ///     than the index has columns is refused rather than padded.
    /// </param>
    /// <remarks>
    ///     <para>
    ///     <b>Most relevant first.</b> FTS5's <c>bm25()</c> returns a score that is more NEGATIVE the
    ///     better the match, so a raw ascending sort is already best-first and this reads the way it
    ///     looks. <see cref="ThenByRelevanceDescending{T}" /> exists for the rare worst-first case
    ///     rather than leaving callers to discover the sign for themselves.
    ///     </para>
    ///     <para>
    ///     <b>It composes.</b> Relevance is an ordering term like any other, so
    ///     <c>OrderByRelevance().ThenByDescending(x =&gt; x.Published)</c> breaks ties by date, and
    ///     <c>OrderBy(x =&gt; x.Category).ThenByRelevance()</c> ranks within each category. It does not
    ///     replace an ordering already in the chain.
    ///     </para>
    ///     <para>
    ///     <b>It requires a full-text predicate in the same query</b>, and is refused at translation
    ///     time without one. <c>bm25()</c> is only legal where its table is the subject of a MATCH, so
    ///     the alternative is a SQLite error a long way from the call site.
    ///     </para>
    /// </remarks>
    public static IOrderedQueryable<T> OrderByRelevance<T>(this IQueryable<T> source,
        params double[] columnWeights)
        => Rank(source, nameof(OrderByRelevance), columnWeights);

    /// <inheritdoc cref="OrderByRelevance{T}" />
    /// <summary>Worst match first. The inverse of <see cref="OrderByRelevance{T}" />.</summary>
    public static IOrderedQueryable<T> OrderByRelevanceDescending<T>(this IQueryable<T> source,
        params double[] columnWeights)
        => Rank(source, nameof(OrderByRelevanceDescending), columnWeights);

    /// <inheritdoc cref="OrderByRelevance{T}" />
    /// <summary>Break an existing ordering's ties by relevance.</summary>
    public static IOrderedQueryable<T> ThenByRelevance<T>(this IOrderedQueryable<T> source,
        params double[] columnWeights)
        => Rank(source, nameof(ThenByRelevance), columnWeights);

    /// <inheritdoc cref="OrderByRelevance{T}" />
    /// <summary>Break an existing ordering's ties by relevance, worst match first.</summary>
    public static IOrderedQueryable<T> ThenByRelevanceDescending<T>(this IOrderedQueryable<T> source,
        params double[] columnWeights)
        => Rank(source, nameof(ThenByRelevanceDescending), columnWeights);

    /// <summary>
    ///     The matching fragment of the indexed text, with the matched terms marked — FTS5's
    ///     <c>snippet()</c> (<a href="https://github.com/JasperFx/fisher/issues/220">fisher#220</a>).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>Only inside a <c>Select</c>, on a query that carries a full-text predicate.</b> It is a
    ///     projected column rather than a document member — the value is computed by the match, so
    ///     there is nothing on the document to read it from, and no in-memory meaning to fall back to.
    ///     </para>
    ///     <code>
    ///     session.Query&lt;Article&gt;()
    ///         .Where(x =&gt; x.Search("corrosion"))
    ///         .Select(x =&gt; new { x.Title, Extract = x.Snippet() })
    ///     </code>
    ///     <para>
    ///     Takes the best-matching column, which is FTS5's own <c>-1</c>. Defaults are
    ///     <c>&lt;b&gt;</c>/<c>&lt;/b&gt;</c> markers, <c>…</c> for the elision and 32 tokens.
    ///     </para>
    /// </remarks>
    public static string Snippet<T>(this T document)
        => throw OnlyInASelect(nameof(Snippet));

    /// <inheritdoc cref="Snippet{T}(T)" />
    /// <param name="document">The document being projected. Never read.</param>
    /// <param name="startMarker">Written before each matched term.</param>
    /// <param name="endMarker">Written after each matched term.</param>
    /// <param name="ellipsis">Written where text was elided.</param>
    /// <param name="maxTokens">Length budget, in tokens. FTS5 caps this at 64.</param>
    public static string Snippet<T>(this T document, string startMarker, string endMarker,
        string ellipsis, int maxTokens)
        => throw OnlyInASelect(nameof(Snippet));

    /// <summary>
    ///     One indexed column in full, with the matched terms marked — FTS5's <c>highlight()</c>
    ///     (<a href="https://github.com/JasperFx/fisher/issues/220">fisher#220</a>).
    /// </summary>
    /// <param name="document">The document being projected. Never read.</param>
    /// <param name="column">
    ///     Which indexed member to highlight, named as the member is. <b>Required, unlike
    ///     <see cref="Snippet{T}(T)" />'s.</b> FTS5 accepts <c>-1</c> here and returns an EMPTY STRING
    ///     for it rather than an error, so a default of "whichever column matched" would be a silent
    ///     wrong answer, and a default of "the first one" would be a wrong answer whenever the match
    ///     was elsewhere.
    /// </param>
    public static string Highlight<T>(this T document, string column)
        => throw OnlyInASelect(nameof(Highlight));

    /// <inheritdoc cref="Highlight{T}(T,string)" />
    public static string Highlight<T>(this T document, string column, string startMarker,
        string endMarker)
        => throw OnlyInASelect(nameof(Highlight));

    private static NotSupportedException OnlyInASelect(string name)
        => new($"'{name}' is only meaningful inside a Fisher LINQ query's Select — "
               + "session.Query<T>().Where(x => x.Search(\"…\")).Select(x => new { Value = x."
               + name + "() }). It reads a value the full-text match computes, so it has no "
               + "in-memory equivalent and nothing on the document to fall back to.");

    /// <summary>
    ///     Rebuilds the call as an expression node the provider's parser sees, which is what makes
    ///     these ordinary members of the ordering chain rather than a terminal that has to be last.
    /// </summary>
    private static IOrderedQueryable<T> Rank<T>(IQueryable<T> source, string method, double[] weights)
    {
        var call = Expression.Call(
            typeof(FullTextSearchExtensions),
            method,
            [typeof(T)],
            source.Expression,
            Expression.Constant(weights ?? []));

        return (IOrderedQueryable<T>)source.Provider.CreateQuery<T>(call);
    }

    private static NotSupportedException OnlyInAQuery(string name)
        => new($"'{name}' is only meaningful inside a Fisher LINQ query — "
               + "session.Query<T>().Where(x => x." + name + "(\"…\")). It has no in-memory "
               + "equivalent, because the answer comes from the full-text index rather than from the "
               + "document.");
}
