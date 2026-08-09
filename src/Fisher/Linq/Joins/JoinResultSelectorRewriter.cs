using System.Linq.Expressions;

namespace Fisher.Linq.Joins;

/// <summary>
///     Collapses a join's two result selectors into one lambda over the two documents —
///     <c>(outer, inner) =&gt; result</c>.
/// </summary>
/// <remarks>
///     <para>
///         A SQL join hands back one row per match, holding both documents. What the caller wrote is
///         phrased against the intermediate shape the join operator produced — <c>temp.c.Name</c>,
///         where <c>temp</c> is <c>new { c, orders }</c> — so the two selectors have to be collapsed
///         into one before either can be applied to a row.
///     </para>
///     <para>
///         Both LINQ join spellings arrive here in the same shape, which is why one rewriter serves
///         both. A <c>GroupJoin</c>'s intermediate holds the outer row and the group, and the inner row
///         reaches the final selector as its own parameter; a plain <c>Join</c> written in query syntax
///         with any clause after it holds <em>both</em> rows in a transparent identifier, and the final
///         selector has one parameter. The map says which member is which side; where the inner row
///         comes from is then a detail.
///     </para>
///     <para>
///         Ported in outline from Polecat's rewriter of the same name, which is the same problem in the
///         same shape. The rewritten lambda is also what makes ordering after the join resolvable: it
///         is the only place a projected member's name and the document member behind it are both
///         visible.
///     </para>
/// </remarks>
internal static class JoinResultSelectorRewriter
{
    /// <param name="intermediate">The join operator's own result selector.</param>
    /// <param name="final">
    ///     What each row should become, phrased against the intermediate shape.
    /// </param>
    /// <param name="outerType">The outer document type.</param>
    /// <param name="innerType">The inner document type.</param>
    /// <param name="intermediateHoldsInnerRow">
    ///     Whether the intermediate selector's second parameter is one inner row — true for a plain
    ///     <c>Join</c>, false for a <c>GroupJoin</c>, whose second parameter is the whole group.
    /// </param>
    public static LambdaExpression Rewrite(LambdaExpression intermediate, LambdaExpression final,
        Type outerType, Type innerType, bool intermediateHoldsInnerRow)
    {
        var outer = Expression.Parameter(outerType, "outer");
        var inner = Expression.Parameter(innerType, "inner");

        return TryRewrite(intermediate, final, outer, inner, intermediateHoldsInnerRow, out var rewritten)
            ? rewritten!
            // Anything still phrased against the intermediate is something neither side can answer —
            // most often a question about the whole group, which the join has already flattened. Caught
            // here rather than left to Expression.Lambda, whose complaint is about an unbound variable
            // and names nothing the caller wrote.
            : throw new BadLinqExpressionException(
                $"Fisher cannot translate '{final.Body}' as a join's result. Each part of it has to "
                + "come from the outer document or from the matched inner one; the group itself is not "
                + "available, because a join returns one row per match rather than a group per outer "
                + "row.");
    }

    /// <summary>
    ///     The same rewrite over parameters the caller supplies, and without throwing.
    /// </summary>
    /// <remarks>
    ///     Ordering after a join needs both. The parameters have to be the ones the projection was
    ///     rewritten with, so that a resolved key can be told apart by reference rather than by type —
    ///     which a self-join makes ambiguous — and failing is an ordinary answer there, because an
    ///     ordering key may equally be phrased against the projected shape and is tried that way first.
    /// </remarks>
    public static bool TryRewrite(LambdaExpression intermediate, LambdaExpression final,
        ParameterExpression outer, ParameterExpression inner, bool intermediateHoldsInnerRow,
        out LambdaExpression? rewritten)
    {
        var sides = new Dictionary<string, ParameterExpression>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, source) in MembersOf(intermediate.Body))
        {
            if (source == intermediate.Parameters[0])
            {
                sides[name] = outer;
            }
            // A GroupJoin's second parameter is the group rather than a row, so the member holding it
            // is deliberately left unmapped: an expression still naming it is asking about rows the
            // join has flattened, and is refused rather than silently answered about the one matched
            // row. A plain Join's second parameter *is* the row.
            else if (intermediateHoldsInnerRow && source == intermediate.Parameters[1])
            {
                sides[name] = inner;
            }
        }

        var temp = final.Parameters[0];

        var rewriter = new BodyRewriter(sides, temp,
            final.Parameters.Count > 1 ? final.Parameters[1] : null, inner);

        var body = rewriter.Visit(final.Body);

        if (ReferencesParameter.Check(body, temp))
        {
            rewritten = null;
            return false;
        }

        rewritten = Expression.Lambda(body, outer, inner);
        return true;
    }

    /// <summary>
    ///     What each member of a constructed object was built from, by name.
    /// </summary>
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

    private sealed class BodyRewriter : ExpressionVisitor
    {
        private readonly Dictionary<string, ParameterExpression> _sides;
        private readonly ParameterExpression _temp;
        private readonly ParameterExpression? _innerSource;
        private readonly ParameterExpression _inner;

        public BodyRewriter(Dictionary<string, ParameterExpression> sides, ParameterExpression temp,
            ParameterExpression? innerSource, ParameterExpression inner)
        {
            _sides = sides;
            _temp = temp;
            _innerSource = innerSource;
            _inner = inner;
        }

        protected override Expression VisitParameter(ParameterExpression node)
            => node == _innerSource ? _inner : base.VisitParameter(node);

        protected override Expression VisitMember(MemberExpression node)
            => Resolve(node) ?? base.VisitMember(node);

        /// <summary>
        ///     <c>temp.c.Name</c> becomes <c>outer.Name</c>, and <c>temp.c</c> becomes <c>outer</c>.
        /// </summary>
        private Expression? Resolve(MemberExpression node)
        {
            var chain = new List<MemberExpression>();
            Expression? current = node;

            while (current is MemberExpression member)
            {
                chain.Insert(0, member);
                current = member.Expression;
            }

            if (current != _temp || chain.Count == 0
                || !_sides.TryGetValue(chain[0].Member.Name, out var side))
            {
                return null;
            }

            Expression resolved = side;

            for (var i = 1; i < chain.Count; i++)
            {
                resolved = Expression.MakeMemberAccess(resolved, chain[i].Member);
            }

            return resolved;
        }
    }

    private sealed class ReferencesParameter : ExpressionVisitor
    {
        private readonly ParameterExpression _parameter;
        private bool _found;

        private ReferencesParameter(ParameterExpression parameter)
        {
            _parameter = parameter;
        }

        public static bool Check(Expression body, ParameterExpression parameter)
        {
            var visitor = new ReferencesParameter(parameter);
            visitor.Visit(body);

            return visitor._found;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == _parameter)
            {
                _found = true;
            }

            return base.VisitParameter(node);
        }
    }
}
