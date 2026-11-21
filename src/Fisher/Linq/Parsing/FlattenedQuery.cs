using System.Linq.Expressions;
using Fisher.Linq.Members;
using Fisher.Linq.SqlGeneration;
using Weasel.Core;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.Parsing;

/// <summary>
///     <c>SelectMany(x =&gt; x.Collection)</c> over a document's own collection member, and the operators after it
///     (fisher#641, the parity of polecat#1016 with marten#5934, #5940 and #5941).
/// </summary>
/// <remarks>
///     <para>
///         The query before the <c>SelectMany</c> is an ordinary document statement: its filters, ordering and paging
///         decide which parents are flattened, so <c>Take</c>/<c>Skip</c> before it page the parents. It becomes a
///         subquery, numbered in its own order with <c>row_number()</c>, and each parent's collection is unrolled with
///         <c>json_each</c>:
///     </para>
///     <code>
///         select (__fi_p.data -> each_1.fullkey)
///         from (select data, row_number() over (order by …) as __fi_ord from fi_doc_x where … limit …) as __fi_p,
///              json_each(__fi_p.data, '$.children') as each_1
///         where each_1.key is not null and …
///         order by …, __fi_p.__fi_ord, each_1.key
///     </code>
///     <para>
///         Rows come back in LINQ's order: the parents' order, then each collection's own order. An <c>OrderBy</c>
///         after the <c>SelectMany</c> sorts in front of that, and LINQ's sort is stable, so it breaks ties.
///     </para>
///     <para>
///         Each element is read as its own JSON (<c>data -&gt; fullkey</c>) and deserialized with the store's
///         serializer, so an enum under either <c>EnumStorage</c>, a Guid, a decimal or a child document comes back as
///         loading the whole document would build it. A <c>Select</c> after the <c>SelectMany</c>, and a result
///         selector, run over those elements once they are read.
///     </para>
/// </remarks>
internal sealed class FlattenedQuery
{
    internal const string ParentAlias = "__fi_p";
    internal const string OrdinalColumn = "__fi_ord";

    private int _orderingGroupLength;
    private bool _paged;
    private bool _readsParentColumns;
    private readonly string _parentOrdering;
    private readonly IMemberResolver _parentMembers;

    /// <param name="parent">The document statement whose rows are flattened.</param>
    /// <param name="parentOrdering">Its <c>order by</c>, which numbers its rows, or empty.</param>
    /// <param name="parentMembers">The documents' members, read from the parent subquery's columns.</param>
    /// <param name="collection">The collection member being flattened.</param>
    /// <param name="parentType">The document type.</param>
    /// <param name="resultSelector">The result selector, or null.</param>
    public FlattenedQuery(Statement parent, string parentOrdering, IMemberResolver parentMembers,
        CollectionMember collection, Type parentType, LambdaExpression? resultSelector)
    {
        Parent = parent;
        _parentOrdering = parentOrdering;
        _parentMembers = parentMembers;
        Collection = collection;
        ParentType = parentType;
        ResultSelector = resultSelector;
    }

    /// <summary>
    ///     A <c>Select</c> followed by <c>Distinct()</c>, computed in SQL so the distinct values are the projected
    ///     ones, or null. When set, the rows are this projection's columns rather than the element.
    /// </summary>
    public SelectProjection? DistinctProjection { get; private set; }

    /// <summary>The document statement whose rows are flattened, selecting <c>data</c> and the ordinal.</summary>
    public Statement Parent { get; }

    public CollectionMember Collection { get; }

    public Type ParentType { get; }

    public Type ElementType => Collection.ElementType;

    /// <summary><c>SelectMany(x =&gt; x.Children, (x, c) =&gt; …)</c>'s second lambda, or null.</summary>
    public LambdaExpression? ResultSelector { get; }

    /// <summary>A <c>Select</c> after the <c>SelectMany</c>, applied to each row once it is read.</summary>
    public LambdaExpression? Selector { get; private set; }

    public List<ISqlFragment> Wheres { get; } = [];

