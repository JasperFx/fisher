using System.Linq.Expressions;
using Fisher.Linq.Members;
using Fisher.Linq.SqlGeneration;
using Fisher.Storage.FullText;
using Weasel.Core.SqlGeneration;
using Weasel.Sqlite;

namespace Fisher.Linq.Parsing.Methods;

/// <summary>
///     Translates the six <see cref="FullTextSearchExtensions" /> operators into a
///     <see cref="FullTextMatchFilter" />.
/// </summary>
/// <remarks>
///     <para>
///         One parser for all six because they differ only in how the search term becomes an FTS5
///         query — see <see cref="Fts5Query" /> — and every one of them produces the same predicate
///         against the same index.
///     </para>
///     <para>
///         <b>The refusals are the point of this class.</b> Each of them is a query that would
///         otherwise be valid SQL returning no rows, which is indistinguishable from a search that
///         legitimately matched nothing: no index declared, the wrong tokenizer for the operator, or a
///         term too short for the tokenizer to have indexed. The house rule is to refuse loudly rather
///         than mis-translate, and full text is the place where a silent mis-translation is hardest to
///         notice.
///     </para>
/// </remarks>
internal sealed class FullTextSearchMethods : IMethodCallParser
{
    public bool Matches(MethodCallExpression expression)
        => expression.Method.DeclaringType == typeof(FullTextSearchExtensions);

    public ISqlFragment Parse(IMemberResolver memberFactory, MethodCallExpression expression)
    {
        var method = expression.Method.Name;

        var mapping = memberFactory.Mapping
                      ?? throw new BadLinqExpressionException(
                          $"'{method}' can only be used against a document type. Full-text search "
                          + "reads a document's FTS5 index, and this query is not over one.");

        var index = mapping.FullTextIndex
                    ?? throw new BadLinqExpressionException(
                        $"'{mapping.DocumentType.Name}' declares no full-text index, so '{method}' has "
                        + "nothing to search. Declare one with "
                        + $"StoreOptions.Schema.For<{mapping.DocumentType.Name}>().FullTextIndex(...) "
                        + "or the [FullTextIndex] attribute, and re-run the schema migration.");

        // These are extension methods, so the receiver is Arguments[0] and the term is Arguments[1] —
        // the static form, even though every call site is written as though it were an instance one.
        if (expression.Arguments.Count != 2 || !IsQueryParameter(expression.Arguments[0]))
        {
            throw new BadLinqExpressionException(
                $"'{method}' searches the document the query is over, so it has to be called on the "
                + $"lambda's own parameter — Where(x => x.{method}(\"…\")).");
        }

        var term = WhereClauseParser.ExtractValue(expression.Arguments[1]) as string
                   ?? throw new BadLinqExpressionException(
                       $"'{method}' requires a search term that can be evaluated when the query is "
                       + "built.");

        var query = QueryFor(method, term, index, mapping.DocumentType.Name);

        return new FullTextMatchFilter(
            SchemaUtils.QuoteName(FullTextSchema.TableNameFor(mapping).Name),
            memberFactory.TableQualifier, query);
    }

    /// <summary>
    ///     Whether the receiver is the query's own document, rather than some other object that
    ///     happened to be in scope.
    /// </summary>
    /// <remarks>
    ///     <c>Where(x =&gt; someOtherThing.Search("…"))</c> compiles, and there is no index for
    ///     "some other thing" — the search would silently be run against the queried type's. Refused
    ///     with the shape that works named.
    /// </remarks>
    private static bool IsQueryParameter(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert } unary)
        {
            expression = unary.Operand;
        }

        return expression is ParameterExpression;
    }

    private static string QueryFor(string method, string term, FullTextIndex index, string documentType)
    {
        if (method == nameof(FullTextSearchExtensions.NgramSearch))
        {
            RequireTrigram(index, documentType, term);
            return Fts5Query.Ngram(term);
        }

        RefuseTrigram(method, index, documentType);

        return method switch
        {
            nameof(FullTextSearchExtensions.Search) => term,
            nameof(FullTextSearchExtensions.PlainTextSearch) => Fts5Query.PlainText(term),
            nameof(FullTextSearchExtensions.PhraseSearch) => Fts5Query.Phrase(term),
            nameof(FullTextSearchExtensions.WebStyleSearch) => Fts5Query.WebStyle(term),
            nameof(FullTextSearchExtensions.PrefixSearch) => Fts5Query.Prefix(term),
            _ => throw new BadLinqExpressionException(
                $"Fisher cannot translate '{method}' to a full-text query.")
        };
    }

    /// <summary>
    ///     A trigram index cannot answer the word-oriented operators, so they are refused against one.
    /// </summary>
    /// <remarks>
    ///     It stores three-character windows rather than terms, so a phrase, a prefix or an ordinary
    ///     word search against it is not merely less accurate — a query for <c>fox</c> happens to work
    ///     (three characters), one for <c>quick</c> does not, and one for <c>ox</c> does not. Rather
    ///     than have the same operator work or not depending on the length of what the caller typed,
    ///     the combination is refused outright with the operator that <em>does</em> work named.
    /// </remarks>
    private static void RefuseTrigram(string method, FullTextIndex index, string documentType)
    {
        if (index.Tokenizer != FullTextTokenizer.Trigram)
        {
            return;
        }

        throw new BadLinqExpressionException(
            $"'{documentType}' declares a Trigram full-text index, which stores three-character "
            + $"windows rather than words — so '{method}' would match only where a word happened to "
            + "be exactly three characters long. Use NgramSearch against a Trigram index, or declare "
            + "the index with the Porter or Unicode tokenizer.");
    }

    private static void RequireTrigram(FullTextIndex index, string documentType, string term)
    {
        if (index.Tokenizer != FullTextTokenizer.Trigram)
        {
            throw new BadLinqExpressionException(
                $"NgramSearch needs a full-text index declared with the Trigram tokenizer, and "
                + $"'{documentType}' declares {index.Tokenizer}. A word tokenizer stores whole terms, "
                + "so it cannot match a fragment of one and the search would come back empty rather "
                + "than failing. Declare the index with FullTextTokenizer.Trigram, or use "
                + "PrefixSearch to match from the start of a word.");
        }

        if (term.Trim().Length < 3)
        {
            throw new BadLinqExpressionException(
                "NgramSearch needs a term of at least three characters, because a trigram index "
                + "stores three-character windows and a shorter term matches none of them.");
        }
    }
}
