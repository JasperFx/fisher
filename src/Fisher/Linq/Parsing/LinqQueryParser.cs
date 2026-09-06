using System.Linq.Expressions;
using Fisher.Linq.Includes;
using Fisher.Linq.Joins;
using Fisher.Linq.Members;
using Fisher.Linq.SqlGeneration;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.Parsing;

/// <summary>
///     Which rows of a soft-deleted document type a query wants.
/// </summary>
internal enum SoftDeleteScope
{
    /// <summary>The default: <c>is_deleted = 0</c>.</summary>
    LiveOnly,

    /// <summary><c>MaybeDeleted()</c> — no filter at all.</summary>
    LiveAndDeleted,

    /// <summary><c>IsDeleted()</c>, <c>DeletedSince()</c>, <c>DeletedBefore()</c>.</summary>
    DeletedOnly
}

/// <summary>
///     Walks a LINQ method chain into a <see cref="Statement" />.
/// </summary>
/// <remarks>
///     <para>
///         The counterpart of Polecat's parser of the same name: <c>Where</c>, the four ordering
///         operators, <c>Take</c> and <c>Skip</c>, projection, grouping and the
///         <c>GroupJoin</c>/<c>SelectMany</c> pair. Anything it cannot translate is refused by name —
///         see <see cref="Statement" /> for what the SQL side of each looks like.
///     </para>
///     <para>
///         The chain arrives outermost-call-first, so it is walked to the source and then applied in
///         reverse. Order matters for <c>ThenBy</c>, which must land after the <c>OrderBy</c> it
///         refines.
///     </para>
/// </remarks>
internal class LinqQueryParser
{
    private readonly IMemberResolver _memberFactory;
    private readonly WhereClauseParser _whereParser;

    public LinqQueryParser(IMemberResolver memberFactory)
    {
        _memberFactory = memberFactory;
        _whereParser = new WhereClauseParser(memberFactory);
    }

    /// <summary>
    ///     The where fragments the chain produced, ANDed together by the statement.
    /// </summary>
    public List<ISqlFragment> Wheres { get; } = [];

    public List<(string Locator, bool Descending)> OrderBys { get; } = [];

    /// <summary>
    ///     The member behind each entry of <see cref="OrderBys" />, or null where there is none — an
    ///     ordering over a projection or a group aggregate resolves to an expression, not a member.
    /// </summary>
    /// <remarks>
    ///     Kept parallel rather than folded into <see cref="OrderBys" /> because only keyset paging
    ///     needs it: a cursor's values have to be typed by the member's CLR type on decode, and the
    ///     terminal key has to be checked for being the identity. Everything else wants the locator and
    ///     nothing more.
    /// </remarks>
    public List<IQueryableMember?> OrderByMembers { get; } = [];

    public int? Limit { get; private set; }

    public int? Offset { get; private set; }

    /// <summary>
    ///     Which soft-deleted rows the query asked for. The last such operator in the chain wins, so
    ///     <c>MaybeDeleted().IsDeleted()</c> is the deleted ones — reading left to right, as the chain
    ///     does.
    /// </summary>
    public SoftDeleteScope SoftDeleteScope { get; private set; } = SoftDeleteScope.LiveOnly;

    /// <summary>Set by <c>DeletedSince</c>; a lower bound on <c>deleted_at</c>.</summary>
    public DateTimeOffset? DeletedSince { get; private set; }

    /// <summary>Set by <c>DeletedBefore</c>; an upper bound on <c>deleted_at</c>.</summary>
    public DateTimeOffset? DeletedBefore { get; private set; }

    /// <summary>
    ///     Whether the chain used any soft-delete operator at all — asked so that using one against a
    ///     type that is not soft-deleted can be refused rather than silently ignored.
    /// </summary>
    public bool UsedSoftDeleteOperator { get; private set; }

    /// <summary>
    ///     The <c>Select</c> the chain ended with, or null when the query returns documents.
    /// </summary>
    public SelectProjection? Projection { get; private set; }

