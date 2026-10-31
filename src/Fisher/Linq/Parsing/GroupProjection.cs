using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Fisher.Linq.Members;
using Fisher.Linq.SqlGeneration;
using Weasel.Core;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.Parsing;

/// <summary>
///     A <c>Select</c> over an <see cref="IGrouping{TKey,TElement}" />, and the <c>HAVING</c> clause a
///     <c>Where</c> after the <c>GroupBy</c> becomes (fisher#24).
/// </summary>
/// <remarks>
///     <para>
///         Built on the same rewrite <see cref="SelectProjection" /> uses — every grouping expression
///         in the lambda becomes an indexer into an <c>object?[]</c> of row values, and the body
///         compiles once. What differs is what counts as a column: over a group there is no document
///         parameter, only <c>g.Key</c> and aggregates over the group.
///     </para>
///     <para>
///         <b>That removes the trap this feature was expected to have.</b> SQLite permits a bare
///         non-aggregated column in a <c>GROUP BY</c> select and picks an arbitrary row for it, where
///         T-SQL rejects the query — so a query that is an error on Polecat would silently return
///         arbitrary data here. The plan was to validate it in the parser. It turns out not to be
///         reachable through this API at all: the lambda's parameter is the <em>grouping</em>, not the
///         document, so there is no ungrouped member in scope to select. The validation is the type
///         system's, and it is free.
///     </para>
/// </remarks>
internal sealed class GroupProjection
{
    private GroupProjection(string[] columns, Type[] columnTypes, Func<object?[], object?> build,
        ISqlFragment? selectFragment)
    {
        Columns = columns;
        ColumnTypes = columnTypes;
        Build = build;
        SelectFragment = selectFragment;
    }

    /// <summary>The SQL expressions to select, in order — the key and the aggregates.</summary>
    public string[] Columns { get; }

    public Type[] ColumnTypes { get; }

    public Func<object?[], object?> Build { get; }

    /// <summary>
    ///     The select list when a column binds a value, which a plain string cannot carry. Null otherwise.
    /// </summary>
    public ISqlFragment? SelectFragment { get; }

    public static GroupProjection For(LambdaExpression selector, GroupingTranslator translator)
    {
        var collector = new Collector(translator, selector.Parameters[0]);
        var body = collector.Visit(selector.Body)!;

        if (collector.Columns.Count == 0)
        {
            throw new BadLinqExpressionException(
                "A Select over a GroupBy must project the key or an aggregate over the group.");
        }

        var lambda = Expression.Lambda<Func<object?[], object?>>(
            Expression.Convert(body, typeof(object)), collector.Values);

        var selectFragment = collector.Fragments.Any(x => x is not null)
            ? new GroupedSelect(collector.Columns, collector.Fragments)
            : null;

        return new GroupProjection(collector.Columns.ToArray(), collector.ColumnTypes.ToArray(),
            lambda.Compile(), selectFragment);
    }

    private sealed class Collector : ExpressionVisitor
    {
        private readonly GroupingTranslator _translator;
        private readonly ParameterExpression _group;
        private readonly Dictionary<string, int> _indexes = new();

        public Collector(GroupingTranslator translator, ParameterExpression group)
        {
            _translator = translator;
            _group = group;
        }

        public ParameterExpression Values { get; } = Expression.Parameter(typeof(object?[]), "values");

        public List<string> Columns { get; } = [];

        public List<Type> ColumnTypes { get; } = [];

        public List<ISqlFragment?> Fragments { get; } = [];

        public override Expression? Visit(Expression? node)
        {
            if (node is not null && _translator.TryTranslate(node, _group, out var sql))
            {
                return Capture(sql, node.Type, null);
            }

            if (node is not null && _translator.TryTranslateFilteredCount(node, _group, out var count))
            {
                return Capture(node.ToString(), node.Type, count);
            }

            return base.Visit(node);
        }

        /// <summary>
        ///     A call the translator declined would otherwise reach <c>Compile()</c> still naming the
        ///     group, whose error says nothing about the query.
        /// </summary>
        protected override Expression VisitMethodCall(MethodCallExpression node)
            => node.Arguments.Count > 0 && node.Arguments[0] == _group
                ? throw Refused($"'{node.Method.Name}'")
                : base.VisitMethodCall(node);

        protected override Expression VisitParameter(ParameterExpression node)
            => node == _group ? throw Refused("The group itself") : base.VisitParameter(node);

        private static BadLinqExpressionException Refused(string what)
            => new($"{what} cannot be used in a Select over a GroupBy. GROUP BY has collapsed the rows, so only "
                   + "g.Key and the aggregates over the group can be read: Count, LongCount, Sum, Min, Max and "
                   + "Average.");

        private Expression Capture(string sql, Type type, ISqlFragment? fragment)
        {
            if (!_indexes.TryGetValue(sql, out var index))
            {
                index = Columns.Count;
                _indexes[sql] = index;
                Columns.Add(sql);
                ColumnTypes.Add(type);
                Fragments.Add(fragment);
            }

            return Expression.Convert(Expression.ArrayIndex(Values, Expression.Constant(index)), type);
        }
    }

