using System.Text;

namespace Fisher.Linq.Parsing.Methods;

/// <summary>
///     Turns each search operator's argument into the FTS5 query string it means.
/// </summary>
/// <remarks>
///     <para>
///         Marten hands its five operators to five PostgreSQL functions — <c>to_tsquery</c>,
///         <c>plainto_tsquery</c>, <c>phraseto_tsquery</c>, <c>websearch_to_tsquery</c> — and does no
///         parsing of its own. FTS5 has one query language and no such functions, so the difference
///         between "these words in any order" and "this phrase" has to be expressed <em>in</em> that
///         language, which is what this does.
///     </para>
///     <para>
///         <b>Everything is quoted on the way through.</b> An FTS5 bareword is a token and an operator
///         both, so an unquoted user term containing <c>OR</c>, <c>NOT</c>, <c>NEAR</c>, <c>*</c> or a
///         quotation mark either changes the query's meaning or makes it a syntax error. Quoting is
///         what makes every operator but <see cref="Fisher.Linq.FullTextSearchExtensions.Search{T}" />
///         safe to hand raw user input — and <c>Search</c> is the one that documents the opposite.
///     </para>
///     <para>
///         The query is bound as a parameter, so none of this is a SQL-injection boundary; what it is
///         is an <em>FTS5-syntax</em> boundary, where the failure is a wrong answer rather than a
///         breach.
///     </para>
/// </remarks>
internal static class Fts5Query
{
    /// <summary>Every word ANDed, order-independent — <c>PlainTextSearch</c>.</summary>
    public static string PlainText(string term)
        => string.Join(" AND ", Words(term).Select(Quote));

    /// <summary>The words adjacent and in order — <c>PhraseSearch</c>.</summary>
    public static string Phrase(string term)
    {
        var words = Words(term);
        return words.Count == 0 ? "" : Quote(string.Join(' ', words));
    }

    /// <summary>Every word as a prefix, ANDed — <c>PrefixSearch</c>.</summary>
    /// <remarks>
    ///     <c>"word"*</c> rather than <c>word*</c>: the quoted form is a prefix of a quoted string in
    ///     FTS5's grammar, so it takes the same escaping as every other term rather than needing a
    ///     second rule.
    /// </remarks>
    public static string Prefix(string term)
        => string.Join(" AND ", Words(term).Select(word => Quote(word) + "*"));

    /// <summary>The whole term as one phrase, for a trigram index — <c>NgramSearch</c>.</summary>
    public static string Ngram(string term) => Quote(term.Trim());

    /// <summary>
    ///     Quoted phrases, <c>or</c> between alternatives, <c>-</c> to exclude — <c>WebStyleSearch</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Deliberately cannot be malformed: anything the grammar does not recognise is a word to
    ///         search for. That is the property that makes it the right operator to point a search box
    ///         at, and it is why it does not simply forward to
    ///         <see cref="Fisher.Linq.FullTextSearchExtensions.Search{T}" />.
    ///     </para>
    ///     <para>
    ///         <b>A query of nothing but exclusions is refused.</b> "Everything except X" is what it
    ///         would mean, and FTS5's <c>NOT</c> is binary — it narrows a left-hand result set and has
    ///         no unary form — so there is no query to build. Postgres's <c>websearch_to_tsquery</c>
    ///         produces one that matches nothing at all, which is a worse answer than saying so.
    ///     </para>
    /// </remarks>
    public static string WebStyle(string term)
    {
        var include = new List<string>();
        var exclude = new List<string>();
        var alternatives = new List<string>();

        foreach (var token in WebStyleTokens(term))
        {
            if (token.Equals("or", StringComparison.OrdinalIgnoreCase) && include.Count > 0)
            {
                alternatives.Add(include[^1]);
                include.RemoveAt(include.Count - 1);
                continue;
            }

            var text = token;
            var negated = text.StartsWith('-') && text.Length > 1;

            if (negated)
            {
                text = text[1..];
            }

            var quoted = Quote(text.Trim('"'));

            if (negated)
            {
                exclude.Add(quoted);
            }
            else if (alternatives.Count > 0)
            {
                // The token after an `or` joins whatever preceded it, and any further `or` extends
                // the same group rather than starting a new one.
                alternatives.Add(quoted);
                include.Add("(" + string.Join(" OR ", alternatives) + ")");
                alternatives.Clear();
            }
            else
            {
                include.Add(quoted);
            }
        }

        // A trailing `or` with nothing after it: keep what it was going to join rather than dropping
        // the term the caller did type.
        include.AddRange(alternatives);

        if (include.Count == 0)
        {
            return exclude.Count == 0
                ? ""
                : throw new BadLinqExpressionException(
                    "WebStyleSearch cannot run a query of only exclusions. FTS5's NOT narrows a "
                    + "result set rather than negating one, so there is nothing for it to narrow — "
                    + "add at least one term to search for.");
        }

        var positive = string.Join(" AND ", include);

        return exclude.Count == 0
            ? positive
            : $"({positive}) NOT ({string.Join(" OR ", exclude)})";
    }

    /// <summary>
    ///     Splits on whitespace, discarding everything else. Punctuation goes because FTS5's default
    ///     tokenizers drop it when indexing too, so keeping it could only produce a term that cannot
    ///     match.
    /// </summary>
    private static List<string> Words(string term)
        => term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();

    /// <summary>
    ///     Web-style tokens: a double-quoted run is one token, everything else splits on whitespace.
    ///     An unterminated quote runs to the end of the input rather than being an error.
    /// </summary>
    private static IEnumerable<string> WebStyleTokens(string term)
    {
        var current = new StringBuilder();
        var quoted = false;

        foreach (var character in term)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    /// <summary>
    ///     An FTS5 string literal. Double quotes delimit it and a doubled quote escapes one, which is
    ///     the whole of the escaping rule.
    /// </summary>
    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
