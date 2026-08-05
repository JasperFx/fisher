using System.Linq.Expressions;
using Fisher.Linq.Members;
using Fisher.Linq.SqlGeneration;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.Parsing.Methods;

/// <summary>
///     Shared helpers for the string predicate parsers.
/// </summary>
/// <remarks>
///     <para>
///         <strong>These are built on <c>instr</c> and <c>substr</c>, not <c>LIKE</c>, and that is the
///         central SQLite decision in this file.</strong> Polecat translates
///         <c>Contains</c>/<c>StartsWith</c>/<c>EndsWith</c> to <c>LIKE</c> patterns. On SQLite that
///         would be wrong twice over, both verified against 3.51:
///     </para>
///     <list type="number">
///         <item>
///             <description>
///                 <c>LIKE</c> is case-<em>insensitive</em> for ASCII by default, while <c>=</c> is
///                 case-<em>sensitive</em>. A LIKE-based <c>Contains("frodo")</c> would match
///                 <c>"Frodo"</c> on the very same data where <c>== "frodo"</c> does not — an
///                 internally inconsistent query surface, and not what .NET's ordinal
///                 <see cref="string.Contains(string)" /> means.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <c>_</c> and <c>%</c> are <c>LIKE</c> wildcards, so a literal needle containing
///                 either has to be escaped. Polecat's <c>[_]</c> bracket escaping is T-SQL-only;
///                 SQLite needs an <c>ESCAPE</c> clause. This is the same trap CLAUDE.md already
///                 records for the document cleaner's table matching. <c>instr</c> takes its needle
///                 literally and sidesteps it entirely.
///             </description>
///         </item>
///     </list>
///     <para>
///         An explicit <see cref="StringComparison" /> of <c>OrdinalIgnoreCase</c> is honoured by
///         lowering both sides — the needle client-side, the column with <c>lower()</c>.
///     </para>
/// </remarks>
internal static class StringMethods
{
    /// <summary>
    ///     Resolves the member a string method was called on, rejecting calls on anything that is not a
    ///     document member.
    /// </summary>
    internal static IQueryableMember MemberOf(IMemberResolver memberFactory, MethodCallExpression expression)
    {
        if (expression.Object is not MemberExpression member)
        {
            throw new BadLinqExpressionException(
                $"'{expression.Method.Name}' is only supported when called on a document member.");
        }

        return memberFactory.ResolveMember(member);
    }

    /// <summary>
    ///     The needle, as a string. Null is rejected: SQL's three-valued logic would quietly drop every
    ///     row rather than throw the way the CLR method would.
    /// </summary>
    internal static string NeedleOf(MethodCallExpression expression, int index = 0)
    {
        var value = WhereClauseParser.ExtractValue(expression.Arguments[index]);

        return value as string
               ?? throw new BadLinqExpressionException(
                   $"'{expression.Method.Name}' requires a non-null string argument.");
    }

    /// <summary>
    ///     Whether an explicit <see cref="StringComparison" /> argument asks for case insensitivity.
    /// </summary>
    internal static bool IsCaseInsensitive(MethodCallExpression expression)
    {
        foreach (var argument in expression.Arguments)
        {
            if (argument.Type != typeof(StringComparison))
            {
                continue;
            }

            var comparison = (StringComparison)WhereClauseParser.ExtractValue(argument)!;
            return comparison is StringComparison.OrdinalIgnoreCase
                or StringComparison.CurrentCultureIgnoreCase
                or StringComparison.InvariantCultureIgnoreCase;
        }

        return false;
    }

    /// <summary>
    ///     Folds the locator and needle to lower case together, or leaves both alone.
    /// </summary>
    internal static (string Locator, string Needle) Fold(string locator, string needle, bool caseInsensitive)
        => caseInsensitive
            ? ($"lower({locator})", needle.ToLowerInvariant())
            : (locator, needle);
}

/// <summary>
///     <c>x.Name.Contains("value")</c> → <c>instr(locator, @p0) &gt; 0</c>.
/// </summary>
internal class StringContains : IMethodCallParser
{
    public bool Matches(MethodCallExpression expression)
        => expression.Method.Name == "Contains"
           && expression.Method.DeclaringType == typeof(string)
           && expression.Arguments.Count >= 1
           && expression.Arguments[0].Type == typeof(string);

    public ISqlFragment Parse(IMemberResolver memberFactory, MethodCallExpression expression)
    {
        var member = StringMethods.MemberOf(memberFactory, expression);
        var (locator, needle) = StringMethods.Fold(member.RawLocator,
            StringMethods.NeedleOf(expression), StringMethods.IsCaseInsensitive(expression));

        return new InstrFilter(locator, needle, ">", 0);
    }
}

