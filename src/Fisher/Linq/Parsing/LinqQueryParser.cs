using System.Linq.Expressions;
using Fisher.Linq.Members;
using Fisher.Linq.SqlGeneration;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.Parsing;

/// <summary>
///     Walks a LINQ method chain into a <see cref="Statement" />.
/// </summary>
/// <remarks>
///     <para>
///         A much narrower version of Polecat's parser of the same name, covering the operators Fisher
///         can answer today: <c>Where</c>, the four ordering operators, <c>Take</c> and <c>Skip</c>.
///         Grouping, projection and joins are absent because the SQL for them is absent — see
///         <see cref="Statement" />.
///     </para>
///     <para>
///         The chain arrives outermost-call-first, so it is walked to the source and then applied in
///         reverse. Order matters for <c>ThenBy</c>, which must land after the <c>OrderBy</c> it
///         refines.
///     </para>
/// </remarks>
internal class LinqQueryParser
{
    private readonly IMemberResolver _memberFactory;
    private readonly WhereClauseParser _whereParser;

    public LinqQueryParser(IMemberResolver memberFactory)
    {
        _memberFactory = memberFactory;
        _whereParser = new WhereClauseParser(memberFactory);
    }

    /// <summary>
    ///     The where fragments the chain produced, ANDed together by the statement.
    /// </summary>
    public List<ISqlFragment> Wheres { get; } = [];

    public List<(string Locator, bool Descending)> OrderBys { get; } = [];

    public int? Limit { get; private set; }

    public int? Offset { get; private set; }

    public void Parse(Expression expression)
    {
        var calls = new List<MethodCallExpression>();

        var current = expression;
        while (current is MethodCallExpression call)
        {
            calls.Add(call);
            current = call.Arguments.Count > 0 ? call.Arguments[0] : null!;
        }

        // Outermost first on the way down, so apply from the source outward.
        for (var i = calls.Count - 1; i >= 0; i--)
        {
            Apply(calls[i]);
        }
    }

    private void Apply(MethodCallExpression call)
    {
        switch (call.Method.Name)
        {
            case "Where":
                Wheres.Add(_whereParser.Parse(UnwrapLambda(call).Body));
                break;

            case "OrderBy":
                OrderBys.Add((LocatorFor(call), false));
                break;

            case "OrderByDescending":
                OrderBys.Add((LocatorFor(call), true));
                break;

            case "ThenBy":
                OrderBys.Add((LocatorFor(call), false));
                break;

            case "ThenByDescending":
                OrderBys.Add((LocatorFor(call), true));
                break;

            case "Take":
                Limit = (int)WhereClauseParser.ExtractValue(call.Arguments[1])!;
                break;

            case "Skip":
                Offset = (int)WhereClauseParser.ExtractValue(call.Arguments[1])!;
                break;

            // Terminal operators are handled by the provider, which knows what shape of result to
            // ask the statement for; they contribute nothing to the WHERE/ORDER BY.
            case "Count":
            case "LongCount":
            case "Any":
            case "First":
            case "FirstOrDefault":
            case "Single":
            case "SingleOrDefault":
                ApplyTerminalPredicate(call);
                break;

            default:
                throw new BadLinqExpressionException(
                    $"Fisher cannot translate '{call.Method.Name}' to SQL yet. Supported operators are "
                    + "Where, OrderBy, OrderByDescending, ThenBy, ThenByDescending, Take and Skip.");
        }
    }

    /// <summary>
    ///     <c>First(x =&gt; ...)</c> and friends carry an optional predicate, which is a where clause by
    ///     another name.
    /// </summary>
    private void ApplyTerminalPredicate(MethodCallExpression call)
    {
        if (call.Arguments.Count > 1)
        {
            Wheres.Add(_whereParser.Parse(UnwrapLambda(call).Body));
        }
    }

    /// <summary>
    ///     Resolves an ordering key, refusing one whose stored form does not sort — the same guard the
    ///     where parser applies to a range comparison, for the same reason.
    /// </summary>
    private string LocatorFor(MethodCallExpression call)
    {
        var body = UnwrapLambda(call).Body;

        while (body is UnaryExpression { NodeType: ExpressionType.Convert } unary)
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression memberExpression)
        {
            throw new BadLinqExpressionException(
                $"'{call.Method.Name}' is only supported over a document member.");
        }

        var member = _memberFactory.ResolveMember(memberExpression);

        if (!member.AllowsRangeComparison)
        {
            throw new BadLinqExpressionException(
                $"Cannot order by the {member.MemberType.Name} member in SQLite: its stored form is not "
                + "order-preserving, so the rows would come back in a plausible but wrong order. For an "
                + "enum, storing it as an integer (StoreOptions.Serializer.EnumStorage) makes ordering "
                + "meaningful.");
        }

        return member.TypedLocator;
    }

    private static LambdaExpression UnwrapLambda(MethodCallExpression call)
    {
        var argument = call.Arguments[^1];

        while (argument is UnaryExpression { NodeType: ExpressionType.Quote } quote)
        {
            argument = quote.Operand;
        }

        return (LambdaExpression)argument;
    }
}
