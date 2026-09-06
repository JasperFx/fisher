using System.Linq.Expressions;
using Fisher.Linq.Members;
using Fisher.Linq.SqlGeneration;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.Parsing.Methods;

/// <summary>
///     Shared shape tests and helpers for the collection sub-query parsers. Everything here is about
///     recognising "a document member that is a collection" from the expression tree alone, before any
///     member resolution happens.
/// </summary>
internal static class ChildCollections
{
    /// <summary>
    ///     Whether the expression is a member chain rooted at the query parameter whose CLR type is a
    ///     collection Fisher stores as a JSON array.
    /// </summary>
    public static bool IsCollectionDocumentMember(Expression expression)
        => StripConversions(expression) is MemberExpression member
           && IsDocumentMember(member)
           && CollectionMember.TryGetElementType(member.Type, out _);

    public static bool IsDocumentMember(Expression expression)
    {
        var current = StripConversions(expression) as MemberExpression;
        while (current != null)
        {
            if (current.Expression is ParameterExpression)
            {
                return true;
            }

            current = current.Expression as MemberExpression;
        }

        return false;
    }

    /// <summary>
    ///     Resolves the collection member behind an already shape-matched expression, refusing by name
    ///     when resolution lands on something that cannot be unrolled — a duplicated column being the
    ///     one way a collection-shaped member resolves to a non-collection.
    /// </summary>
    public static CollectionMember ResolveCollection(IMemberResolver memberFactory,
        Expression expression, string operatorName)
    {
        var member = memberFactory.ResolveMember((MemberExpression)StripConversions(expression));

        if (member is not CollectionMember collection)
        {
            throw new BadLinqExpressionException(
                $"Cannot translate {operatorName}() over the member '{expression}': it does not "
                + "resolve to a JSON array Fisher can unroll with json_each.");
        }

        return collection;
    }

    /// <summary>
    ///     The lambda argument of an <c>Any</c>/<c>All</c>/<c>Count</c> call, unwrapped from the
    ///     <c>Quote</c> node expression trees arrive in.
    /// </summary>
    public static LambdaExpression LambdaOf(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Quote or ExpressionType.Convert } unary)
        {
            expression = unary.Operand;
        }

        return (LambdaExpression)expression;
    }

    /// <summary>
    ///     Parses an element predicate into the fragment the sub-query's WHERE carries, after refusing
    ///     any reference that escapes the element's scope.
    /// </summary>
    public static ISqlFragment ParseElementPredicate(CollectionMember collection, LambdaExpression lambda,
        string operatorName)
    {
        var parameter = lambda.Parameters[0];
        OuterReferenceGuard.AssertScoped(lambda.Body, parameter, operatorName);

        var resolver = collection.CreateElementResolver(parameter);
        return new WhereClauseParser(resolver).Parse(lambda.Body);
    }

    public static Expression StripConversions(Expression expression)
    {
        while (true)
        {
            switch (expression)
            {
                case UnaryExpression { NodeType: ExpressionType.Convert } unary:
                    expression = unary.Operand;
                    continue;

                // The implicit array-to-span conversion the compiler inserts for
                // MemoryExtensions overloads — same unwrapping EnumerableContains does.
                case MethodCallExpression { Method.Name: "op_Implicit" or "op_Explicit" } conversion
                    when conversion.Arguments.Count == 1:
                    expression = conversion.Arguments[0];
                    continue;

                case MethodCallExpression { Method.Name: "AsSpan" or "AsMemory" } asSpan:
                    expression = asSpan.Object ?? asSpan.Arguments[0];
                    continue;

                default:
                    return expression;
            }
        }
    }
}

/// <summary>
///     Refuses a collection predicate that references anything other than the lambda's own element —
///     the outer document, or a captured element of an enclosing lambda.
/// </summary>
/// <remarks>
///     A whole-tree walk rather than a check at member-resolution time, because an escaped reference on
///     the <em>value</em> side of a comparison never reaches the resolver — it goes through closure
///     evaluation, which would throw an unhelpful "variable 'x' of type … is not defined" from deep in
///     expression compilation. Refusing up front names the actual problem. The correlated-EXISTS shape
///     could support outer <em>member</em> references one day; today they are refused rather than
///     mis-scoped.
/// </remarks>
internal sealed class OuterReferenceGuard : ExpressionVisitor
{
    private readonly HashSet<ParameterExpression> _inScope;
    private readonly string _operatorName;