/// <summary>
///     <c>x.Name.StartsWith("value")</c> → <c>instr(locator, @p0) = 1</c>.
/// </summary>
/// <remarks>
///     An empty needle gives <c>instr = 1</c>, so <c>StartsWith("")</c> is true — which is what .NET
///     says too. Verified rather than assumed.
/// </remarks>
internal class StringStartsWith : IMethodCallParser
{
    public bool Matches(MethodCallExpression expression)
        => expression.Method.Name == "StartsWith"
           && expression.Method.DeclaringType == typeof(string)
           && expression.Arguments.Count >= 1
           && expression.Arguments[0].Type == typeof(string);

    public ISqlFragment Parse(IMemberResolver memberFactory, MethodCallExpression expression)
    {
        var member = StringMethods.MemberOf(memberFactory, expression);
        var (locator, needle) = StringMethods.Fold(member.RawLocator,
            StringMethods.NeedleOf(expression), StringMethods.IsCaseInsensitive(expression));

        return new InstrFilter(locator, needle, "=", 1);
    }
}

/// <summary>
///     <c>x.Name.EndsWith("value")</c> → <c>substr(locator, -5) = @p0</c>.
/// </summary>
/// <remarks>
///     The offset is the needle's length negated, computed here rather than as <c>-length(?)</c> in
///     SQL — the needle is a constant at translation time, so this binds one parameter instead of two.
///     An empty needle is special-cased to <c>1=1</c>: <c>substr(x, 0)</c> returns the whole string, so
///     the generated form would say false where .NET's <c>EndsWith("")</c> says true.
/// </remarks>
internal class StringEndsWith : IMethodCallParser
{
    public bool Matches(MethodCallExpression expression)
        => expression.Method.Name == "EndsWith"
           && expression.Method.DeclaringType == typeof(string)
           && expression.Arguments.Count >= 1
           && expression.Arguments[0].Type == typeof(string);

    public ISqlFragment Parse(IMemberResolver memberFactory, MethodCallExpression expression)
    {
        var member = StringMethods.MemberOf(memberFactory, expression);
        var (locator, needle) = StringMethods.Fold(member.RawLocator,
            StringMethods.NeedleOf(expression), StringMethods.IsCaseInsensitive(expression));

        if (needle.Length == 0)
        {
            return new WhereFragment("1=1");
        }

        return new ComparisonFilter($"substr({locator}, -{needle.Length})", "=", needle);
    }
}

/// <summary>
///     <c>x.Name.Equals("value")</c>, including the <see cref="StringComparison" /> overload.
/// </summary>
internal class StringEquals : IMethodCallParser
{
    public bool Matches(MethodCallExpression expression)
        => expression.Method.Name == "Equals"
           && expression.Method.DeclaringType == typeof(string)
           && expression.Object is MemberExpression
           && expression.Arguments.Count >= 1
           && expression.Arguments[0].Type == typeof(string);

    public ISqlFragment Parse(IMemberResolver memberFactory, MethodCallExpression expression)
    {
        var member = StringMethods.MemberOf(memberFactory, expression);
        var (locator, needle) = StringMethods.Fold(member.RawLocator,
            StringMethods.NeedleOf(expression), StringMethods.IsCaseInsensitive(expression));

        return new ComparisonFilter(locator, "=", needle);
    }
}

/// <summary>
///     <c>string.IsNullOrEmpty(x.Name)</c> → <c>locator is null or locator = ''</c>.
/// </summary>
internal class StringIsNullOrEmpty : IMethodCallParser
{
    public bool Matches(MethodCallExpression expression)
        => expression.Method.Name is "IsNullOrEmpty" or "IsNullOrWhiteSpace"
           && expression.Method.DeclaringType == typeof(string)
           && expression.Arguments.Count == 1;

    public ISqlFragment Parse(IMemberResolver memberFactory, MethodCallExpression expression)
    {
        if (expression.Arguments[0] is not MemberExpression memberExpression)
        {
            throw new BadLinqExpressionException(
                $"'{expression.Method.Name}' is only supported over a document member.");
        }

        var member = memberFactory.ResolveMember(memberExpression);

        // IsNullOrWhiteSpace additionally folds away surrounding whitespace, which trim() does.
        var locator = expression.Method.Name == "IsNullOrWhiteSpace"
            ? $"trim({member.RawLocator})"
            : member.RawLocator;

        return new WhereFragment($"({locator} is null or {locator} = '')");
    }
}