    /// <summary>Set by <c>Distinct()</c>.</summary>
    public bool IsDistinct { get; private set; }

    /// <summary>The <c>GROUP BY</c> key locator, or null when the query does not group.</summary>
    public string? GroupByLocator { get; private set; }

    /// <summary>The <c>Select</c> over the grouping — required once <c>GroupBy</c> is used.</summary>
    public GroupProjection? GroupProjection { get; private set; }

    /// <summary>The <c>HAVING</c> fragments, from any <c>Where</c> after the <c>GroupBy</c>.</summary>
    public List<ISqlFragment> Havings { get; } = [];

    /// <summary>Which tenants the query runs against. Defaults to the session's.</summary>
    public TenantScope TenantScope { get; private set; } = TenantScope.Current;

    /// <summary>The tenants named by <c>TenantIsOneOf</c>, when that is the scope.</summary>
    public string[]? TenantIds { get; private set; }

    /// <summary>A <c>last_modified</c> lower bound, from <c>ModifiedSince</c>.</summary>
    public DateTimeOffset? ModifiedSince { get; private set; }

    /// <summary>A <c>last_modified</c> upper bound, from <c>ModifiedBefore</c>.</summary>
    public DateTimeOffset? ModifiedBefore { get; private set; }

    /// <summary>Set by <c>QueryForNonStaleData</c>; how long to wait for the daemon before running.</summary>
    public TimeSpan? NonStaleTimeout { get; private set; }

    /// <summary>Set by <c>Stats</c>; where the unpaged total is to be written.</summary>
    public QueryStatistics? Statistics { get; private set; }

    private GroupingTranslator? _grouping;

    /// <summary>The key <c>DistinctBy</c> deduplicates on, or null.</summary>
    public string? DistinctByLocator { get; private set; }

    /// <summary>
    ///     The <c>GroupJoin(...).SelectMany(...)</c> pairs the chain used, in the order they were
    ///     written (fisher#25, extended to a chain by fisher#55).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Captured rather than translated. Resolving any of it needs each <em>inner</em> side's
    ///         member factory, and an inner document type is only known from its own <c>GroupJoin</c>
    ///         node, so the provider does the translating — the same division the parser already keeps
    ///         for a projection's compiled body.
    ///     </para>
    ///     <para>
    ///         <b>A list rather than one, and order is the whole of what it carries.</b> Each join after
    ///         the first is written against the shape the one before it produced, so a post-join
    ///         <c>Where</c> or <c>OrderBy</c> means whichever shape was current where it was written —
    ///         which is why they hang off the join they follow rather than off the parser.
    ///     </para>
    /// </remarks>
    public List<GroupJoinData> Joins { get; } = [];

    /// <summary>
    ///     Whether the chain carries an <c>Include()</c> — see fisher#204.
    /// </summary>
    /// <remarks>
    ///     The plans themselves are read straight off the expression tree by
    ///     <see cref="IncludePlans" />, since they contribute nothing to the SQL. What the parser needs
    ///     them for is the refusal in <see cref="RefuseIncompatibleIncludes" />: an include is resolved
    ///     against the query's <em>materialized documents</em>, so it has no meaning over a shape that
    ///     is not one.
    /// </remarks>
    public bool HasIncludes { get; private set; }

    /// <summary>The join the chain is currently adding clauses to, or null before the first.</summary>
    private GroupJoinData? CurrentJoin => Joins.Count == 0 ? null : Joins[^1];

    /// <summary>Whether the chain is past everything that shapes the current join's rows.</summary>
    private bool Joined => CurrentJoin is { IsComplete: true };

    public void Parse(Expression expression)
    {
        var calls = new List<MethodCallExpression>();

        var current = expression;
        while (current is MethodCallExpression call)
        {
            calls.Add(call);
            current = call.Arguments.Count > 0 ? call.Arguments[0] : null!;
        }

        // Outermost first on the way down, so apply from the source outward.
        for (var i = calls.Count - 1; i >= 0; i--)
        {
            Apply(calls[i]);
        }

        RefuseIncompatibleIncludes();
    }

