using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Fisher.Linq.Joins;

/// <summary>
///     .NET 10's <c>RightJoin</c>, rewritten as a <c>LeftJoin</c> with its two sources swapped — fisher#666,
///     the port of marten#5910.
/// </summary>
/// <remarks>
///     <para>
///         <c>a.RightJoin(b, ak, bk, (x, y) =&gt; r)</c> keeps every row of <c>b</c>, which is exactly
///         <c>b.LeftJoin(a, bk, ak, (y, x) =&gt; r)</c>. The result selector keeps its own parameters and only
///         swaps their order, so whatever it builds means what it did. Everything downstream then sees an
///         ordinary left join, whose inner side's filters already go in the <c>ON</c> clause: the side that may
///         have no match is <c>a</c> here, and its <c>Where</c> says which of its rows may match, as it should.
///     </para>
///     <para>
///         <b>Done when the query is built</b>, in the provider's <c>CreateQuery</c>, because the swap changes
///         which document type the query starts from, and every terminal reads that off the root of the chain.
///     </para>
///     <para>
///         <b>Refused rather than swapped</b>: a comparer, an inner sequence that is not a Fisher query, and any
///         operator but <c>Where</c> before the join on either source. An ordering or a <c>Take</c> on the side
///         that becomes the inner one would apply within one row's matches, and on the other side it would no
///         longer be what decides the order LINQ yields <c>b</c>'s rows in.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Closes Queryable.LeftJoin over the element types of the caller's own RightJoin call, which the caller's code already instantiated.")]
[UnconditionalSuppressMessage("Trimming", "IL2060:DynamicallyAccessedMembers",
    Justification = "MakeGenericMethod over Queryable.LeftJoin, which has no generic constraints, with the element types of the caller's own RightJoin call.")]
internal static class RightJoinRewriter
{
    private static readonly MethodInfo LeftJoinDefinition =
        new Func<IQueryable<object>, IEnumerable<object>, Expression<Func<object, object>>,
                Expression<Func<object, object>>, Expression<Func<object, object?, object>>, IQueryable<object>>(
                Queryable.LeftJoin)
            .Method.GetGenericMethodDefinition();

    /// <summary>
    ///     The expression with a <c>RightJoin</c> at its top swapped into a <c>LeftJoin</c>, or the same
    ///     instance when there is none.
    /// </summary>
    public static Expression Rewrite(Expression expression)
    {
        if (expression is not MethodCallExpression { Method.Name: "RightJoin" } call
            || call.Method.DeclaringType != typeof(Queryable))
        {
            return expression;
        }

        if (call.Arguments.Count != 5)
        {
            throw new BadLinqExpressionException(
                "Fisher cannot translate a RightJoin with a key comparer. The join compares the keys' stored values "
                + "in SQL, so a comparer would be ignored. Normalize the keys on write, or join in memory.");
        }

        var outer = call.Arguments[0];
        var inner = call.Arguments[1];

        if (!IsFisherQuery(inner))
        {
            throw new BadLinqExpressionException(
                "Fisher cannot translate a RightJoin over an in-memory sequence. Both sides of a join have to be "
                + "session.Query<T>() queries, optionally filtered with Where.");
        }

        RefuseAnythingButWhere(outer, "outer");
        RefuseAnythingButWhere(inner, "inner");

        var arguments = call.Method.GetGenericArguments();
        var leftJoin = LeftJoinDefinition.MakeGenericMethod(arguments[1], arguments[0], arguments[2], arguments[3]);

        var outerKey = Unquote(call.Arguments[2]);
        var innerKey = Unquote(call.Arguments[3]);
        var result = Unquote(call.Arguments[4]);

        return Expression.Call(leftJoin,
            inner,
            outer,
            Expression.Quote(innerKey),
            Expression.Quote(outerKey),
            Expression.Quote(Expression.Lambda(result.Body, result.Parameters[1], result.Parameters[0])));
    }

    /// <summary>Whether the chain starts at a <c>session.Query&lt;T&gt;()</c>.</summary>
    private static bool IsFisherQuery(Expression source)
    {
        var current = source;

        while (current is MethodCallExpression call && call.Arguments.Count > 0)
        {
            current = call.Arguments[0];
        }

        return current is ConstantExpression { Value: { } root }
               && root.GetType().IsGenericType
               && root.GetType().GetGenericTypeDefinition() == typeof(FisherQueryable<>);
    }

    private static void RefuseAnythingButWhere(Expression source, string side)
    {
        for (var current = source;
             current is MethodCallExpression call && call.Arguments.Count > 0;
             current = call.Arguments[0])
        {
            if (call.Method.Name != "Where")
            {
                throw new BadLinqExpressionException(
                    $"Fisher cannot translate a RightJoin whose {side} query uses '{call.Method.Name}'. A RightJoin "
                    + "is translated as a left join with its two sources swapped, so only Where may come before it "
                    + "on either source. Apply the operator after the join instead.");
            }
        }
    }

    private static LambdaExpression Unquote(Expression argument)
    {
        while (argument is UnaryExpression { NodeType: ExpressionType.Quote } quote)
        {
            argument = quote.Operand;
        }

        return (LambdaExpression)argument;
    }
}