    public List<(string Locator, bool Descending)> OrderBys { get; } = [];

    public int? Limit { get; private set; }

    public int? Offset { get; private set; }

    public bool IsDistinct { get; private set; }

    /// <summary>The element's own JSON, as stored.</summary>
    public string ElementJson => $"({ParentAlias}.data -> {Collection.Alias}.fullkey)";

    /// <summary>An operator written after the <c>SelectMany</c>.</summary>
    public void Apply(MethodCallExpression call)
    {
        switch (call.Method.Name)
        {
            case "Where":
                RefuseAfterPaging(call, "Where");
                Wheres.Add(WhereFor(Lambda(call)));
                break;

            case "OrderBy":
                AddOrdering(call, descending: false, primary: true);
                break;

            case "OrderByDescending":
                AddOrdering(call, descending: true, primary: true);
                break;

            case "ThenBy":
                AddOrdering(call, descending: false, primary: false);
                break;

            case "ThenByDescending":
                AddOrdering(call, descending: true, primary: false);
                break;

            case "Take":
                var take = (int)WhereClauseParser.ExtractValue(call.Arguments[1])!;
                Limit = Limit is { } limit ? Math.Min(limit, take) : take;
                _paged = true;
                break;

            case "Skip":
                var skip = (int)WhereClauseParser.ExtractValue(call.Arguments[1])!;
                if (Limit is { } current) Limit = Math.Max(0, current - skip);
                Offset = (Offset ?? 0) + skip;
                _paged = true;
                break;

            case "Distinct" when call.Arguments.Count == 1:
                if (ResultSelector is not null || _paged || (Selector is null && !Collection.HasScalarElements))
                {
                    throw new BadLinqExpressionException(
                        "Fisher supports Distinct() after a SelectMany over a collection of values (strings, "
                        + "numbers, enums, Guids), or after one Select over the elements, before any Take or Skip. "
                        + "Distinct over child documents or after a result selector is not translated.");
                }

                if (Selector is not null)
                {
                    DistinctProjection = SelectProjection.For(Selector, ResolverFor(Selector)).ComputedInSql()
                                         ?? throw new BadLinqExpressionException(
                                             $"Fisher cannot translate Distinct() after Select({Selector}) over a "
                                             + "SelectMany: the projected value has no exact translation to SQL, "
                                             + "and Distinct has to compare the value the Select computes. Project "
                                             + "members of the element, or integer arithmetic over them.");
                }

                IsDistinct = true;
                break;

            case "Select" when Lambda(call).Parameters.Count == 1:
                if (Selector is not null)
                {
                    throw new BadLinqExpressionException(
                        "Fisher supports one Select after a SelectMany. Combine the two into one projection.");
                }

                Selector = Lambda(call);
                break;

            default:
                throw new BadLinqExpressionException(
                    $"Fisher cannot translate '{call.Method.Name}' after a SelectMany over a document's collection. "
                    + "Supported after it are Where, the OrderBy family, Take, Skip, Distinct over values and one "
                    + "Select, with ToListAsync, the First/Single family, CountAsync, AnyAsync and the aggregates. "
                    + WhereClauseParser.Alternatives);
        }
    }