    /// <summary>
    ///     An <c>Include()</c> only means something over a query that returns whole documents.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The include's identity values are read out of the query's own materialized rows by
    ///         running the caller's id source over them — see
    ///         <see cref="IncludePlan{TParent,TInclude}" /> — so a <c>Select</c>, a <c>GroupBy</c> or a
    ///         join leaves nothing to run it against: those produce values, group aggregates and
    ///         projected shapes respectively, none of which is the document the id source was written
    ///         for.
    ///     </para>
    ///     <para>
    ///         Refused rather than allowed to silently leave the destination list empty, which is the
    ///         house rule and is doubly right here — an unpopulated <c>IList</c> looks exactly like a
    ///         query that legitimately matched nothing.
    ///     </para>
    /// </remarks>
    private void RefuseIncompatibleIncludes()
    {
        if (!HasIncludes)
        {
            return;
        }

        if (Joins.Count > 0)
        {
            throw new BadLinqExpressionException(
                "Include() cannot be combined with a join. A joined query yields the projected shape "
                + "rather than documents of one type, so there is nothing for the include's id source "
                + "to read. Include on the outer query before the join, or on the inner query passed "
                + "to it.");
        }

        if (Projection is not null || GroupProjection is not null || GroupByLocator is not null)
        {
            throw new BadLinqExpressionException(
                "Include() cannot follow a Select or a GroupBy. Its related documents are fetched using "
                + "the identities carried by the rows the query returns, and a projected or grouped row "
                + "is not the document those identities live on. Include before the projection, or "
                + "project the results after they come back.");
        }
    }

