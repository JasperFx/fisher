using System.Linq.Expressions;

namespace Fisher.Linq.Joins;

/// <summary>
///     One rung of a join chain: what the shape at that point is made of, phrased over the joined
///     documents themselves (fisher#55).
/// </summary>
/// <param name="Members">
///     Each member of the shape, by name, as an expression over the sides' parameters. <c>x.a</c> in the
///     next join's selectors resolves through this to <c>t0</c>, and <c>x.a.Name</c> to <c>t0.Name</c>.
/// </param>
/// <param name="Type">The CLR type of the shape, which is how a lambda written against it is spotted.</param>
/// <param name="Body">
///     The shape itself, already over the sides' parameters. Needed because the next join's selectors
///     may name the whole shape rather than a member of it — a <c>GroupJoin</c>'s own selector writes
///     <c>(y, waters) =&gt; new { y, waters }</c>, where <c>y</c> is this entire rung.
/// </param>
/// <remarks>
///     <para>
///         <b>This is the whole of what a second join needed.</b> One join can be translated by looking
///         at two lambdas, because both of their parameters are documents. A second join is written
///         against the shape the first produced — <c>x =&gt; x.b.CatchId</c> — so its outer key names no
///         document at all until that shape is resolved back to one. Composing rung by rung is what
///         turns an arbitrarily long chain into one lambda over N documents.
///     </para>
///     <para>
///         Two rungs are kept per join rather than one, because the two LINQ spellings put post-join
///         clauses in different places: method syntax's <c>Where</c> names the <em>result</em>, while
///         query syntax's <c>where</c> comes before its <c>select</c> and names the
///         <em>intermediate</em> the join operator built. Which one a lambda means is decided by its
///         parameter's type, never by trying one and falling back — guessing would be ambiguous
///         whenever the two happen to share a member name.
///     </para>
/// </remarks>
internal sealed record JoinShape(IReadOnlyDictionary<string, Expression> Members, Type Type, Expression Body)
{
    /// <summary>
    ///     Re-express <paramref name="body" /> — written against this shape — over the sides'
    ///     parameters, or null when some part of it cannot be.
    /// </summary>
    /// <param name="body">The expression to rewrite.</param>
    /// <param name="shape">The parameter standing for this shape in <paramref name="body" />.</param>
    /// <param name="direct">
    ///     Parameters that already stand for a document rather than for a shape — a
    ///     <c>SelectMany</c>'s second parameter is the matched inner row.
    /// </param>
    /// <remarks>
    ///     Returning null rather than throwing is deliberate: the caller usually has a second shape to
    ///     try, and only it knows which failure is worth a message. What null means in practice is that
    ///     something in the expression still names the shape — most often a <c>GroupJoin</c>'s group,
    ///     which is unmapped on purpose because the join has flattened it away.
    /// </remarks>
    public Expression? Rewrite(Expression body, ParameterExpression shape,
        IReadOnlyDictionary<ParameterExpression, Expression>? direct = null)
    {
        var rewritten = new Substitution(shape, Members, direct).Visit(body);

        return References.Any(rewritten, shape) ? null : rewritten;
    }

    /// <summary>
    ///     The shape a lambda body describes, given that its own parameters have already been resolved.
    /// </summary>
    /// <param name="body">The constructed shape — an anonymous type, an initialiser or a constructor call.</param>
    /// <param name="unresolved">
    ///     A parameter that stands for nothing a joined row can answer, whose members are therefore left
    ///     out of the map. This is a <c>GroupJoin</c>'s group.
    /// </param>
    /// <remarks>
    ///     <b>Leaving the group out is what refuses a question about it, rather than answering it
    ///     wrongly.</b> A <c>GroupJoin</c>'s second parameter is the whole group, and a SQL join has
    ///     already flattened that into one row per match — so <c>x.catches.Count()</c> would otherwise
    ///     silently become a count of the single matched row. Absent from the map, it survives the
    ///     rewrite still naming the shape's own parameter, which is exactly the condition
    ///     <see cref="Rewrite" /> reports as untranslatable.
    /// </remarks>
    public static JoinShape For(Expression body, ParameterExpression? unresolved = null)
    {
        var members = JoinResultSelectorRewriter.MembersOf(body);

        if (unresolved is not null)
        {
            foreach (var name in members
                         .Where(pair => References.Any(pair.Value, unresolved))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                members.Remove(name);
            }
        }

        return new JoinShape(members, body.Type, body);
    }

    private sealed class Substitution : ExpressionVisitor
    {
        private readonly ParameterExpression _shape;
        private readonly IReadOnlyDictionary<string, Expression> _members;
        private readonly IReadOnlyDictionary<ParameterExpression, Expression>? _direct;

        public Substitution(ParameterExpression shape,
            IReadOnlyDictionary<string, Expression> members,
            IReadOnlyDictionary<ParameterExpression, Expression>? direct)
        {
            _shape = shape;
            _members = members;
            _direct = direct;
        }

        protected override Expression VisitParameter(ParameterExpression node)
            => _direct is not null && _direct.TryGetValue(node, out var replacement)
                ? replacement
                : base.VisitParameter(node);

        protected override Expression VisitMember(MemberExpression node)
            => Resolve(node) ?? base.VisitMember(node);

        /// <summary>
        ///     <c>x.a.Name</c> becomes <c>t0.Name</c>, and <c>x.a</c> becomes <c>t0</c>.
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

            if (current != _shape || chain.Count == 0
                || !_members.TryGetValue(chain[0].Member.Name, out var resolved))
            {
                return null;
            }

            for (var i = 1; i < chain.Count; i++)
            {
                resolved = Fold(resolved, chain[i].Member);
            }

            return resolved;
        }

        /// <summary>
        ///     A member access on a shape that is right here in the expression, read off it rather than
        ///     left to be evaluated.
        /// </summary>
        /// <remarks>
        ///     <b>Third and later joins need this; the second does not.</b> A second join's shape holds
        ///     documents (<c>new { a = t0, c = t1 }</c>), so <c>x.c.WaterId</c> resolves to a plain
        ///     <c>t1.WaterId</c>. A third join's shape holds the second's shape, so <c>y.y.a.Name</c>
        ///     would resolve to <c>new { a = t0, c = t1 }.a.Name</c> — a legal expression tree that
        ///     evaluates correctly in memory and is not a member chain rooted at a parameter, which is
        ///     the only thing the member factories and the where parser can translate. Folding keeps
        ///     every resolved member a plain <c>side.Member</c> however deep the chain of shapes gets.
        /// </remarks>
        private static Expression Fold(Expression target, System.Reflection.MemberInfo member)
            => target is NewExpression or MemberInitExpression
               && JoinResultSelectorRewriter.MembersOf(target).TryGetValue(member.Name, out var inner)
                ? inner
                : Expression.MakeMemberAccess(target, member);
    }

    private sealed class References : ExpressionVisitor
    {
        private readonly ParameterExpression _parameter;
        private bool _found;

        private References(ParameterExpression parameter) => _parameter = parameter;

        public static bool Any(Expression body, ParameterExpression parameter)
        {
            var visitor = new References(parameter);
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