    /// <summary>The statement over the flattened rows, selecting the element (and the parent for a result selector).</summary>
    public Statement BuildStatement()
    {
        // Every column of the documents when a filter reads a member of the parent, which may be a duplicated
        // field's own column rather than a path into data.
        Parent.SelectColumns = $"{(_readsParentColumns ? "*" : "data")}, row_number() over ({_parentOrdering}) as {OrdinalColumn}";

        var statement = new Statement
        {
            FromFragment = new FlattenedFrom(Parent, Collection),
            SelectColumns = DistinctProjection is { } projection
                ? string.Join(", ", projection.Locators)
                : ResultSelector is null ? ElementJson : $"{ElementJson}, {ParentAlias}.data",
            IsDistinct = IsDistinct,
            Limit = Limit,
            Offset = Offset,
            NonStaleTimeout = Parent.NonStaleTimeout
        };

        // fisher#609, as for documents: a decimal column is deduplicated by its sort key
        if (DistinctProjection is { } distinct && !distinct.DistinctKeys.SequenceEqual(distinct.Locators))
        {
            statement.IsDistinct = false;
            statement.GroupBy = string.Join(", ", distinct.DistinctKeys);
        }

        statement.DocumentTypes.AddRange(Parent.DocumentTypes);

        // The phantom row json_each yields over a member stored as JSON null (see CollectionMember).
        statement.Wheres.Add(new WhereFragment($"{Collection.Alias}.key is not null"));
        statement.Wheres.AddRange(Wheres);

        statement.OrderBys.AddRange(OrderBys);

        // LINQ's order within any ties: the parents', then the collection's own. Not under Distinct, whose rows are
        // values that no longer belong to one parent.
        if (!IsDistinct)
        {
            statement.OrderBys.Add(($"{ParentAlias}.{OrdinalColumn}", false));
            statement.OrderBys.Add(($"{Collection.Alias}.key", false));
        }

        return statement;
    }

    /// <summary>The member a lambda over the element reads, for an aggregate.</summary>
    public IQueryableMember AggregatedMember(LambdaExpression selector, string function)
    {
        if (Selector is not null || ResultSelector is not null)
        {
            throw new BadLinqExpressionException(
                $"Fisher cannot answer {function}Async after a Select or a result selector over a SelectMany. "
                + $"Aggregate the elements directly, as in SelectMany(x => x.Values).{function}Async(v => v).");
        }

        var resolver = ResolverFor(selector);
        var body = StripConvert(selector.Body);

        return body switch
        {
            ParameterExpression parameter => resolver.ResolveParameter(parameter)
                                             ?? throw new BadLinqExpressionException(
                                                 $"Cannot {function} '{selector}': the elements are child documents. "
                                                 + "Aggregate one of their members."),
            MemberExpression member => resolver.ResolveMember(member),
            _ => throw new BadLinqExpressionException(
                $"{function}Async after a SelectMany is only supported over the element or one of its members.")
        };
    }

    private IMemberResolver ResolverFor(LambdaExpression lambda)
        => Collection.CreateElementResolver(lambda.Parameters[0]);

    /// <summary>
    ///     A <c>Where</c> after the <c>SelectMany</c>, over whatever its rows are by then.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Over the element it is translated as it stands. After a <c>Select</c> or a result selector the rows
    ///         are computed once they are read, so the predicate is rewritten back onto what they are computed from:
    ///         the row parameter is replaced by the <c>Select</c>'s body, and then by the result selector's, and a
    ///         member of an anonymous type or an object initializer by the expression that set it. What is left reads
    ///         the element and the parent document, as in
    ///         <c>SelectMany(x =&gt; x.Lines, (x, l) =&gt; new { x.Number, l.Sku }).Where(r =&gt; r.Number &gt; 2)</c>,
    ///         which filters on <c>x.Number &gt; 2</c>. A member set through a constructor's arguments cannot be
    ///         traced back, and is refused by the resolver.
    ///     </para>
    ///     <para>
    ///         Filtering before the projection is the same answer LINQ gives after it, because a <c>Where</c> keeps or
    ///         drops whole rows. Paging is the exception, which is why a <c>Where</c> after <c>Take</c> or
    ///         <c>Skip</c> stays refused.
    ///     </para>
    /// </remarks>
    private ISqlFragment WhereFor(LambdaExpression predicate)
    {
        var body = predicate.Body;
        var row = predicate.Parameters[0];

        if (Selector is not null)
        {
            body = RowSubstitution.Replace(body, row, Selector.Body);
            row = Selector.Parameters[0];
        }

        if (ResultSelector is null)
        {
            return new WhereClauseParser(Collection.CreateElementResolver(row)).Parse(body);
        }

        body = RowSubstitution.Replace(body, row, ResultSelector.Body);
        _readsParentColumns = true;

        var resolver = new RowMemberResolver(
            ResultSelector.Parameters[0], _parentMembers,
            ResultSelector.Parameters[1], Collection.CreateElementResolver(ResultSelector.Parameters[1]));

        return new WhereClauseParser(resolver).Parse(body);
    }