    private void Apply(MethodCallExpression call)
    {
        switch (call.Method.Name)
        {
            case "GroupJoin":
                ApplyGroupJoin(call, grouped: true);
                break;

            // A plain Join is the same join with no grouping step — what query syntax emits when the
            // `join` clause has no `into`. Its result selector is already over the two documents, so
            // there is nothing for a SelectMany to flatten and none may follow.
            case "Join":
                ApplyGroupJoin(call, grouped: false);
                break;

            case "SelectMany" when CurrentJoin is { IsGrouped: true, FinalSelector: null }:
                ApplySelectMany(call);
                break;

            // Query syntax puts a transparent identifier in a plain Join's result selector whenever
            // anything follows the join clause, and spells what the caller actually wanted as this
            // Select. Same two-selector shape a GroupJoin has, one call earlier.
            case "Select" when CurrentJoin is { IsGrouped: false, FinalSelector: null }:
                CurrentJoin.FinalSelector = UnwrapLambda(call);
                break;

            // Past the SelectMany the element is the projected shape, not a document — so an ordering
            // key names a member of it and has to be mapped back to whichever document it came from.
            // Kept as an expression for the provider, which is where the result selector is rewritten.
            case "OrderBy" when Joined:
            case "ThenBy" when Joined:
                CurrentJoin!.OrderBys.Add((UnwrapLambda(call), false));
                break;

            case "OrderByDescending" when Joined:
            case "ThenByDescending" when Joined:
                CurrentJoin!.OrderBys.Add((UnwrapLambda(call), true));
                break;

            // A post-join predicate names the joined shape, so like the ordering keys it is kept as an
            // expression and resolved once the result selector has been rewritten onto the two
            // documents. It filters joined rows, which is what a `where` after a `join` clause means.
            case "Where" when Joined:
                CurrentJoin!.Wheres.Add(UnwrapLambda(call));
                break;

            // Everything else that resolves a member is refused past the join rather than resolved
            // against the projected shape, which has no columns of its own. Both halves of the join
            // are still queryable — the outer before the GroupJoin, the inner as the query passed to
            // it — and that is what the message says.
            case "Select" when Joined:
            case "GroupBy" when Joined:
            case "Distinct" when Joined:
            case "DistinctBy" when Joined:
                throw new BadLinqExpressionException(
                    $"Fisher cannot apply '{call.Method.Name}' to the result of a join: its members "
                    + "belong to the projected shape rather than to either document, so there is no "
                    + "column to resolve them to. Filter or project the outer query before the "
                    + "GroupJoin, or the inner query passed to it.");

            // A Where before the GroupBy filters rows; one after it filters groups. The chain is
            // walked source-outward, so which it is falls out of whether the key has been seen yet.
            case "Where" when _grouping is not null:
                Havings.Add(_grouping.ParseHaving(UnwrapLambda(call).Body, UnwrapLambda(call).Parameters[0]));
                break;

            case "Where":
                Wheres.Add(_whereParser.Parse(UnwrapLambda(call).Body));
                break;

            case "GroupBy":
                ApplyGroupBy(call);
                break;

            // fisher#220. Relevance is an ordering term like any other -- the locator is bm25() over
            // the joined FTS5 table rather than a member's column, and BuildStatement is what turns
            // the query's full-text predicate into that join. Placed with the rest of the family
            // because that is exactly what it is: OrderByRelevance().ThenBy(...) and
            // OrderBy(...).ThenByRelevance() both mean what they look like.
            case "OrderByRelevance":
            case "ThenByRelevance":
                AddRelevanceOrdering(call, descending: false);
                break;

            case "OrderByRelevanceDescending":
            case "ThenByRelevanceDescending":
                AddRelevanceOrdering(call, descending: true);
                break;

            case "OrderBy":
                AddOrdering(call, descending: false);
                break;

            case "OrderByDescending":
                AddOrdering(call, descending: true);
                break;

            case "ThenBy":
                AddOrdering(call, descending: false);
                break;

            case "ThenByDescending":
                AddOrdering(call, descending: true);
                break;

            case "Select":
                ApplySelect(call);
                break;

            case "Distinct":
                IsDistinct = true;
                break;

            case "DistinctBy" when Projection is not null:
                throw new BadLinqExpressionException(
                    "DistinctBy is for deduplicating documents by a member. After a Select, use "
                    + "Distinct().");

            case "DistinctBy":
                DistinctByLocator = LocatorFor(call);
                break;

            // Carries no SQL of its own: the plan hanging off this node is fetched by a second
            // statement once the query's rows are materialized. Matched on the declaring type for the
            // same reason the soft-delete markers below are.
            case nameof(IncludeExtensions.IncludeMarker)
                when call.Method.DeclaringType == typeof(IncludeExtensions):
                HasIncludes = true;
                break;

            case nameof(LinqExtensions.AnyTenant)
                when call.Method.DeclaringType == typeof(LinqExtensions):
                TenantScope = TenantScope.AnyTenant;
                break;

            case nameof(LinqExtensions.TenantIsOneOf)
                when call.Method.DeclaringType == typeof(LinqExtensions):
                TenantScope = TenantScope.NamedTenants;
                TenantIds = (string[])WhereClauseParser.ExtractValue(call.Arguments[1])!;
                break;

            case nameof(Metadata.MetadataExtensions.ModifiedSince)
                when call.Method.DeclaringType == typeof(Metadata.MetadataExtensions):
                ModifiedSince = (DateTimeOffset)WhereClauseParser.ExtractValue(call.Arguments[1])!;
                break;

            case nameof(Metadata.MetadataExtensions.ModifiedBefore)
                when call.Method.DeclaringType == typeof(Metadata.MetadataExtensions):
                ModifiedBefore = (DateTimeOffset)WhereClauseParser.ExtractValue(call.Arguments[1])!;
                break;

            case nameof(Metadata.NonStaleDataExtensions.QueryForNonStaleData)
                when call.Method.DeclaringType == typeof(Metadata.NonStaleDataExtensions):
                NonStaleTimeout = (TimeSpan)WhereClauseParser.ExtractValue(call.Arguments[1])!;
                break;

            case nameof(StatisticsExtensions.Stats)
                when call.Method.DeclaringType == typeof(StatisticsExtensions):
                Statistics = (QueryStatistics)WhereClauseParser.ExtractValue(call.Arguments[1])!;
                break;

            case "Take":
                Limit = (int)WhereClauseParser.ExtractValue(call.Arguments[1])!;
                break;

            case "Skip":
                Offset = (int)WhereClauseParser.ExtractValue(call.Arguments[1])!;
                break;

            // Matched on the declaring type as well as the name: these are Fisher's own marker
            // methods, and a caller's extension method that happened to share a name is not one.
            case nameof(SoftDeletes.SoftDeletedExtensions.MaybeDeleted)
                when call.Method.DeclaringType == typeof(SoftDeletes.SoftDeletedExtensions):
                MarkSoftDelete(SoftDeleteScope.LiveAndDeleted);
                break;

            case nameof(SoftDeletes.SoftDeletedExtensions.IsDeleted)
                when call.Method.DeclaringType == typeof(SoftDeletes.SoftDeletedExtensions):
                MarkSoftDelete(SoftDeleteScope.DeletedOnly);
                break;

            case nameof(SoftDeletes.SoftDeletedExtensions.DeletedSince)
                when call.Method.DeclaringType == typeof(SoftDeletes.SoftDeletedExtensions):
                MarkSoftDelete(SoftDeleteScope.DeletedOnly);
                DeletedSince = (DateTimeOffset)WhereClauseParser.ExtractValue(call.Arguments[1])!;
                break;

            case nameof(SoftDeletes.SoftDeletedExtensions.DeletedBefore)
                when call.Method.DeclaringType == typeof(SoftDeletes.SoftDeletedExtensions):
                MarkSoftDelete(SoftDeleteScope.DeletedOnly);
                DeletedBefore = (DateTimeOffset)WhereClauseParser.ExtractValue(call.Arguments[1])!;
                break;

            // Terminal operators are handled by the provider, which knows what shape of result to
            // ask the statement for; they contribute nothing to the WHERE/ORDER BY.
            case "Count":
            case "LongCount":
            case "Any":
            case "First":
            case "FirstOrDefault":
            case "Single":
            case "SingleOrDefault":
                ApplyTerminalPredicate(call);
                break;

            default:
                throw new BadLinqExpressionException(
                    $"Fisher cannot translate '{call.Method.Name}' to SQL yet. Supported operators are "
                    + "Where, Select, Distinct, DistinctBy, OrderBy, OrderByDescending, ThenBy, "
                    + "ThenByDescending, Take and Skip.");
        }
    }