    /// <summary>The select list, for a query with a column that binds a value.</summary>
    private sealed class GroupedSelect(List<string> columns, List<ISqlFragment?> fragments) : ISqlFragment
    {
        public void Apply(ICommandBuilder builder)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                if (fragments[i] is { } fragment)
                {
                    fragment.Apply(builder);
                }
                else
                {
                    builder.Append(columns[i]);
                }
            }
        }
    }
}

/// <summary>
///     <c>g.Count(x =&gt; …)</c>: <c>count(*) filter (where …)</c>, over the fragment the predicate
///     parsed to.
/// </summary>
/// <remarks>
///     A fragment and not a string, unlike every other grouped column, because the predicate can bind
///     values. Inlining them would put a caller's value in SQL text, which is why
///     <c>LiteralRenderingCommandBuilder</c> is confined to DDL.
/// </remarks>
internal sealed class FilteredCount(ISqlFragment predicate) : ISqlFragment
{
    public void Apply(ICommandBuilder builder)
    {
        builder.Append("count(*) filter (where ");
        predicate.Apply(builder);
        builder.Append(")");
    }
}

/// <summary>A <c>HAVING</c> comparison whose left side binds values of its own.</summary>
internal sealed class FilteredCountComparison(ISqlFragment count, string op, object value) : ISqlFragment
{
    public void Apply(ICommandBuilder builder)
    {
        count.Apply(builder);
        builder.Append(" ");
        builder.Append(op);
        builder.Append(" ");
        builder.AppendParameter(value);
    }
}

/// <summary>
///     Turns the expressions reachable on an <see cref="IGrouping{TKey,TElement}" /> into SQL.
/// </summary>
/// <remarks>
///     Shared by the projection above and by the <c>HAVING</c> parser, so the two cannot disagree about
///     what <c>g.Count()</c> means.
/// </remarks>
internal sealed class GroupingTranslator
{
    private readonly IMemberResolver _members;
    private readonly string _keyLocator;

    public GroupingTranslator(IMemberResolver members, string keyLocator)
    {
        _members = members;
        _keyLocator = keyLocator;
    }

    /// <summary>
    ///     The SQL for <paramref name="node" /> when it is something a group can answer, else false.
    /// </summary>
    public bool TryTranslate(Expression node, ParameterExpression group, out string sql)
    {
        sql = "";

        // g.Key
        if (node is MemberExpression { Member.Name: "Key" } member && member.Expression == group)
        {
            sql = _keyLocator;
            return true;
        }

        if (node is not MethodCallExpression call || !IsOverTheGroup(call, group))
        {
            return false;
        }

        // g.Count() and g.LongCount(), the only aggregates with no selector.
        if (call.Method.Name is "Count" or "LongCount" && call.Arguments.Count == 1)
        {
            sql = "count(*)";
            return true;
        }

        if (call.Arguments.Count != 2)
        {
            return false;
        }

        var function = call.Method.Name switch
        {
            "Sum" => AggregateFunction.Sum,
            "Min" => AggregateFunction.Min,
            "Max" => AggregateFunction.Max,
            "Average" => AggregateFunction.Average,
            _ => (AggregateFunction?)null
        };

        if (function is null)
        {
            return false;
        }

        sql = $"{function.Value.Sql()}({LocatorFor(call, function.Value)})";
        return true;
    }

    /// <summary>
    ///     <c>g.Count(x =&gt; …)</c> or <c>g.LongCount(x =&gt; …)</c>. The predicate goes through the same
    ///     parser a <c>Where</c> does, so the two cannot disagree about what it means (marten#5625).
    /// </summary>
    public bool TryTranslateFilteredCount(Expression node, ParameterExpression group,
        [NotNullWhen(true)] out ISqlFragment? count)
    {
        count = null;

        if (node is not MethodCallExpression { Method.Name: "Count" or "LongCount", Arguments.Count: 2 } call
            || !IsOverTheGroup(call, group))
        {
            return false;
        }

        var predicate = (LambdaExpression)StripQuotes(call.Arguments[1]);
        count = new FilteredCount(new WhereClauseParser(_members).Parse(predicate.Body));

        return true;
    }

    /// <summary>
    ///     Whether this call's source is the grouping parameter — as opposed to some other enumerable
    ///     the caller closed over, which has nothing to do with the group.
    /// </summary>
    private static bool IsOverTheGroup(MethodCallExpression call, ParameterExpression group)
        => call.Arguments.Count > 0 && call.Arguments[0] == group;

    private string LocatorFor(MethodCallExpression call, AggregateFunction function)
    {
        var lambda = (LambdaExpression)StripQuotes(call.Arguments[1]);
        var body = lambda.Body;

        while (body is UnaryExpression { NodeType: ExpressionType.Convert } unary)
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression memberExpression)
        {
            throw new BadLinqExpressionException(
                $"'{call.Method.Name}' over a group is only supported over a document member.");
        }

