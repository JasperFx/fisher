using System.Linq.Expressions;

namespace Fisher.Linq.Includes;

/// <summary>
///     Reads the <c>Include()</c> plans back out of a query's expression tree.
/// </summary>
/// <remarks>
///     A walk of its own rather than a field on <see cref="Parsing.LinqQueryParser" />, because the
///     terminal operators that need the plans reach the parser through
///     <c>FisherQueryProvider.Build&lt;T&gt;</c>, which hands back a statement and a selector and
///     nothing else. Widening that return for a list the SQL side has no use for would push the
///     includes through every caller of it; this walk is a dozen lines and touches none of them. The
///     parser still sees the marker — it has to, so that combining <c>Include</c> with a projection or
///     a join can be refused by name.
/// </remarks>
internal static class IncludePlans
{
    /// <summary>
    ///     Every plan in the chain, in the order the <c>Include</c> calls were written.
    /// </summary>
    public static IReadOnlyList<IIncludePlan> From(Expression expression)
    {
        List<IIncludePlan>? plans = null;

        var current = expression;

        while (current is MethodCallExpression call && call.Arguments.Count > 0)
        {
            if (PlanIn(call) is { } plan)
            {
                (plans ??= []).Add(plan);
            }

            current = call.Arguments[0];
        }

        if (plans is null)
        {
            return [];
        }

        // The walk runs outermost-first; the caller wrote them the other way round.
        plans.Reverse();
        return plans;
    }

    /// <summary>Whether the chain carries any include at all.</summary>
    public static bool Any(Expression expression)
    {
        var current = expression;

        while (current is MethodCallExpression call && call.Arguments.Count > 0)
        {
            if (PlanIn(call) is not null)
            {
                return true;
            }

            current = call.Arguments[0];
        }

        return false;
    }

    private static IIncludePlan? PlanIn(MethodCallExpression call)
        => call.Method.DeclaringType == typeof(IncludeExtensions)
           && call.Method.Name == nameof(IncludeExtensions.IncludeMarker)
           && call.Arguments[1] is ConstantExpression { Value: IIncludePlan plan }
            ? plan
            : null;
}