    /// <summary>
    ///     A projection ends the chain as far as document members are concerned, so only one is
    ///     allowed and nothing may follow it that needs to resolve one.
    /// </summary>
    /// <remarks>
    ///     A second <c>Select</c> would have to resolve members of the <em>first</em> projection's
    ///     result, which is not a document and has no locators. Refused by name rather than producing a
    ///     confusing "no such member" from the member factory.
    /// </remarks>
    /// <summary>
    ///     One grouping key, resolved through the same member factory everything else uses.
    /// </summary>
    /// <remarks>
    ///     Only the single-key-selector overload. <c>GroupBy(key, element)</c> and the result-selector
    ///     overloads are refused because their element projections would have to be applied per row
    ///     inside a group, which is the one thing a <c>GROUP BY</c> has already collapsed.
    /// </remarks>
    private void ApplyGroupBy(MethodCallExpression call)
    {
        if (call.Arguments.Count != 2)
        {
            throw new BadLinqExpressionException(
                "Fisher supports GroupBy with a single key selector. The element- and result-selector "
                + "overloads would need the individual rows of a group, which GROUP BY has collapsed.");
        }

        GroupByLocator = LocatorFor(call);
        _grouping = new GroupingTranslator(_memberFactory, GroupByLocator);
    }

    /// <summary>
    ///     <c>GroupJoin(inner, outerKey, innerKey, (outer, group) =&gt; …)</c> — captured whole
    ///     (fisher#25).
    /// </summary>
    private void ApplyGroupJoin(MethodCallExpression call, bool grouped)
    {
        // fisher#55: a second join is joined onto the shape the first produced, not onto a document
        // table, so its outer key selector names a member of that shape. Resolving it is the provider's
        // job — see JoinShape — and all the parser has to do is keep them in order.
        if (CurrentJoin is { IsComplete: false })
        {
            throw new BadLinqExpressionException(
                $"A GroupJoin must be followed by SelectMany before a second '{call.Method.Name}'. "
                + "Without it the first join still yields a grouping per outer row, and there is no "
                + "one-row-per-match shape for the second to join onto.");
        }

        var arguments = call.Method.GetGenericArguments();

        Joins.Add(new GroupJoinData
        {
            InnerSource = call.Arguments[1],
            OuterKeySelector = LambdaAt(call, 2),
            InnerKeySelector = LambdaAt(call, 3),
            IntermediateSelector = LambdaAt(call, 4),
            IsGrouped = grouped,
            OuterType = arguments[0],
            InnerType = arguments[1]
        });
    }

