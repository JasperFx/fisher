using Weasel.Core;

namespace Fisher.Linq.SqlGeneration;

/// <summary>
///     Two fragments joined by <c>and</c> or <c>or</c>, always parenthesised.
/// </summary>
/// <remarks>
///     The parentheses are not cosmetic. A predicate like <c>a || b</c> nested inside an outer
///     <c>and</c> would bind wrongly without them, because SQL gives <c>AND</c> higher precedence than
///     <c>OR</c> — and the compound <c>||</c> predicate is exactly what
///     <c>assign_tag_where_with_compound_predicate</c> exercises.
/// </remarks>
internal class CompoundWhereFragment : ISqlFragment
{
    private readonly string _separator;
    private readonly ISqlFragment _left;
    private readonly ISqlFragment _right;

    public CompoundWhereFragment(string separator, ISqlFragment left, ISqlFragment right)
    {
        _separator = separator;
        _left = left;
        _right = right;
    }

    public static ISqlFragment And(IReadOnlyList<ISqlFragment> fragments) => Combine("and", fragments);

    public static ISqlFragment Or(IReadOnlyList<ISqlFragment> fragments) => Combine("or", fragments);

    private static ISqlFragment Combine(string separator, IReadOnlyList<ISqlFragment> fragments)
    {
        if (fragments.Count == 0)
        {
            throw new ArgumentException("Cannot combine an empty set of SQL fragments.", nameof(fragments));
        }

        var combined = fragments[0];
        for (var i = 1; i < fragments.Count; i++)
        {
            combined = new CompoundWhereFragment(separator, combined, fragments[i]);
        }

        return combined;
    }

    public void Apply(ICommandBuilder builder)
    {
        builder.Append('(');
        _left.Apply(builder);
        builder.Append(' ');
        builder.Append(_separator);
        builder.Append(' ');
        _right.Apply(builder);
        builder.Append(')');
    }
}
