using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Fisher.Linq.Members;
using Fisher.Linq.Parsing;
using Fisher.Linq.SqlGeneration;

namespace Fisher.Linq;

/// <summary>
///     <c>SelectMany</c> over a document's own collection member (fisher#641). See <see cref="FlattenedQuery" />.
/// </summary>
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Compiles the caller's own Select and result selector lambdas with Expression.Compile, as a Select projection and a join result selector already do. The element and document types flow in from the caller's SelectMany call and are preserved per the AOT publishing guide.")]
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "Deserializes the flattened elements and their parent documents with the store's own serializer, as loading the whole document does. Document types are preserved by their registration on the caller side per the AOT publishing guide.")]
public partial class FisherQueryProvider
{
    /// <summary>
    ///     The flattened query, or null when the chain has no <c>SelectMany</c> over a document's collection.
    /// </summary>
    /// <remarks>
    ///     A <c>SelectMany</c> after a <c>GroupJoin</c> is the join's, and handled there.
    /// </remarks>
    private FlattenedQuery? FlattenFor(Expression expression)
    {
        var calls = new List<MethodCallExpression>();

        for (var current = expression;
             current is MethodCallExpression call && call.Arguments.Count > 0;
             current = call.Arguments[0])
        {
            calls.Add(call);
        }

        // Outermost first, so the source end is at the back.
        var index = -1;
        for (var i = calls.Count - 1; i >= 0; i--)
        {
            if (calls[i].Method.Name is "GroupJoin" or "Join") return null;

            if (calls[i].Method.Name == "SelectMany" && calls[i].Method.DeclaringType == typeof(Queryable))
            {
                index = i;
                break;
            }
        }

        if (index < 0) return null;

        var selectMany = calls[index];
        var sourceType = SourceTypeFor(expression);
        var collectionSelector = Unquote(selectMany.Arguments[1]);

        if (collectionSelector.Parameters.Count != 1 || collectionSelector.Parameters[0].Type != sourceType)
        {
            throw new BadLinqExpressionException(
                $"Fisher translates SelectMany only over a collection member of the '{sourceType.Name}' documents "
                + "themselves, as in SelectMany(x => x.Items), with no Select before it and not the overload that "
                + $"passes an index. '{collectionSelector}' is not one. " + WhereClauseParser.Alternatives);
        }

        var body = collectionSelector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert } convert) body = convert.Operand;

        var mapping = _session.Options.Schema.MappingFor(sourceType);
        var member = body is MemberExpression memberExpression && RootOf(memberExpression) == collectionSelector.Parameters[0]
            ? mapping.MembersFor(FlattenedQuery.ParentAlias).ResolveMember(memberExpression)
            : null;

        if (member is not CollectionMember collection)
        {
            throw new BadLinqExpressionException(
                $"Fisher translates SelectMany only over a collection member of the document, as in "
                + $"SelectMany(x => x.Items). '{collectionSelector.Body}' is not one. " + WhereClauseParser.Alternatives);
        }

        var (parent, parser, _, _) = BuildStatement(sourceType, selectMany.Arguments[0]);

        if (RowProjection.For(parser) is not null || parser.GroupByLocator is not null || parser.Joins.Count > 0
            || parser.IsDistinct || parser.DistinctByLocator is not null || parser.AfterPage is not null
            || parser.HasIncludes || parser.Statistics is not null || parser.RequiresFullTextJoin)
        {
            throw new BadLinqExpressionException(
                "Before a SelectMany over a document's collection Fisher supports the operators that choose and "
                + "order the documents: Where, the OrderBy family, Take and Skip, and Fisher's own filters. A "
                + "Select, GroupBy, join, Distinct, Include, Stats, relevance ordering or an operator after "
                + "Take/Skip is not translated there.");
        }

        var ordering = parent.OrderBys.Count == 0
            ? ""
            : "order by " + string.Join(", ", parent.OrderBys.Select(x => x.Descending ? x.Locator + " desc" : x.Locator));

        parent.SelectColumns = $"data, row_number() over ({ordering}) as {FlattenedQuery.OrdinalColumn}";

        var flattened = new FlattenedQuery(parent, collection, sourceType,
            selectMany.Arguments.Count == 3 ? Unquote(selectMany.Arguments[2]) : null);

        for (var i = index - 1; i >= 0; i--)
        {
            flattened.Apply(calls[i]);
        }

        return flattened;
    }

    private async Task<IReadOnlyList<T>> FlattenedListAsync<T>(FlattenedQuery flattened, Statement statement,
        CancellationToken token)
    {
        var serializer = _session.Options.Serializer;
        var resultSelector = flattened.ResultSelector is { } pair ? CompileResultSelector(pair) : null;
        var selector = flattened.Selector is { } select ? CompileSelector(select) : null;

        var results = new List<T>();

        await using var reader = await ExecuteReaderAsync(statement, token).ConfigureAwait(false);

        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            object? row = serializer.FromJson(flattened.ElementType, JsonAt(reader, 0));

            if (resultSelector is not null)
            {
                var parent = serializer.FromJson(flattened.ParentType, JsonAt(reader, 1));
                row = resultSelector(parent, row);
            }

            if (selector is not null)
            {
                row = selector(row);
            }

            results.Add((T)row!);
        }

        return results;
    }

    private static string JsonAt(System.Data.Common.DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? "null" : reader.GetString(ordinal);

    private async Task<long> FlattenedCountAsync(FlattenedQuery flattened, CancellationToken token)
    {
        var statement = flattened.BuildStatement();
        statement.OrderBys.Clear();

        return Convert.ToInt64(await ExecuteScalarAsync(
            new Statement { Subquery = statement, SelectColumns = "count(*)" }, token).ConfigureAwait(false));
    }

    private async Task<bool> FlattenedAnyAsync(FlattenedQuery flattened, CancellationToken token)
    {
        var statement = flattened.BuildStatement();
        statement.SelectColumns = "1";
        statement.IsDistinct = false;
        statement.OrderBys.Clear();
        statement.IsExistsWrapper = true;

        return Convert.ToInt64(await ExecuteScalarAsync(statement, token).ConfigureAwait(false)) != 0;
    }

    private async Task<TResult?> FlattenedAggregateAsync<TResult>(FlattenedQuery flattened, AggregateFunction function,
        LambdaExpression selector, CancellationToken token)
    {
        var member = flattened.AggregatedMember(selector, function.ToString());
        var described = selector.Body.ToString();

        if (function.RequiresANumber() && !IsNumeric(member.MemberType))
        {
            throw new BadLinqExpressionException(
                $"Cannot {function.Sql()} the {member.MemberType.Name} value '{described}': it is not a number.");
        }

        if (!function.RequiresANumber() && !member.AllowsRangeComparison)
        {
            throw new BadLinqExpressionException(
                $"Cannot take the {function.Sql()} of the {member.MemberType.Name} value '{described}' in SQLite: "
                + "its stored form is not order-preserving.");
        }

        var (locator, sqlFunction) = DecimalAggregates.Applies(member)
            ? (DecimalAggregates.JsonTextOf(member, described), DecimalAggregates.FunctionFor(function))
            : (member.TypedLocator, function.Sql());

        // The value is selected inside, under any paging and Distinct, and aggregated outside.
        var inner = flattened.BuildStatement();
        inner.SelectColumns = $"{locator} as __fi_v";

        if (!inner.Limit.HasValue && !inner.Offset.HasValue)
        {
            inner.OrderBys.Clear();
        }

        var outer = new Statement { Subquery = inner, SelectColumns = $"{sqlFunction}(__fi_v)" };

        return Coerce<TResult>(await ExecuteScalarAsync(outer, token).ConfigureAwait(false));
    }

    private static Func<object?, object?, object?> CompileResultSelector(LambdaExpression lambda)
    {
        var parent = Expression.Parameter(typeof(object), "parent");
        var element = Expression.Parameter(typeof(object), "element");

        var body = Expression.Invoke(lambda,
            Expression.Convert(parent, lambda.Parameters[0].Type),
            Expression.Convert(element, lambda.Parameters[1].Type));

        return Expression.Lambda<Func<object?, object?, object?>>(Expression.Convert(body, typeof(object)), parent, element)
            .Compile();
    }

    private static Func<object?, object?> CompileSelector(LambdaExpression lambda)
    {
        var row = Expression.Parameter(typeof(object), "row");
        var body = Expression.Invoke(lambda, Expression.Convert(row, lambda.Parameters[0].Type));

        return Expression.Lambda<Func<object?, object?>>(Expression.Convert(body, typeof(object)), row).Compile();
    }

    private static LambdaExpression Unquote(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Quote } quote) expression = quote.Operand;

        return expression as LambdaExpression
               ?? throw new BadLinqExpressionException($"Expected a lambda, but found '{expression}'.");
    }

    private static ParameterExpression? RootOf(MemberExpression expression)
    {
        Expression? current = expression;
        while (current is MemberExpression member) current = member.Expression;
        return current as ParameterExpression;
    }
}