    /// <summary>
    ///     The <c>SelectMany</c> that flattens a <c>GroupJoin</c> back into one row per match — which is
    ///     the shape a SQL join produces.
    /// </summary>
    /// <remarks>
    ///     The collection selector is where the join's kind is decided and nothing else: a bare
    ///     <c>temp.group</c> is an inner join and <c>temp.group.DefaultIfEmpty()</c> is a left one.
    ///     Anything else — a <c>Where</c>, a <c>Take</c>, an ordering over the group — is a question
    ///     about the rows inside one outer row's group, which a join has already flattened, so it is
    ///     refused rather than quietly ignored the way Polecat's <c>ContainsDefaultIfEmpty</c> scan
    ///     would.
    /// </remarks>
    private void ApplySelectMany(MethodCallExpression call)
    {
        if (call.Arguments.Count != 3)
        {
            throw new BadLinqExpressionException(
                "A GroupJoin must be followed by SelectMany with both a collection selector and a "
                + "result selector — GroupJoin(...).SelectMany(x => x.Group.DefaultIfEmpty(), "
                + "(x, inner) => ...).");
        }

        CurrentJoin!.IsLeftJoin = IsDefaultIfEmpty(LambdaAt(call, 1).Body);
        CurrentJoin.FinalSelector = LambdaAt(call, 2);
    }

    private static bool IsDefaultIfEmpty(Expression collectionSelector)
    {
        switch (collectionSelector)
        {
            case MemberExpression:
                return false;

            case MethodCallExpression { Method.Name: "DefaultIfEmpty", Arguments.Count: 1 } call
                when call.Arguments[0] is MemberExpression:
                return true;

            default:
                throw new BadLinqExpressionException(
                    $"Fisher cannot translate '{collectionSelector}' as a join's collection selector. "
                    + "It must be the group itself, or the group with DefaultIfEmpty() for a left join; "
                    + "anything else asks about the rows within one group, which the join has already "
                    + "flattened.");
        }
    }

    private static LambdaExpression LambdaAt(MethodCallExpression call, int index)
    {
        var argument = call.Arguments[index];

        while (argument is UnaryExpression { NodeType: ExpressionType.Quote } quote)
        {
            argument = quote.Operand;
        }

        return (LambdaExpression)argument;
    }

    private void ApplySelect(MethodCallExpression call)
    {
        if (_grouping is not null)
        {
            if (GroupProjection is not null)
            {
                throw new BadLinqExpressionException("Fisher supports one Select per query.");
            }

            var lambda = UnwrapLambda(call);
            GroupProjection = GroupProjection.For(lambda, _grouping);
            return;
        }

        if (Projection is not null)
        {
            throw new BadLinqExpressionException(
                "Fisher supports one Select per query. A second one would have to project members of "
                + "the first projection's result, which is not a document and has no columns.");
        }

        Projection = SelectProjection.For(UnwrapLambda(call), _memberFactory);
    }