        var member = _members.ResolveMember(memberExpression);

        // The same two guards the query-level aggregates apply, for the same reasons — SQLite's sum()
        // over text returns 0 rather than failing, and a string-stored enum does not order.
        if (function.RequiresANumber() && !IsNumeric(member.MemberType))
        {
            throw new BadLinqExpressionException(
                $"Cannot {function.Sql()} the {member.MemberType.Name} member "
                + $"'{memberExpression.Member.Name}': it is not a number.");
        }

        if (!function.RequiresANumber() && !member.AllowsRangeComparison)
        {
            throw new BadLinqExpressionException(
                $"Cannot take the {function.Sql()} of the {member.MemberType.Name} member "
                + $"'{memberExpression.Member.Name}': its stored form is not order-preserving.");
        }

        return member.TypedLocator;
    }

    private static bool IsNumeric(Type type)
    {
        var inner = Nullable.GetUnderlyingType(type) ?? type;

        return !inner.IsEnum && Type.GetTypeCode(inner) is TypeCode.Byte or TypeCode.SByte
            or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32
            or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double
            or TypeCode.Decimal;
    }

    private static Expression StripQuotes(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Quote } quote)
        {
            expression = quote.Operand;
        }

        return expression;
    }

    /// <summary>
    ///     A <c>Where</c> after the <c>GroupBy</c>, as a <c>HAVING</c> fragment.
    /// </summary>
    /// <remarks>
    ///     Deliberately narrower than <see cref="WhereClauseParser" />: a comparison between a grouping
    ///     expression and a constant, composed with <c>&amp;&amp;</c> and <c>||</c>. That is what a
    ///     HAVING clause is for, and widening it would mean answering questions about individual rows
    ///     from a clause that runs after they have been collapsed.
    /// </remarks>
    public ISqlFragment ParseHaving(Expression body, ParameterExpression group)
    {
        switch (body)
        {
            case BinaryExpression { NodeType: ExpressionType.AndAlso } and:
                return CompoundWhereFragment.And(
                    [ParseHaving(and.Left, group), ParseHaving(and.Right, group)]);

            case BinaryExpression { NodeType: ExpressionType.OrElse } or:
                return CompoundWhereFragment.Or(
                    [ParseHaving(or.Left, group), ParseHaving(or.Right, group)]);

            case UnaryExpression { NodeType: ExpressionType.Not } not:
                return new NotFragment(ParseHaving(not.Operand, group));

            case BinaryExpression binary when Comparisons.TryGetValue(binary.NodeType, out var op):
                return Comparison(binary, op, group);

            default:
                throw new BadLinqExpressionException(
                    "A Where after a GroupBy becomes a HAVING clause, so it can only compare the group's "
                    + $"key or an aggregate over it against a value. '{body}' is neither.");
        }
    }

    private static readonly Dictionary<ExpressionType, string> Comparisons = new()
    {
        { ExpressionType.Equal, "=" },
        { ExpressionType.NotEqual, "!=" },
        { ExpressionType.GreaterThan, ">" },
        { ExpressionType.GreaterThanOrEqual, ">=" },
        { ExpressionType.LessThan, "<" },
        { ExpressionType.LessThanOrEqual, "<=" }
    };

    private ISqlFragment Comparison(BinaryExpression binary, string op, ParameterExpression group)
    {
        if (TryTranslateFilteredCount(binary.Left, group, out var leftCount))
        {
            return new FilteredCountComparison(leftCount, op, HavingValue(binary.Right));
        }

        if (TryTranslateFilteredCount(binary.Right, group, out var rightCount))
        {
            return new FilteredCountComparison(rightCount, Flip(op), HavingValue(binary.Left));
        }

        if (TryTranslate(binary.Left, group, out var left))
        {
            return new ComparisonFilter(left, op,
                WhereClauseParser.ExtractValue(binary.Right)
                ?? throw new BadLinqExpressionException("A HAVING comparison cannot be against null."));
        }

        if (TryTranslate(binary.Right, group, out var right))
        {
            // Reversed operands: `10 < g.Count()` is `count(*) > 10`.
            return new ComparisonFilter(right, Flip(op),
                WhereClauseParser.ExtractValue(binary.Left)
                ?? throw new BadLinqExpressionException("A HAVING comparison cannot be against null."));
        }

        throw new BadLinqExpressionException(
            $"Neither side of '{binary}' is the group's key or an aggregate over it.");
    }

    private static object HavingValue(Expression expression)
        => WhereClauseParser.ExtractValue(expression)
           ?? throw new BadLinqExpressionException("A HAVING comparison cannot be against null.");

    private static string Flip(string op) => op switch
    {
        ">" => "<",
        ">=" => "<=",
        "<" => ">",
        "<=" => ">=",
        _ => op
    };
}
