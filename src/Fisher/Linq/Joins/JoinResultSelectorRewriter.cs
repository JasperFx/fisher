using System.Linq.Expressions;

namespace Fisher.Linq.Joins;

/// <summary>
///     What each member of a constructed join result was built from, by name.
/// </summary>
/// <remarks>
///     <para>
///         All that survives of the two-selector rewriter fisher#25 shipped. Collapsing a join's
///         selectors is <see cref="JoinShape" />'s job since fisher#55, because a chain has to compose
///         rung by rung rather than resolve in one step — but reading a constructed shape apart is
///         still the primitive that makes it possible, and it is the same three shapes it always was.
///     </para>
///     <para>
///         Ported in outline from Polecat's rewriter of the same name.
///     </para>
/// </remarks>
internal static class JoinResultSelectorRewriter
{
    /// <remarks>
    ///     <para>
    ///         Three shapes, because all three are ordinary ways to write a join's result: an anonymous
    ///         type, an object initialiser, and a constructor call. Only the first two carry
    ///         <see cref="NewExpression.Members" />; a positional record's <c>new Row(a.Name, c.Name)</c>
    ///         has none, so the names come from the constructor's parameters — which for a record
    ///         <em>are</em> its properties, differing only in case.
    ///     </para>
    ///     <para>
    ///         Hence the case-insensitive comparer. Matching ordinally would make a record work when
    ///         reached one way and not the other, which is worse than not supporting it.
    ///     </para>
    /// </remarks>
    public static Dictionary<string, Expression> MembersOf(Expression body)
    {
        var members = new Dictionary<string, Expression>(StringComparer.OrdinalIgnoreCase);

        switch (body)
        {
            case NewExpression { Members: not null } created:
                for (var i = 0; i < created.Members.Count; i++)
                {
                    members[created.Members[i].Name] = created.Arguments[i];
                }

                break;

            case NewExpression { Constructor: not null } constructed:
                var parameters = constructed.Constructor.GetParameters();

                for (var i = 0; i < parameters.Length && i < constructed.Arguments.Count; i++)
                {
                    if (parameters[i].Name is { } name)
                    {
                        members[name] = constructed.Arguments[i];
                    }
                }

                break;

            case MemberInitExpression initialized:
                foreach (var assignment in initialized.Bindings.OfType<MemberAssignment>())
                {
                    members[assignment.Member.Name] = assignment.Expression;
                }

                break;
        }

        return members;
    }
}