    private void MarkSoftDelete(SoftDeleteScope scope)
    {
        UsedSoftDeleteOperator = true;
        SoftDeleteScope = scope;
    }

    /// <summary>
    ///     <c>First(x =&gt; ...)</c> and friends carry an optional predicate, which is a where clause by
    ///     another name.
    /// </summary>
    private void ApplyTerminalPredicate(MethodCallExpression call)
    {
        if (call.Arguments.Count > 1 && Joined)
        {
            throw new BadLinqExpressionException(
                $"'{call.Method.Name}' cannot take a predicate over the result of a join, whose members "
                + "belong to the projected shape rather than to either document. Filter the outer query "
                + "before the GroupJoin, or the inner query passed to it.");
        }

        if (call.Arguments.Count > 1)
        {
            Wheres.Add(_whereParser.Parse(UnwrapLambda(call).Body));
        }
    }

    /// <summary>
    ///     An ordering key, which after a <c>Select</c> is a key over the <em>projection</em> rather
    ///     than over the document.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Only the identity — <c>OrderBy(x =&gt; x)</c> over a single-column projection — is
    ///         supported, because that is the idiom
    ///         <c>Select(x =&gt; x.Species).Distinct().OrderBy(x =&gt; x)</c> and it maps to the one
    ///         column the statement already selects.
    ///     </para>
    ///     <para>
    ///         Ordering by a member of a <em>shaped</em> projection is refused, and deliberately so
    ///         rather than for want of effort: the mapping back from an anonymous type's member to a
    ///         locator only exists while the projection is a plain member-for-member copy, and
    ///         <c>SelectProjection</c>'s whole point is that it does not have to be. Ordering before the
    ///         <c>Select</c> always works and reads no worse.
    ///     </para>
    /// </remarks>
    private void AddOrdering(MethodCallExpression call, bool descending)
    {
        OrderBys.Add((OrderingLocatorFor(call), descending));
        OrderByMembers.Add(_lastOrderingMember);
    }

    /// <summary>
    ///     <c>OrderByRelevance</c> and its three siblings -- fisher#220.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     The locator is <c>bm25(&lt;alias&gt;)</c> against the alias <c>BuildStatement</c> joins the
    ///     FTS5 table under, so it is settled here even though the join is added there: the alias is a
    ///     constant, and keeping the locator's construction next to the other orderings is what lets
    ///     relevance travel through paging, reversal and the aggregate wraps without any of them
    ///     learning it is special.
    ///     </para>
    ///     <para>
    ///     <b>bm25() scores more negative for a better match</b>, so best-first is a plain ascending
    ///     sort and `descending` here means worst-first. The sign is FTS5's, not a choice.
    ///     </para>
    ///     <para>
    ///     <c>OrderByMembers</c> gets a null, which is what keyset pagination already does for any
    ///     ordering key that is not a document member -- a rank is not a value a cursor can carry.
    ///     </para>
    /// </remarks>
    private void AddRelevanceOrdering(MethodCallExpression call, bool descending)
    {
        RequiresFullTextJoin = true;

        var weights = call.Arguments.Count > 1
            ? WhereClauseParser.ExtractValue(call.Arguments[1]) as double[] ?? []
            : [];

        RelevanceWeights = weights;

        var mapping = _memberFactory.Mapping
                      ?? throw new BadLinqExpressionException(
                          "OrderByRelevance can only be used against a document type. It reads a "
                          + "document's FTS5 index, and this query is not over one.");

        var index = mapping.FullTextIndex
                    ?? throw new BadLinqExpressionException(
                        $"'{mapping.DocumentType.Name}' declares no full-text index, so there is "
                        + "nothing for OrderByRelevance to rank by.");

        if (weights.Length > 0 && weights.Length != index.ColumnNames.Length)
        {
            throw new BadLinqExpressionException(
                $"OrderByRelevance was given {weights.Length} column weight(s), but "
                + $"'{mapping.DocumentType.Name}'s full-text index covers {index.ColumnNames.Length}. "
                + "bm25() takes one weight per indexed column, in declaration order — pass one for "
                + "each, or none at all for FTS5's default of 1.0 each.");
        }

        var table = Weasel.Sqlite.SchemaUtils.QuoteName(
            Storage.FullText.FullTextSchema.TableNameFor(mapping).Name);

        OrderBys.Add((FullTextRank.Locator(table, weights), descending));
        OrderByMembers.Add(null);
    }