    private void RefuseAfterPaging(MethodCallExpression call, string what)
    {
        if (_paged)
        {
            throw new BadLinqExpressionException(
                $"Fisher cannot translate {what} after Take or Skip over a SelectMany. Write it before the paging.");
        }
    }

    private void RefuseAfter(MethodCallExpression call, string what)
    {
        if (ResultSelector is not null || Selector is not null)
        {
            throw new BadLinqExpressionException(
                $"Fisher cannot translate {what} after a Select or a result selector over a SelectMany, whose rows "
                + $"are computed once they are read. Write the {what} before the Select, over the element.");
        }

        if (_paged)
        {
            throw new BadLinqExpressionException(
                $"Fisher cannot translate {what} after Take or Skip over a SelectMany. Write it before the paging.");
        }

        if (IsDistinct && what != "Where")
        {
            throw new BadLinqExpressionException(
                $"Fisher cannot translate {what} after Distinct over a SelectMany. Order before the Distinct.");
        }
    }

    private void AddOrdering(MethodCallExpression call, bool descending, bool primary)
    {
        RefuseAfter(call, call.Method.Name);

        var lambda = Lambda(call);

        if (!new ParameterUse(lambda.Parameters[0]).FoundIn(lambda.Body))
        {
            // fisher#591's rule: a key that reads nothing from the row ties every row.
            LinqQueryParser.DropConstantKey(ref _orderingGroupLength, primary);
            return;
        }

        var index = LinqQueryParser.OrderingInsertionIndex(ref _orderingGroupLength, primary);
        OrderBys.Insert(index, (OrderingLocatorFor(lambda, call.Method.Name), descending));
    }

    private string OrderingLocatorFor(LambdaExpression lambda, string method)
    {
        var resolver = ResolverFor(lambda);
        var body = StripConvert(lambda.Body);

        // w => w.Length over a string element or a string member of one (marten#5941)
        if (body is MemberExpression { Member.Name: "Length" } length && length.Expression?.Type == typeof(string))
        {
            var target = length.Expression switch
            {
                ParameterExpression parameter => resolver.ResolveParameter(parameter),
                MemberExpression member => resolver.ResolveMember(member),
                _ => null
            };

            if (target is not null)
            {
                return $"length({target.RawLocator})";
            }
        }

        var resolved = body switch
        {
            ParameterExpression parameter => resolver.ResolveParameter(parameter),
            MemberExpression member when IndexedAccess.RootOf(member) is not null => resolver.ResolveMember(member),
            _ when IndexedAccess.IsIndex(body) && IndexedAccess.RootOf(body) is not null => resolver.ResolveIndexed(body),
            _ => null
        };

        // fisher#666 (marten#5968): a computed key over the element, -c.Number or c.Name.ToUpper(), translated
        // over the element's own locators or refused by name.
        if (resolved is null)
        {
            return OrderingKeys.Translate(lambda.Body, lambda, resolver,
                $"Fisher cannot translate {method}({lambda}) after a SelectMany");
        }

        OrderingKeys.AssertConversionsKeepOrder(lambda, $"Fisher cannot translate {method}({lambda}) after a SelectMany");

        if (!resolved.AllowsRangeComparison)
        {
            throw new BadLinqExpressionException(
                $"Cannot order by the {resolved.MemberType.Name} value in SQLite: its stored form is not "
                + "order-preserving, so the rows would come back in a plausible but wrong order. For an enum, "
                + "storing it as an integer (StoreOptions.Serializer.EnumStorage) makes ordering meaningful.");
        }

        return DecimalPrecision.OrderingFor(resolved);
    }

    private static LambdaExpression Lambda(MethodCallExpression call)
    {
        var argument = call.Arguments[1];

        while (argument is UnaryExpression { NodeType: ExpressionType.Quote } quote)
        {
            argument = quote.Operand;
        }

        return argument as LambdaExpression
               ?? throw new BadLinqExpressionException($"'{call.Method.Name}' expects a lambda.");
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            expression = unary.Operand;
        }

