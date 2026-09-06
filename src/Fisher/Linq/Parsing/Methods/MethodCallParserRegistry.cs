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
        // First, and matched on the declaring type alone: the six full-text operators are called on
        // the document rather than on a member, which no other parser's shape test expects.
        new FullTextSearchMethods(),
        new StringContains(),
        new StringStartsWith(),
        new StringEndsWith(),
        new StringEquals(),
        new StringIsNullOrEmpty(),
        new StringToLower(),
        new StringToUpper(),
        new StringTrim(),
        new IsOneOf(),
        // Before EnumerableContains: a Contains whose receiver is a collection-typed document member
        // belongs to the json_each sub-query, whichever shape its argument takes — including the
        // member-vs-member shape, which gets a refusal naming the actual problem.
        new CollectionContains(),
        new EnumerableContains(),
        new CollectionAny(),
        new CollectionAll(),
        new IsEmpty(),
        new MatchesSqlParser(),
        new ObjectEquals()
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