    /// <summary>
    ///     Set when something in the query needs a value the FTS5 match COMPUTES rather than just the
    ///     rows it matches, which is what makes the predicate a join instead of a sub-select --
    ///     fisher#220.
    /// </summary>
    public bool RequiresFullTextJoin { get; private set; }

    /// <summary>The per-column <c>bm25()</c> weights, empty for FTS5's default of 1.0 each.</summary>
    public double[] RelevanceWeights { get; private set; } = [];

    // fisher#220: the FTS5 table is joined under its own name rather than an alias, because SQLite
    // refuses an alias on the left of MATCH. See JoinClause.Alias.

    private IQueryableMember? _lastOrderingMember;

    private string OrderingLocatorFor(MethodCallExpression call)
    {
        _lastOrderingMember = null;

        var lambda = UnwrapLambda(call);

        // Still over the grouping — the Select has not been applied yet. The ordering key is the
        // group's key or an aggregate over it, and `OrderByDescending(g => g.Count())` is the reason
        // grouping is usually reached for at all.
        if (_grouping is not null && GroupProjection is null)
        {
            return _grouping.TryTranslate(lambda.Body, lambda.Parameters[0], out var sql)
                ? sql
                : throw new BadLinqExpressionException(
                    $"Cannot order a grouped query by '{lambda.Body}'. Order by the group's key or by "
                    + "an aggregate over it.");
        }

        var columns = GroupProjection?.Columns ?? Projection?.Locators;

        if (columns is null)
        {
            return LocatorFor(call);
        }

        var body = lambda.Body;

        while (body is UnaryExpression { NodeType: ExpressionType.Convert } unary)
        {
            body = unary.Operand;
        }

        if (body == lambda.Parameters[0] && columns.Length == 1)
        {
            return columns[0];
        }

        throw new BadLinqExpressionException(
            $"Fisher cannot order by '{lambda.Body}' after a Select. Only OrderBy(x => x) over a "
            + "single-value projection is supported; order before the Select to sort by a document "
            + "member, or by the group's key or an aggregate.");
    }

    /// <summary>
    ///     Resolves an ordering key, refusing one whose stored form does not sort — the same guard the
    ///     where parser applies to a range comparison, for the same reason.
    /// </summary>
    private string LocatorFor(MethodCallExpression call)
    {
        var body = UnwrapLambda(call).Body;

        while (body is UnaryExpression { NodeType: ExpressionType.Convert } unary)
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression memberExpression)
        {
            throw new BadLinqExpressionException(
                $"'{call.Method.Name}' is only supported over a document member.");
        }

        var member = _memberFactory.ResolveMember(memberExpression);
        _lastOrderingMember = member;

        if (!member.AllowsRangeComparison)
        {
            throw new BadLinqExpressionException(
                $"Cannot order by the {member.MemberType.Name} member in SQLite: its stored form is not "
                + "order-preserving, so the rows would come back in a plausible but wrong order. For an "
                + "enum, storing it as an integer (StoreOptions.Serializer.EnumStorage) makes ordering "
                + "meaningful.");
        }

        return member.TypedLocator;
    }

    private static LambdaExpression UnwrapLambda(MethodCallExpression call)
    {
        var argument = call.Arguments[^1];

        while (argument is UnaryExpression { NodeType: ExpressionType.Quote } quote)
        {
            argument = quote.Operand;
        }

        return (LambdaExpression)argument;
    }
}
