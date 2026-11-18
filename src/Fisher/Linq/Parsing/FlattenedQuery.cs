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

    public FlattenedQuery(Statement parent, CollectionMember collection, Type parentType,
        LambdaExpression? resultSelector)
    {
        Parent = parent;
        Collection = collection;
        ParentType = parentType;
        ResultSelector = resultSelector;
    }

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
                RefuseAfter(call, "Where");
                Wheres.Add(new WhereClauseParser(ResolverFor(Lambda(call))).Parse(Lambda(call).Body));
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
                if (Selector is not null || ResultSelector is not null || !Collection.HasScalarElements || _paged)
                {
                    throw new BadLinqExpressionException(
                        "Fisher supports Distinct() straight after a SelectMany over a collection of values "
                        + "(strings, numbers, enums, Guids), before any Select, Take or Skip. Distinct over child "
                        + "documents or projected values is not translated.");
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
        var statement = new Statement
        {
            FromFragment = new FlattenedFrom(Parent, Collection),
            SelectColumns = ResultSelector is null ? ElementJson : $"{ElementJson}, {ParentAlias}.data",
            IsDistinct = IsDistinct,
            Limit = Limit,
            Offset = Offset,
            NonStaleTimeout = Parent.NonStaleTimeout
        };

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
            MemberExpression member => resolver.ResolveMember(member),
            _ => null
        } ?? throw new BadLinqExpressionException(
            $"Fisher cannot translate {method}({lambda}) after a SelectMany. Order by the element, one of its "
            + "members, or a string's Length.");

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