    private OuterReferenceGuard(ParameterExpression parameter, string operatorName)
    {
        _inScope = [parameter];
        _operatorName = operatorName;
    }

    public static void AssertScoped(Expression body, ParameterExpression parameter, string operatorName)
        => new OuterReferenceGuard(parameter, operatorName).Visit(body);

    protected override Expression VisitLambda<T>(Expression<T> node)
    {
        foreach (var parameter in node.Parameters)
        {
            _inScope.Add(parameter);
        }

        var result = base.VisitLambda(node);

        foreach (var parameter in node.Parameters)
        {
            _inScope.Remove(parameter);
        }

        return result;
    }

    protected override Expression VisitParameter(ParameterExpression node)
    {
        if (!_inScope.Contains(node))
        {
            throw new BadLinqExpressionException(
                $"The {_operatorName}() predicate references '{node.Name}', which is outside the "
                + "collection element's scope. Only the element's own members can be used inside a "
                + "collection predicate; compare against locals or constants instead.");
        }

        return base.VisitParameter(node);
    }
}

/// <summary>
///     <c>x.Tags.Contains(value)</c> → <c>exists (select 1 from json_each(data, '$.tags') as each_1
///     where each_1.key is not null and each_1.value = @p0)</c>.
/// </summary>
/// <remarks>
///     <para>
///         The mirror image of <see cref="EnumerableContains" />: there the collection is an evaluated
///         value and the member is the needle; here the collection is the document member and the
///         needle is a value. Registered first of the two and claims every <c>Contains</c> whose
///         receiver is a collection-typed document member, so that the member-vs-member shape
///         (<c>x.Tags.Contains(x.Name)</c>) gets a refusal that names the problem instead of
///         <see cref="EnumerableContains" />'s complaint about evaluating the source.
///     </para>
///     <para>
///         The value goes through the <em>element member's</em> conversion — built by the same switch
///         as any document member of that type — which is what keeps an enum honouring the store's
///         <c>EnumStorage</c> and naming policy, a Guid matching its lowercase canonical text, and a
///         bool matching the stored 1/0.
///     </para>
/// </remarks>
internal sealed class CollectionContains : IMethodCallParser
{
    public bool Matches(MethodCallExpression expression)
    {
        if (expression.Method.Name != "Contains")
        {
            return false;
        }

        // Instance form on a collection — x.Tags.Contains(value). A string receiver belongs to
        // StringContains.
        if (expression.Object != null && expression.Arguments.Count == 1)
        {
            return expression.Method.DeclaringType != typeof(string)
                   && ChildCollections.IsCollectionDocumentMember(expression.Object);
        }

        // Static form — Enumerable.Contains(x.Tags, value), or the span overload for an array member.
        return expression.Object == null
               && expression.Arguments.Count == 2
               && ChildCollections.IsCollectionDocumentMember(expression.Arguments[0]);
    }

    public ISqlFragment Parse(IMemberResolver memberFactory, MethodCallExpression expression)
    {
        var receiver = expression.Object ?? expression.Arguments[0];
        var needle = expression.Object == null ? expression.Arguments[1] : expression.Arguments[0];

        if (ChildCollections.IsDocumentMember(needle))
        {
            throw new BadLinqExpressionException(
                $"Cannot translate '{expression}': Contains() over a collection member needs a value "
                + "that can be evaluated when the query is built, not another document member.");
        }

        var collection = ChildCollections.ResolveCollection(memberFactory, receiver, "Contains");

        var element = collection.ElementMember
                      ?? throw new BadLinqExpressionException(
                          $"Cannot translate '{expression}': Contains() over a collection of "
                          + $"{collection.ElementType.Name} elements has no single stored value to "
                          + "compare. Use Any(c => …) with a predicate on the element's members.");

        var value = element.ConvertValue(WhereClauseParser.ExtractValue(needle));

        ISqlFragment where = value is null
            ? new WhereFragment($"{element.RawLocator} is null")
            : new ComparisonFilter(element.TypedLocator, "=", value);

        return new ExistsSubQueryFilter(collection.JsonEachSource, collection.Alias, where);
    }
}

