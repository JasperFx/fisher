using Weasel.Core;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.SqlGeneration;

/// <summary>
///     A fragment that knows how to negate itself with C#'s two-valued answer rather than SQL's
///     three-valued one (fisher#608).
/// </summary>
internal interface INegatableFragment
{
    ISqlFragment Negate();
}

/// <summary>
///     Negates a parsed predicate the way C# would evaluate <c>!(…)</c> over the same document (fisher#608).
/// </summary>
/// <remarks>
///     <para>
///         C# compares a null member as false — <c>x.N &gt; 3</c> is false when <c>x.N</c> is null — so
///         <c>!(x.N &gt; 3)</c> is true. SQL compares it as NULL, and <c>not (NULL)</c> is NULL, which a
///         WHERE drops. A plain <c>not (…)</c> therefore loses every null row under a negation.
///     </para>
///     <para>
///         A comparison against a member negates itself null-safe (<see cref="ComparisonFilter" />,
///         <see cref="WhereInFilter" />, <see cref="ModuloFilter" />). A compound is negated by De Morgan,
///         so each leaf decides for itself. Anything else, including a string method whose C# call would
///         throw on a null receiver, keeps SQL's <c>not (…)</c>.
///     </para>
/// </remarks>
internal static class SqlNegation
{
    public static ISqlFragment Negate(ISqlFragment fragment) => fragment switch
    {
        INegatableFragment negatable => negatable.Negate(),
        CompoundWhereFragment compound => new CompoundWhereFragment(
            compound.Separator.Trim() == "and" ? "or" : "and",
            Negate(compound.Children.Left), Negate(compound.Children.Right)),
        NotFragment not => not.Inner,
        _ => new NotFragment(fragment)
    };

    /// <summary>
    ///     Renders <c>left op right</c> with C#'s answer for a null operand: <c>!=</c> is <c>is not</c>, and a
    ///     negated comparison is <c>is</c> / <c>is not</c> for equality and <c>not coalesce(…, 0)</c> for a
    ///     range. A non-negated <c>=</c> or range comparison is left plain, which is what an index serves.
    /// </summary>
    public static void ApplyNullSafe(ICommandBuilder builder, Action<ICommandBuilder> left, string op,
        bool negated, Action<ICommandBuilder> right)
    {
        if (!negated)
        {
            left(builder);
            builder.Append(op == "!=" ? " is not " : $" {op} ");
            right(builder);
            return;
        }

        if (op is "=" or "!=")
        {
            left(builder);
            builder.Append(op == "=" ? " is not " : " is ");
            right(builder);
            return;
        }

        builder.Append("not coalesce(");
        left(builder);
        builder.Append($" {op} ");
        right(builder);
        builder.Append(", 0)");
    }
}
