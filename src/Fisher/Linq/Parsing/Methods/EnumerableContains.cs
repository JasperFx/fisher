using System.Collections;
using System.Linq.Expressions;
using Fisher.Linq.Members;
using Fisher.Linq.SqlGeneration;

namespace Fisher.Linq.Parsing.Methods;

/// <summary>
///     <c>ids.Contains(x.Id)</c> → <c>id in (@p0, @p1, ...)</c>.
/// </summary>
/// <remarks>
///     <para>
///         Matches both the LINQ static form (<c>Enumerable.Contains(source, x.Member)</c>) and the
///         instance form on a <see cref="List{T}" />. Distinguished from
///         <see cref="StringContains" /> by the argument being a document member rather than the
///         needle — <c>x.Name.Contains("a")</c> and <c>names.Contains(x.Name)</c> are both spelled
///         <c>Contains</c>.
///     </para>
///     <para>
///         Every element goes through <see cref="IQueryableMember.ConvertValue" />, which is what keeps
///         a list of Guids matching the lowercase canonical text actually stored.
///     </para>
/// </remarks>
internal class EnumerableContains : IMethodCallParser
{
    public bool Matches(MethodCallExpression expression)
    {
        if (expression.Method.Name != "Contains")
        {
            return false;
        }

        // The static two-argument form. Declaring type is deliberately not pinned to Enumerable:
        // `array.Contains(x)` binds to MemoryExtensions.Contains(ReadOnlySpan<T>, T) on modern .NET,
        // and List<T>/IEnumerable<T> go through Enumerable. What identifies the form is the shape —
        // a source and a document member — not which static class the compiler picked.
        if (expression.Object == null && expression.Arguments.Count == 2)
        {
            return IsDocumentMember(expression.Arguments[1]);
        }

        // The instance form on a collection — but not on a string, which StringContains owns.
        return expression.Object != null
               && expression.Method.DeclaringType != typeof(string)
               && expression.Arguments.Count == 1
               && IsDocumentMember(expression.Arguments[0]);
    }

    public ISqlFragment Parse(IMemberResolver memberFactory, MethodCallExpression expression)
    {
        var isStaticForm = expression.Object == null;

        var memberArgument = isStaticForm ? expression.Arguments[1] : expression.Arguments[0];
        var sourceExpression = isStaticForm ? expression.Arguments[0] : expression.Object!;

        var member = memberFactory.ResolveMember((MemberExpression)Strip(memberArgument));

        if (WhereClauseParser.ExtractValue(StripSpanConversion(sourceExpression)) is not IEnumerable source)
        {
            throw new BadLinqExpressionException(
                "'Contains' requires a collection that can be evaluated when the query is built.");
        }

        var values = new List<object?>();
        foreach (var item in source)
        {
            values.Add(member.ConvertValue(item));
        }

        return new WhereInFilter(member.TypedLocator, values);
    }

    private static bool IsDocumentMember(Expression expression)
    {
        var current = Strip(expression) as MemberExpression;
        while (current != null)
        {
            if (current.Expression is ParameterExpression)
            {
                return true;
            }

            current = current.Expression as MemberExpression;
        }

        return false;
    }

    private static Expression Strip(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert } unary)
        {
            expression = unary.Operand;
        }

        return expression;
    }

    /// <summary>
    ///     Unwraps the implicit array-to-span conversion the compiler inserts for
    ///     <c>MemoryExtensions.Contains</c>, yielding the underlying array expression.
    /// </summary>
    /// <remarks>
    ///     Necessary rather than cosmetic: <see cref="ReadOnlySpan{T}" /> is a ref struct, so evaluating
    ///     the conversion by compiling a lambda and invoking it throws — a span cannot be returned as
    ///     <see cref="object" />. Stripping back to the array means the value is evaluated as the array
    ///     it started as.
    /// </remarks>
    private static Expression StripSpanConversion(Expression expression)
    {
        if (expression is MethodCallExpression { Method.Name: "op_Implicit" or "op_Explicit" } conversion
            && conversion.Arguments.Count == 1)
        {
            return conversion.Arguments[0];
        }

        if (expression is MethodCallExpression { Method.Name: "AsSpan" or "AsMemory" } asSpan)
        {
            return asSpan.Object ?? asSpan.Arguments[0];
        }

        return expression;
    }
}