        return expression;
    }

    private sealed class ParameterUse(ParameterExpression parameter) : ExpressionVisitor
    {
        private bool _found;

        public bool FoundIn(Expression expression)
        {
            Visit(expression);
            return _found;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            _found |= node == parameter;
            return node;
        }
    }

    /// <summary>
    ///     Replaces a row parameter with the expression the row was computed by, and a member read off an anonymous
    ///     type or an object initializer with the expression that set it.
    /// </summary>
    private sealed class RowSubstitution : ExpressionVisitor
    {
        private readonly ParameterExpression _row;
        private readonly Expression _computedBy;

        private RowSubstitution(ParameterExpression row, Expression computedBy)
        {
            _row = row;
            _computedBy = computedBy;
        }

        public static Expression Replace(Expression body, ParameterExpression row, Expression computedBy)
            => new RowSubstitution(row, computedBy).Visit(body)!;

        protected override Expression VisitParameter(ParameterExpression node)
            => node == _row ? _computedBy : node;

        protected override Expression VisitMember(MemberExpression node)
        {
            var target = Visit(node.Expression);

            switch (target)
            {
                case NewExpression { Members: { } members } created:
                    for (var i = 0; i < members.Count; i++)
                    {
                        if (SameMember(members[i], node.Member)) return created.Arguments[i];
                    }

                    break;

                case MemberInitExpression initialized:
                    foreach (var binding in initialized.Bindings)
                    {
                        if (binding is MemberAssignment assignment && SameMember(assignment.Member, node.Member))
                        {
                            return assignment.Expression;
                        }
                    }

                    break;
            }

            return node.Update(target);
        }

        // An anonymous type's constructor names its property getters rather than its properties
        private static bool SameMember(System.Reflection.MemberInfo left, System.Reflection.MemberInfo right)
            => left == right || (left.Name == right.Name && left.DeclaringType == right.DeclaringType)
                             || (left is System.Reflection.MethodInfo getter && getter.Name == "get_" + right.Name
                                 && getter.DeclaringType == right.DeclaringType);
    }

    /// <summary>
    ///     Resolves a member of a result selector's two parameters: the document's from the parent subquery, the
    ///     element's from <c>json_each</c>.
    /// </summary>
    private sealed class RowMemberResolver(
        ParameterExpression parent, IMemberResolver parentMembers,
        ParameterExpression element, IMemberResolver elementMembers) : IMemberResolver
    {
        public IQueryableMember ResolveMember(MemberExpression expression)
            => SideOf(expression).ResolveMember(expression);

        // fisher#666: an index into an array of the document or of the element.
        public IQueryableMember ResolveIndexed(Expression expression)
            => SideOf(expression).ResolveIndexed(expression);

        private IMemberResolver SideOf(Expression expression)
        {
            var current = IndexedAccess.RootOf(expression);

            if (current == parent) return parentMembers;
            if (current == element) return elementMembers;

            throw new BadLinqExpressionException(
                $"Fisher cannot translate '{expression}' in a Where after a SelectMany's result selector. A member "
                + "has to be traced back to the document or the element: one the result selector sets in an "
                + "anonymous type or an object initializer, not through a constructor's arguments.");
        }

        public IQueryableMember? ResolveParameter(ParameterExpression parameter)
            => parameter == element ? elementMembers.ResolveParameter(parameter) : null;
    }

    /// <summary>
    ///     <c>(&lt;parent statement&gt;) as __fi_p, json_each(__fi_p.data, '$.path') as each_1</c>.
    /// </summary>
    private sealed class FlattenedFrom(Statement parent, CollectionMember collection) : ISqlFragment
    {
        public void Apply(ICommandBuilder builder)
        {
            builder.Append("(");
            parent.Apply(builder);
            builder.Append($") as {ParentAlias}, {collection.JsonEachSource} as {collection.Alias}");
        }
    }
}
