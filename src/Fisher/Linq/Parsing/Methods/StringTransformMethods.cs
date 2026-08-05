using System.Linq.Expressions;
using Fisher.Linq.Members;
using Fisher.Linq.SqlGeneration;

namespace Fisher.Linq.Parsing.Methods;

/// <summary>
///     <c>x.Name.ToLower() == "abc"</c> → <c>lower(locator) = @p0</c>.
/// </summary>
/// <remarks>
///     Returns a <see cref="SqlFunctionLocator" /> rather than a complete predicate: the call is the
///     <em>operand</em> of a comparison, and the parent binary expression is what turns it into one.
/// </remarks>
internal class StringToLower : IMethodCallParser
{
    public bool Matches(MethodCallExpression expression)
        => expression.Method.DeclaringType == typeof(string)
           && expression.Method.Name is "ToLower" or "ToLowerInvariant"
           && expression.Arguments.Count == 0;

    public ISqlFragment Parse(IMemberResolver memberFactory, MethodCallExpression expression)
        => new SqlFunctionLocator("lower", StringMethods.MemberOf(memberFactory, expression).RawLocator);
}

/// <summary>
///     <c>x.Name.ToUpper() == "ABC"</c> → <c>upper(locator) = @p0</c>.
/// </summary>
internal class StringToUpper : IMethodCallParser
{
    public bool Matches(MethodCallExpression expression)
        => expression.Method.DeclaringType == typeof(string)
           && expression.Method.Name is "ToUpper" or "ToUpperInvariant"
           && expression.Arguments.Count == 0;

    public ISqlFragment Parse(IMemberResolver memberFactory, MethodCallExpression expression)
        => new SqlFunctionLocator("upper", StringMethods.MemberOf(memberFactory, expression).RawLocator);
}

/// <summary>
///     <c>x.Name.Trim()</c> and its one-sided forms.
/// </summary>
/// <remarks>
///     SQLite spells these <c>trim</c> / <c>ltrim</c> / <c>rtrim</c> as single functions, so
///     <c>Trim()</c> is one call rather than Polecat's nested <c>LTRIM(RTRIM(...))</c>.
/// </remarks>
internal class StringTrim : IMethodCallParser
{
    public bool Matches(MethodCallExpression expression)
        => expression.Method.DeclaringType == typeof(string)
           && expression.Method.Name is "Trim" or "TrimStart" or "TrimEnd"
           && expression.Arguments.Count == 0;

    public ISqlFragment Parse(IMemberResolver memberFactory, MethodCallExpression expression)
    {
        var function = expression.Method.Name switch
        {
            "TrimStart" => "ltrim",
            "TrimEnd" => "rtrim",
            _ => "trim"
        };

        return new SqlFunctionLocator(function, StringMethods.MemberOf(memberFactory, expression).RawLocator);
    }
}
