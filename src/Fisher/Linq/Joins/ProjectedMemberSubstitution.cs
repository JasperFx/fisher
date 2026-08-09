using System.Linq.Expressions;

namespace Fisher.Linq.Joins;

/// <summary>
///     Replaces members of a join's projected result with the expressions they were built from.
/// </summary>
/// <remarks>
///     <para>
///         What makes a filter or an ordering written after the join translatable at all: the projected
///         shape has no columns, but each of its members may have come straight from a document member,
///         and the result selector is the record of which. <c>x.Weight</c> over
///         <c>new { …, Weight = c.Weight }</c> becomes <c>inner.Weight</c>, which the ordinary member
///         factories can resolve.
///     </para>
///     <para>
///         A member built from anything else — a concatenation, an arithmetic expression, a constant —
///         substitutes to that expression, and the caller then finds it is not a member and refuses the
///         operator. That is the honest boundary: the value exists only after the row has been read, so
///         SQLite cannot filter or sort on it.
///     </para>
/// </remarks>
internal sealed class ProjectedMemberSubstitution : ExpressionVisitor
{
    private readonly ParameterExpression _parameter;
    private readonly Dictionary<string, Expression> _members;
    private bool _unresolved;

    private ProjectedMemberSubstitution(ParameterExpression parameter,
        Dictionary<string, Expression> members)
    {
        _parameter = parameter;
        _members = members;
    }

    /// <returns>
    ///     The rewritten expression, or null when some part of it named the projected shape and could
    ///     not be traced back to a document.
    /// </returns>
    public static Expression? Apply(Expression body, ParameterExpression parameter,
        Dictionary<string, Expression> members)
    {
        var substitution = new ProjectedMemberSubstitution(parameter, members);
        var rewritten = substitution.Visit(body);

        return substitution._unresolved ? null : rewritten;
    }

    protected override Expression VisitMember(MemberExpression node)
    {
        if (node.Expression == _parameter)
        {
            if (_members.TryGetValue(node.Member.Name, out var source))
            {
                return source;
            }

            _unresolved = true;
            return node;
        }

        return base.VisitMember(node);
    }

    protected override Expression VisitParameter(ParameterExpression node)
    {
        // The parameter reached other than as the receiver of a member access — the whole projected
        // object, which no column holds.
        if (node == _parameter)
        {
            _unresolved = true;
        }

        return base.VisitParameter(node);
    }
}
