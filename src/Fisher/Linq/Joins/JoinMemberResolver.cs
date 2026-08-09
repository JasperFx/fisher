using System.Linq.Expressions;
using Fisher.Linq.Members;

namespace Fisher.Linq.Joins;

/// <summary>
///     Resolves a member to whichever side of the join it belongs to.
/// </summary>
/// <remarks>
///     <para>
///         A predicate written after a join can name members of both documents in one expression —
///         <c>angler.Region == "Shire" &amp;&amp; landed.Weight &gt; 5</c> — so the where parser needs a
///         resolver that answers for either table. Which side a member belongs to is decided by the
///         parameter its chain is rooted at, by reference: a self-join has the same document type on
///         both sides, so comparing types would resolve every member against the outer one.
///     </para>
///     <para>
///         That the seam is a single-method interface is what makes this cheap. It is the same reason
///         <c>EventMemberFactory</c> exists — <c>IMemberResolver</c> was made an interface rather than
///         a concrete type precisely so a caller could answer "where does this member live" differently.
///     </para>
/// </remarks>
internal sealed class JoinMemberResolver : IMemberResolver
{
    private readonly ParameterExpression _outerParameter;
    private readonly IMemberResolver _outer;
    private readonly IMemberResolver _inner;

    public JoinMemberResolver(ParameterExpression outerParameter, IMemberResolver outer,
        IMemberResolver inner)
    {
        _outerParameter = outerParameter;
        _outer = outer;
        _inner = inner;
    }

    public IQueryableMember ResolveMember(MemberExpression expression)
    {
        Expression? current = expression;

        while (current is MemberExpression member)
        {
            current = member.Expression;
        }

        if (current is not ParameterExpression parameter)
        {
            throw new BadLinqExpressionException(
                $"Fisher cannot resolve '{expression}' against either side of the join. A member has to "
                + "belong to one of the two joined documents.");
        }

        return (parameter == _outerParameter ? _outer : _inner).ResolveMember(expression);
    }
}