/// <summary>
///     <c>x.Children.Any()</c> and <c>x.Children.Any(c =&gt; …)</c> as correlated existence tests.
/// </summary>
/// <remarks>
///     Both forms answer false for an absent member and for one stored as JSON null — no elements is no
///     elements, however the nothing is spelled. The predicate form resolves the lambda's member
///     accesses against the <c>json_each</c> element (<c>json_extract(each_1.value, '$.port')</c>),
///     with everything outside the element's scope refused by <see cref="OuterReferenceGuard" />.
/// </remarks>
internal sealed class CollectionAny : IMethodCallParser
{
    public bool Matches(MethodCallExpression expression)
        => expression.Method.Name == "Any"
           && expression.Object == null
           && expression.Arguments.Count is 1 or 2
           && ChildCollections.IsCollectionDocumentMember(expression.Arguments[0]);

    public ISqlFragment Parse(IMemberResolver memberFactory, MethodCallExpression expression)
    {
        var collection = ChildCollections.ResolveCollection(memberFactory, expression.Arguments[0], "Any");

        if (expression.Arguments.Count == 1)
        {
            return new ExistsSubQueryFilter(collection.JsonEachSource, collection.Alias, null);
        }

        var lambda = ChildCollections.LambdaOf(expression.Arguments[1]);
        var where = ChildCollections.ParseElementPredicate(collection, lambda, "Any");

        return new ExistsSubQueryFilter(collection.JsonEachSource, collection.Alias, where);
    }
}

/// <summary>
///     <c>x.Children.All(c =&gt; …)</c> → <c>not exists (… where … and not (predicate))</c>.
/// </summary>
/// <remarks>
///     The double negation is the standard relational spelling of a universal quantifier, and it gives
///     the empty, absent and null collections the same vacuous truth <c>Enumerable.All</c> gives an
///     empty sequence. One SQL subtlety: a predicate that evaluates to NULL for some element (say a
///     null <c>Port</c> compared to a string) is <em>not true</em> for that element, and
///     <c>not (NULL)</c> is NULL rather than true — so the negated arm wraps the predicate in a
///     <c>coalesce</c>-free <c>is not true</c>-equivalent by testing <c>not (p)</c> alongside
///     <c>(p) is null</c>.
/// </remarks>
internal sealed class CollectionAll : IMethodCallParser
{
    public bool Matches(MethodCallExpression expression)
        => expression.Method.Name == "All"
           && expression.Object == null
           && expression.Arguments.Count == 2
           && ChildCollections.IsCollectionDocumentMember(expression.Arguments[0]);

    public ISqlFragment Parse(IMemberResolver memberFactory, MethodCallExpression expression)
    {
        var collection = ChildCollections.ResolveCollection(memberFactory, expression.Arguments[0], "All");

        var lambda = ChildCollections.LambdaOf(expression.Arguments[1]);
        var predicate = ChildCollections.ParseElementPredicate(collection, lambda, "All");

        // "some element fails the predicate" must catch the element for which the predicate is NULL —
        // in SQL that element neither passes nor fails, but All() means every element *passes*.
        var fails = new FailsPredicateFragment(predicate);

        return new ExistsSubQueryFilter(collection.JsonEachSource, collection.Alias, fails, negated: true);
    }

    /// <summary>
    ///     <c>((p) is null or not (p))</c> — true exactly when the element does not satisfy the
    ///     predicate, including when SQL three-valued logic makes the predicate NULL.
    /// </summary>
    private sealed class FailsPredicateFragment : ISqlFragment
    {
        private readonly ISqlFragment _predicate;

        public FailsPredicateFragment(ISqlFragment predicate)
        {
            _predicate = predicate;
        }

        public void Apply(Weasel.Core.ICommandBuilder builder)
        {
            builder.Append("((");
            _predicate.Apply(builder);
            builder.Append(") is null or not (");
            _predicate.Apply(builder);
            builder.Append("))");
        }
    }
}
