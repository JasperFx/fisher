using System.Collections;
using System.Linq.Expressions;
using Fisher.Linq.Members;
using Fisher.Linq.SqlGeneration;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.Parsing.Methods;

/// <summary>
///     <c>x.Member.IsOneOf(a, b, c)</c> / <c>x.Member.In(...)</c> → <c>locator in (…)</c>.
/// </summary>
/// <remarks>
///     The same SQL <see cref="EnumerableContains" /> produces, reached from the other direction —
///     there the collection is the receiver and the member is the argument, here the member is the
///     receiver. Marten and Polecat both carry both spellings, so query code ports either way.
/// </remarks>
internal sealed class IsOneOf : IMethodCallParser
{
    public bool Matches(MethodCallExpression expression)
        => expression.Method.DeclaringType == typeof(LinqExtensions)
           && expression.Method.Name is nameof(LinqExtensions.IsOneOf) or nameof(LinqExtensions.In)
           && expression.Arguments.Count == 2;

    public ISqlFragment Parse(IMemberResolver memberFactory, MethodCallExpression expression)
    {
        var member = memberFactory.ResolveMember(
            (MemberExpression)StripConversions(expression.Arguments[0]));

        if (WhereClauseParser.ExtractValue(expression.Arguments[1]) is not IEnumerable values)
        {
            throw new BadLinqExpressionException(
                "IsOneOf needs a set of values that can be evaluated when the query is built.");
        }

        return new WhereInFilter(member.TypedLocator,
            values.Cast<object?>().Select(member.ConvertValue).ToList());
    }

    private static Expression StripConversions(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert } convert)
        {
            expression = convert.Operand;
        }

        return expression;
    }
}

/// <summary>
///     <c>x.Tags.IsEmpty()</c> → the member holds no elements, or is not there at all.
/// </summary>
/// <remarks>
///     <para>
///         <c>json_array_length</c> over a JSON array gives its length. The subtlety is the absent
///         case: <c>json_extract</c> yields SQL NULL for a missing key and
///         <c>json_array_length(null)</c> is NULL rather than 0, so a bare <c>= 0</c> would be NULL and
///         the row would fall out of the result. A caller asking "is this empty" means "is there
///         anything in it", and "the key is not there" is an honest yes — hence the explicit null arm.
///     </para>
///     <para>
///         <see cref="IQueryableMember.RawLocator" /> rather than the typed one, because this asks about
///         the stored JSON's shape rather than comparing a value.
///     </para>
/// </remarks>
internal sealed class IsEmpty : IMethodCallParser
{
    public bool Matches(MethodCallExpression expression)
        => expression.Method.DeclaringType == typeof(LinqExtensions)
           && expression.Method.Name == nameof(LinqExtensions.IsEmpty)
           && expression.Arguments.Count == 1;

    public ISqlFragment Parse(IMemberResolver memberFactory, MethodCallExpression expression)
    {
        var member = memberFactory.ResolveMember((MemberExpression)expression.Arguments[0]);

        return new LiteralSqlFragment(
            $"(json_array_length({member.RawLocator}) = 0 or {member.RawLocator} is null)");
    }
}

/// <summary>
///     <c>object.Equals(x.Member, value)</c> → <c>locator = @p0</c>.
/// </summary>
/// <remarks>
///     The static form, which a caller reaches when the member's compile-time type is
///     <see cref="object" /> and <c>==</c> would compare references. The instance form
///     (<c>x.Name.Equals("a")</c>) belongs to <see cref="StringEquals" />, which handles the
///     case-sensitivity question strings raise and this does not.
/// </remarks>
internal sealed class ObjectEquals : IMethodCallParser
{
    public bool Matches(MethodCallExpression expression)
        => expression.Method.Name == "Equals"
           && expression.Object is null
           && expression.Arguments.Count == 2
           && expression.Method.DeclaringType == typeof(object)
           && StripConversions(expression.Arguments[0]) is MemberExpression;

    public ISqlFragment Parse(IMemberResolver memberFactory, MethodCallExpression expression)
    {
        var member = memberFactory.ResolveMember(
            (MemberExpression)StripConversions(expression.Arguments[0]));

        var value = member.ConvertValue(WhereClauseParser.ExtractValue(expression.Arguments[1]));

        return value is null
            ? new LiteralSqlFragment($"{member.RawLocator} is null")
            : new ComparisonFilter(member.TypedLocator, "=", value);
    }

    private static Expression StripConversions(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert } convert)
        {
            expression = convert.Operand;
        }

        return expression;
    }
}
