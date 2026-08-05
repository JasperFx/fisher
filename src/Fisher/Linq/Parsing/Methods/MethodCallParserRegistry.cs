using System.Linq.Expressions;

namespace Fisher.Linq.Parsing.Methods;

/// <summary>
///     The method calls the WHERE translator understands.
/// </summary>
/// <remarks>
///     Order matters where two parsers could claim the same method name. <see cref="StringContains" />
///     is listed before <see cref="EnumerableContains" /> and each additionally checks the shape of the
///     call — <c>x.Name.Contains("a")</c> takes a string argument on a string receiver, while
///     <c>names.Contains(x.Name)</c> takes a document member as its argument.
/// </remarks>
internal static class MethodCallParserRegistry
{
    private static readonly IMethodCallParser[] Parsers =
    [
        new StringContains(),
        new StringStartsWith(),
        new StringEndsWith(),
        new StringEquals(),
        new StringIsNullOrEmpty(),
        new StringToLower(),
        new StringToUpper(),
        new StringTrim(),
        new EnumerableContains()
    ];

    public static IMethodCallParser? FindParser(MethodCallExpression expression)
    {
        foreach (var parser in Parsers)
        {
            if (parser.Matches(expression))
            {
                return parser;
            }
        }

        return null;
    }
}
