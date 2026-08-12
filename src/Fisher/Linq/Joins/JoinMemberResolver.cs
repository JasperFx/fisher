using System.Linq.Expressions;
using Fisher.Linq.Members;

namespace Fisher.Linq.Joins;

/// <summary>
///     Resolves a member to whichever side of the join it belongs to.
/// </summary>
/// <remarks>
///     <para>
///         A predicate written after a join can name members of every joined document in one expression
///         — <c>angler.Region == "Shire" &amp;&amp; landed.Weight &gt; 5 &amp;&amp; water.Name != ""</c>
///         — so the where parser needs a resolver that answers for any of them. Which side a member
///         belongs to is decided by the parameter its chain is rooted at, <b>by reference</b>: a
///         self-join has the same document type on more than one side, so comparing types would resolve
///         every member against the first.
///     </para>
///     <para>
///         That the seam is a single-method interface is what makes this cheap. It is the same reason
///         <c>EventMemberFactory</c> exists — <c>IMemberResolver</c> was made an interface rather than
///         a concrete type precisely so a caller could answer "where does this member live" differently.
///     </para>
/// </remarks>
internal sealed class JoinMemberResolver : IMemberResolver
{
    private readonly IReadOnlyDictionary<ParameterExpression, IMemberResolver> _sides;

    public JoinMemberResolver(IEnumerable<JoinSide> sides)
    {
        _sides = sides.ToDictionary(side => side.Parameter, IMemberResolver (side) => side.Members);
    }

    public IQueryableMember ResolveMember(MemberExpression expression)
    {
        Expression? current = expression;

        while (current is MemberExpression member)
        {
            current = member.Expression;
        }

        if (current is not ParameterExpression parameter || !_sides.TryGetValue(parameter, out var side))
        {
            throw new BadLinqExpressionException(
                $"Fisher cannot resolve '{expression}' against any side of the join. A member has to "
                + "belong to one of the joined documents.");
        }

        return side.ResolveMember(expression);
    }
}
