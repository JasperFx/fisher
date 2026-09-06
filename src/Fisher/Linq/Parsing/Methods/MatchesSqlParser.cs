using System.Linq.Expressions;
using Fisher.Linq.Members;
using Fisher.Linq.SqlGeneration;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.Parsing.Methods;

/// <summary>
///     <c>x.MatchesSql("…", values)</c> → the caller's SQL, bracketed, with its values bound
///     (fisher#202).
/// </summary>
/// <remarks>
///     <para>
///         Registered in <see cref="MethodCallParserRegistry" /> rather than through
///         <c>WhereClauseParser</c>'s <c>additionalParsers</c> seam, so it reaches every predicate the
///         parser serves — <c>Query&lt;T&gt;()</c>, a join's <c>ON</c> clause, <c>DeleteWhere</c>, and
///         the event-side predicates behind <c>QueryEventDataAsync</c> and <c>AssignTagWhere</c>. That
///         is deliberate: the alternative is a method that compiles everywhere and refuses itself in
///         half the places, and the trust model is identical wherever it lands — the caller supplied
///         the SQL.
///     </para>
///     <para>
///         The member resolver is unused, which is the point of the operator: nothing here is
///         translated. Everything about the SQL is the caller's, so the columns it names are the
///         physical ones — <c>json_extract(data, '$.name')</c>, not <c>Name</c>. Use
///         <c>session.ToSql(...)</c> on an ordinary query to see the spellings.
///     </para>
/// </remarks>
internal sealed class MatchesSqlParser : IMethodCallParser
{
    public bool Matches(MethodCallExpression expression)
        => expression.Method.DeclaringType == typeof(LinqExtensions)
           && expression.Method.Name == nameof(LinqExtensions.MatchesSql);

    public ISqlFragment Parse(IMemberResolver memberFactory, MethodCallExpression expression)
    {
        // Two shapes: (doc, sql, values) and (doc, placeholder, sql, values). The placeholder overload
        // exists for the same reason IAdvancedSql's twins do — a bare '?' Fisher does not consume is
        // still SQLite's own anonymous parameter marker, so SQL carrying a literal '?' needs a
        // different one.
        var placeholder = expression.Arguments.Count == 4
            ? (char)WhereClauseParser.ExtractValue(expression.Arguments[1])!
            : '?';

        var sqlArgument = expression.Arguments.Count == 4 ? 2 : 1;

        if (WhereClauseParser.ExtractValue(expression.Arguments[sqlArgument]) is not string sql
            || string.IsNullOrWhiteSpace(sql))
        {
            throw new BadLinqExpressionException(
                "MatchesSql needs a non-empty SQL string that can be evaluated when the query is built.");
        }

        var parameters = WhereClauseParser.ExtractValue(expression.Arguments[sqlArgument + 1])
            as object?[] ?? [];

        return new MatchesSqlFilter(sql, placeholder, parameters);
    }
}
