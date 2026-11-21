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
///         reverse. Order matters for the ordering operators: a later <c>OrderBy</c> lands in front of
///         every earlier key, and a <c>ThenBy</c> right after the keys of the <c>OrderBy</c> it refines
///         (fisher#588).
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
    ///     The operators written after the query's <c>Take</c>/<c>Skip</c>, which apply to the page
    ///     rather than to the rows it was taken from — fisher#589. Null when nothing follows the paging.
    /// </summary>
    /// <remarks>
    ///     The provider renders the statement built from everything else as a subquery and applies
    ///     these on the outside. One boundary only: a further <c>Take</c>/<c>Skip</c> composes into the
    ///     boundary's own paging, and an operator after <em>that</em> is refused.
    /// </remarks>
    public PageBoundary? AfterPage { get; private set; }

    /// <summary>Whether the chain pages anywhere, before or after an <see cref="AfterPage" /> boundary.</summary>
    public bool IsPaged => Limit.HasValue || Offset.HasValue || AfterPage is not null;

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
        // fisher#589: an operator after Take/Skip applies to the page. Checked before everything
        // else because the join, grouping and soft-delete cases below would otherwise fold it into
        // the paged statement, where it runs before the LIMIT.
        if (FollowsPaging(call))
        {
            ApplyAfterPage(call);
            return;
        }

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

            // fisher#666 (marten#5910): .NET 10's LeftJoin is a plain Join that keeps an outer row with no
            // match — the same join the GroupJoin/SelectMany/DefaultIfEmpty spelling produces, with the result
            // selector already over the two documents.
            case "LeftJoin" when call.Method.DeclaringType == typeof(Queryable):
                ApplyGroupJoin(call, grouped: false);
                CurrentJoin!.IsLeftJoin = true;
                break;

            // The provider rewrites a RightJoin into a LeftJoin as the query is built (RightJoinRewriter), so
            // reaching here means the expression did not come through it.
            case "RightJoin" when call.Method.DeclaringType == typeof(Queryable):
                throw new BadLinqExpressionException(
                    "Fisher translates RightJoin by swapping it into a LeftJoin when the query is built, and this "
                    + "expression did not come through Fisher's query provider. Write it as a LeftJoin with the "
                    + "two sources swapped.");

            case "SelectMany" when CurrentJoin is { IsGrouped: true, FinalSelector: null }:
                ApplySelectMany(call);
                break;

            // fisher#641: a SelectMany over a document's collection is translated by the provider
            // (FlattenedQuery) for the terminals it answers. Reaching here means another one.
            case "SelectMany" when CurrentJoin is null:
                throw new BadLinqExpressionException(
                    "A SelectMany over a document's collection answers ToListAsync, the First/Single family, "
                    + "CountAsync/LongCountAsync, AnyAsync, the aggregates and ToSql. This operator is not "
                    + "supported over it. " + WhereClauseParser.Alternatives);

            // Query syntax puts a transparent identifier in a plain Join's result selector whenever
            // anything follows the join clause, and spells what the caller actually wanted as this
            // Select. Same two-selector shape a GroupJoin has, one call earlier.
            case "Select" when CurrentJoin is { IsGrouped: false, FinalSelector: null }:
                CurrentJoin.FinalSelector = UnwrapLambda(call);
                break;

            // Past the SelectMany the element is the projected shape, not a document — so an ordering
            // key names a member of it and has to be mapped back to whichever document it came from.
            // Kept as an expression for the provider, which is where the result selector is rewritten.
            // A new OrderBy is the primary key and everything collected so far only breaks its ties;
            // a ThenBy refines the newest OrderBy (fisher#588, see AddOrderingKey).
            case "OrderBy" when Joined:
                AddJoinOrdering(call, descending: false, primary: true);
                break;

            case "OrderByDescending" when Joined:
                AddJoinOrdering(call, descending: true, primary: true);
                break;

            case "ThenBy" when Joined:
                AddJoinOrdering(call, descending: false, primary: false);
                break;

            case "ThenByDescending" when Joined:
                AddJoinOrdering(call, descending: true, primary: false);
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
                AddRelevanceOrdering(call, descending: false, primary: true);
                break;

            case "ThenByRelevance":
                AddRelevanceOrdering(call, descending: false, primary: false);
                break;

            case "OrderByRelevanceDescending":
                AddRelevanceOrdering(call, descending: true, primary: true);
                break;

            case "ThenByRelevanceDescending":
                AddRelevanceOrdering(call, descending: true, primary: false);
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

            case "Select":
                ApplySelect(call);
                break;

            case "Distinct":
                RequireFaithfulProjection("Distinct()");
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

            // fisher#589: consecutive paging composes rather than overwriting — Take(2).Take(4) is
            // two rows and Take(3).Skip(2) is the third — so it stays one LIMIT/OFFSET and needs no
            // subquery.
            case "Take":
                (Limit, Offset) = ComposeTake(Limit, Offset, CountOf(call));
                break;

            case "Skip":
                (Limit, Offset) = ComposeSkip(Limit, Offset, CountOf(call));
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
                    $"Fisher cannot translate '{call.Method.Name}' to SQL. Supported operators are "
                    + "Where, Select, Distinct, DistinctBy, GroupBy, Join, LeftJoin, RightJoin, GroupJoin followed by SelectMany, "
                    + "OrderBy, OrderByDescending, ThenBy, ThenByDescending, the OrderByRelevance family, "
                    + "Take and Skip, plus Fisher's own query operators (MaybeDeleted, IsDeleted, "
                    + "DeletedSince/DeletedBefore, ModifiedSince/ModifiedBefore, AnyTenant, TenantIsOneOf, "
                    + "QueryForNonStaleData, Stats and Include). " + WhereClauseParser.Alternatives);
        }
    }

    /// <summary>
    ///     Whether <paramref name="call" /> changes which rows the chain yields, or their order, and so
    ///     has to apply to the page when it follows a <c>Take</c>/<c>Skip</c> — fisher#589.
    /// </summary>
    /// <remarks>
    ///     <c>Take</c>/<c>Skip</c> compose into the statement's own paging instead, until a boundary has
    ///     been opened. <c>Select</c> maps each row of the page to one result, so it means the same on
    ///     either side of the LIMIT and is left where it is, as are the markers that carry no SQL and a
    ///     terminal with no predicate. Everything else — the soft-delete, tenant and metadata filters
    ///     included — narrows or reorders rows, so it is either applied to the page or refused.
    /// </remarks>
    private bool FollowsPaging(MethodCallExpression call)
    {
        if (!IsPaged)
        {
            return false;
        }

        return call.Method.Name switch
        {
            "Take" or "Skip" => AfterPage is not null,
            "Select" => false,
            "Count" or "LongCount" or "Any" or "First" or "FirstOrDefault" or "Single" or "SingleOrDefault"
                => call.Arguments.Count > 1,
            nameof(IncludeExtensions.IncludeMarker) => call.Method.DeclaringType != typeof(IncludeExtensions),
            nameof(StatisticsExtensions.Stats) => call.Method.DeclaringType != typeof(StatisticsExtensions),
            nameof(Metadata.NonStaleDataExtensions.QueryForNonStaleData)
                => call.Method.DeclaringType != typeof(Metadata.NonStaleDataExtensions),
            _ => true
        };
    }

    /// <summary>
    ///     An operator written after <c>Take</c>/<c>Skip</c> — fisher#589.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Folding these into the paged statement is what made them silently wrong: a <c>Where</c>
    ///         after <c>Take(3)</c> filtered the table and then took three, where LINQ filters the three.
    ///         So they are collected on <see cref="AfterPage" />, and the provider wraps the paged
    ///         statement as a subquery to apply them.
    ///     </para>
    ///     <para>
    ///         <b>Only what a subquery answers correctly is accepted</b>, and the rest is refused by name.
    ///         The page is read through its document columns, so a predicate or an ordering over a
    ///         document member resolves outside it exactly as it would against the table. A join's
    ///         locators are qualified with an alias that does not survive into the enclosing scope, a
    ///         grouped or deduplicated page has no document columns left, and a full-text rank needs the
    ///         FTS5 table joined to the document's rowid, which the page does not carry.
    ///     </para>
    /// </remarks>
    private void ApplyAfterPage(MethodCallExpression call)
    {
        var name = call.Method.Name;
        var page = OpenPage(name);

        switch (name)
        {
            case "Take":
                (page.Limit, page.Offset) = ComposeTake(page.Limit, page.Offset, CountOf(call));
                return;

            case "Skip":
                (page.Limit, page.Offset) = ComposeSkip(page.Limit, page.Offset, CountOf(call));
                return;

            case "Where":
            case "Count":
            case "LongCount":
            case "Any":
            case "First":
            case "FirstOrDefault":
            case "Single":
            case "SingleOrDefault":
                page.Wheres.Add(_whereParser.Parse(UnwrapLambda(call).Body));
                return;

            // A ThenBy can only follow an OrderBy, and an OrderBy after the paging is the page's.
            case "OrderBy":
            case "ThenBy":
                page.OrderBys.Add((OrderingLocatorFor(call), false));
                return;

            case "OrderByDescending":
            case "ThenByDescending":
                page.OrderBys.Add((OrderingLocatorFor(call), true));
                return;

            case "Distinct" when Projection is not null:
                page.IsDistinct = true;
                return;

            case "Distinct":
                throw new BadLinqExpressionException(
                    "Distinct() after Take/Skip needs a Select before it. Over whole documents it would "
                    + "compare serialized JSON strings rather than documents; use DistinctBy before the "
                    + "paging to deduplicate documents by a member.");

            default:
                throw AfterPagingRefusal(name,
                    "Fisher applies only Where, the OrderBy family, Distinct after a Select, a terminal's "
                    + "predicate and further Take/Skip to a page. Apply it before the Take/Skip.");
        }
    }

    /// <summary>The boundary an operator after the paging lands on, opened by the first one.</summary>
    private PageBoundary OpenPage(string name)
    {
        if (AfterPage is not null)
        {
            if (name is not ("Take" or "Skip") && (AfterPage.Limit.HasValue || AfterPage.Offset.HasValue))
            {
                throw AfterPagingRefusal(name,
                    "This query already applies operators to one page and then pages again; Fisher "
                    + "supports one level of operators over a page.");
            }

            return AfterPage;
        }

        if (Joins.Count > 0)
        {
            throw AfterPagingRefusal(name,
                "The page is a join, whose columns are qualified with table aliases that do not survive "
                + "into a subquery over it. Apply it before the Take/Skip.");
        }

        if (_grouping is not null || IsDistinct || DistinctByLocator is not null)
        {
            throw AfterPagingRefusal(name,
                "The page is grouped or deduplicated, so it has no document columns left for the "
                + "operator to read. Apply it before the Take/Skip.");
        }

        if (RequiresFullTextJoin)
        {
            throw AfterPagingRefusal(name,
                "The page ranks or projects a full-text match, which needs the FTS5 table joined to the "
                + "document table and cannot be carried through a subquery. Apply it before the "
                + "Take/Skip.");
        }

        AfterPage = new PageBoundary();
        return AfterPage;
    }

    private static BadLinqExpressionException AfterPagingRefusal(string name, string reason)
        => new($"Fisher cannot apply '{name}' after Take/Skip. LINQ applies it to the page rather than to "
               + $"the rows the page was taken from, which needs the paged query as a subquery. {reason}");

    private static int CountOf(MethodCallExpression call)
        => Math.Max(0, (int)WhereClauseParser.ExtractValue(call.Arguments[1])!);

    /// <summary><c>Take(n)</c> over the paging already in place: the smaller of the two limits.</summary>
    private static (int? Limit, int? Offset) ComposeTake(int? limit, int? offset, int count)
        => (limit.HasValue ? Math.Min(limit.Value, count) : count, offset);

    /// <summary>
    ///     <c>Skip(k)</c> over the paging already in place: the offsets add, and a limit already in place
    ///     loses the rows skipped from its front.
    /// </summary>
    private static (int? Limit, int? Offset) ComposeSkip(int? limit, int? offset, int count)
        => (limit.HasValue ? Math.Max(limit.Value - count, 0) : null, (offset ?? 0) + count);

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
        // fisher#609: a decimal groups by its sort key, and g.Key reads back the number as written,
        // which every row of the group holds up to its scale.
        var exact = DecimalPrecision.IsExact(_lastOrderingMember);
        _grouping = new GroupingTranslator(_memberFactory,
            exact ? DecimalPrecision.ExactKey(_lastOrderingMember!) : GroupByLocator, keyIsExactDecimal: exact);
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

        // A comparer is the sixth argument of every join operator. The join compares the keys' stored values
        // in SQL, so it would be silently ignored, which is a wrong answer for a case-insensitive comparer.
        if (call.Arguments.Count > 5)
        {
            throw new BadLinqExpressionException(
                $"Fisher cannot translate a {call.Method.Name} with a key comparer. The join compares the keys' "
                + "stored values in SQL, so the comparer would be ignored. Normalize the keys on write, or join in "
                + "memory.");
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

        // fisher#220: a projected snippet or highlight reads a value the match computes, which needs
        // the FTS5 table joined for the same reason bm25 does.
        if (Projection.RequiresFullTextJoin)
        {
            RequiresFullTextJoin = true;
        }
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
    private void AddOrdering(MethodCallExpression call, bool descending, bool primary)
    {
        // fisher#591, marten#5950: a key that reads nothing from the row is the same for every row, so
        // every row ties on it and the keys after it decide, which is what LINQ does with it too.
        // Dropped rather than resolved, because resolving it walked a static or a captured local as a
        // JSON path and sorted by whichever document member shared its name.
        if (IsConstantKey(UnwrapLambda(call)))
        {
            DropConstantKey(ref _orderingGroupLength, primary);
            return;
        }

        AddOrderingKey(OrderingLocatorFor(call), descending, _lastOrderingMember, primary);
    }

    /// <summary>The join path's twin of <see cref="AddOrderingKey" />, over the post-join shape.</summary>
    private void AddJoinOrdering(MethodCallExpression call, bool descending, bool primary)
    {
        var join = CurrentJoin!;
        var length = join.OrderingGroupLength;

        // fisher#596: the document path's rule (fisher#591) on the join path. A key over the joined
        // shape that reads nothing from the row ties every row, so it is dropped and the keys after it
        // decide. It used to reach ApplyJoinOrdering and be refused as "not a member of a joined
        // document", which refused a query LINQ answers. A Last over nothing but such keys then meets
        // the ordinary "requires an OrderBy" refusal, since no key reaches the statement.
        if (IsConstantKey(UnwrapLambda(call)))
        {
            DropConstantKey(ref length, primary);
            join.OrderingGroupLength = length;
            return;
        }

        join.OrderBys.Insert(OrderingInsertionIndex(ref length, primary), (UnwrapLambda(call), descending));
        join.OrderingGroupLength = length;
    }

    /// <summary>
    ///     Records one ordering key, keeping <see cref="OrderByMembers" /> parallel to
    ///     <see cref="OrderBys" />.
    /// </summary>
    /// <remarks>
    ///     <b>An <c>OrderBy</c> goes in front of every key collected so far; a <c>ThenBy</c> goes
    ///     straight after the keys of the <c>OrderBy</c> it refines</b> (fisher#588, marten#5959).
    ///     LINQ's <c>OrderBy</c> is a stable re-sort, so <c>OrderBy(a).OrderBy(b)</c> means
    ///     <c>b, a</c>: the newest key is the primary one and the earlier keys only break its ties. Appending both kinds, which this used to do, rendered
    ///     <c>order by a, b</c> — a plausible order and the wrong one, with nothing to signal it.
    /// </remarks>
    private void AddOrderingKey(string locator, bool descending, IQueryableMember? member, bool primary)
    {
        var index = OrderingInsertionIndex(ref _orderingGroupLength, primary);
        OrderBys.Insert(index, (locator, descending));
        OrderByMembers.Insert(index, member);
    }

    /// <summary>How many keys the newest <c>OrderBy</c> and its <c>ThenBy</c>s have contributed.</summary>
    private int _orderingGroupLength;

    /// <summary>
    ///     Where the next ordering key goes: an <c>OrderBy</c> at the front, a <c>ThenBy</c> straight
    ///     after the keys of the <c>OrderBy</c> it refines.
    /// </summary>
    /// <remarks>
    ///     A <c>ThenBy</c> does not simply append. <c>OrderBy(a).OrderBy(b).ThenBy(c)</c> means
    ///     <c>b, c, a</c>: the <c>ThenBy</c> refines the newest <c>OrderBy</c>, and the older key still only
    ///     breaks the ties of both. Appending would render <c>b, a, c</c>.
    /// </remarks>
    internal static int OrderingInsertionIndex(ref int groupLength, bool primary)
    {
        if (primary)
        {
            groupLength = 1;
            return 0;
        }

        return groupLength++;
    }

    /// <summary>
    ///     What a dropped constant ordering key (fisher#591) does to the re-sort rule (fisher#588).
    /// </summary>
    /// <remarks>
    ///     A constant <c>OrderBy</c> still re-sorts: <c>OrderBy(a).OrderBy(constant).ThenBy(b)</c> means
    ///     <c>b, a</c>, so it starts a new group with no keys in it, and the <c>ThenBy</c> that follows goes
    ///     in front of <c>a</c>. Leaving the group length alone would render <c>a, b</c>. A constant
    ///     <c>ThenBy</c> contributes nothing and changes nothing.
    /// </remarks>
    internal static void DropConstantKey(ref int groupLength, bool primary)
    {
        if (primary)
        {
            groupLength = 0;
        }
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
    private void AddRelevanceOrdering(MethodCallExpression call, bool descending, bool primary)
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

        AddOrderingKey(FullTextRank.Locator(table, weights), descending, null, primary);
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

        if (GroupProjection is null && Projection is null)
        {
            return LocatorFor(call, ordering: true);
        }

        var body = lambda.Body;

        while (body is UnaryExpression { NodeType: ExpressionType.Convert } unary)
        {
            body = unary.Operand;
        }

        if (body == lambda.Parameters[0])
        {
            // fisher#590: the one column is only the value being ordered when the projection copies it.
            RequireFaithfulProjection($"{call.Method.Name}({lambda})");

            var columns = GroupProjection?.Columns ?? Projection!.Locators;

            if (columns.Length == 1)
            {
                return columns[0];
            }
        }

        throw new BadLinqExpressionException(
            $"Fisher cannot order by '{lambda.Body}' after a Select. Only OrderBy(x => x) over a "
            + "single-value projection is supported; order before the Select to sort by a document "
            + "member, or by the group's key or an aggregate.");
    }

    /// <summary>
    ///     Makes sure an operator acting on the projection's columns acts on the values the caller sees —
    ///     fisher#590.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>Distinct()</c> renders <c>select distinct &lt;columns&gt;</c> and identity ordering orders
    ///         by the one column. Both are right only while the columns are the result. After
    ///         <c>Select(x =&gt; x.Number % 3)</c> the column is <c>Number</c>, so both used to act on it and
    ///         come back plausibly wrong: five "distinct" values where there are three, an order that is
    ///         <c>Number</c>'s.
    ///     </para>
    ///     <para>
    ///         So a projection that computes is either moved into SQL, for the integer arithmetic
    ///         <see cref="ComputedColumns" /> can prove matches .NET, or refused by name. A
    ///         <c>Select</c> that is never deduplicated or ordered keeps computing in .NET as before.
    ///     </para>
    /// </remarks>
    private void RequireFaithfulProjection(string operation)
    {
        if (Projection is { IsFaithful: false } projection)
        {
            Projection = projection.ComputedInSql() ?? throw ComputedProjectionRefused(operation, projection.Selector);
        }

        if (GroupProjection is { IsFaithful: false } grouped)
        {
            GroupProjection = grouped.ComputedInSql() ?? throw ComputedProjectionRefused(operation, grouped.Selector);
        }
    }

    private static BadLinqExpressionException ComputedProjectionRefused(string operation, LambdaExpression selector)
        => new($"Fisher cannot apply {operation} after Select({selector}): the projection computes its value in "
               + ".NET after the row is read, so SQL would act on the columns it read rather than on the values "
               + "it returns. Only integer %, / by a constant other than 0 and -1, and unary minus, over integer "
               + "members, the group's key or its aggregates, are computed in SQL. Project the members "
               + "themselves, or apply the operator in memory after the query.");

    /// <summary>
    ///     Resolves an ordering key, refusing one whose stored form does not sort — the same guard the
    ///     where parser applies to a range comparison, for the same reason.
    /// </summary>
    private string LocatorFor(MethodCallExpression call, bool ordering = false)
    {
        // fisher#591: an ordering key like this is dropped before it gets here. A GroupBy or a
        // DistinctBy over one would collapse every row into a single group or a single document, which
        // has no locator to stand for it, so it is refused rather than approximated.
        if (IsConstantKey(UnwrapLambda(call)))
        {
            throw new BadLinqExpressionException(
                $"Cannot translate {call.Method.Name}({UnwrapLambda(call)}): the key reads nothing from the "
                + "document, so it has the same value for every row and there is no column to key on. "
                + "Key on a document member, or compute the result in memory.");
        }

        var lambda = UnwrapLambda(call);
        var body = lambda.Body;

        while (body is UnaryExpression { NodeType: ExpressionType.Convert } unary)
        {
            body = unary.Operand;
        }

        IQueryableMember member;

        // fisher#666: x.Values[1] or x.Grid[1][0], an index into the document, is a member like any other.
        if (IndexedAccess.IsIndex(body) && IndexedAccess.RootOf(body) is not null)
        {
            member = _memberFactory.ResolveIndexed(body);
        }
        else if (body is MemberExpression memberExpression
                 && (!ordering || IndexedAccess.RootOf(memberExpression) is not null))
        {
            member = _memberFactory.ResolveMember(memberExpression);
        }
        else if (ordering)
        {
            // fisher#666: a computed key, -x.Number or x.Name.ToUpper(), translated or refused by name. It
            // has no member behind it, so keyset paging refuses it as it does a relevance rank.
            return OrderingKeys.Translate(lambda.Body, lambda, _memberFactory);
        }
        else
        {
            throw new BadLinqExpressionException(
                $"'{call.Method.Name}' is only supported over a document member.");
        }

        _lastOrderingMember = member;

        if (ordering)
        {
            OrderingKeys.AssertConversionsKeepOrder(lambda);
        }

        if (!member.AllowsRangeComparison)
        {
            throw new BadLinqExpressionException(
                $"Cannot order by the {member.MemberType.Name} member in SQLite: its stored form is not "
                + "order-preserving, so the rows would come back in a plausible but wrong order. For an "
                + "enum, storing it as an integer (StoreOptions.Serializer.EnumStorage) makes ordering "
                + "meaningful.");
        }

        // fisher#609: a decimal orders, groups and deduplicates by its exact value.
        return DecimalPrecision.OrderingFor(member);
    }

    private static bool IsConstantKey(LambdaExpression lambda)
    {
        var finder = new ParameterUse(lambda.Parameters[0]);
        finder.Visit(lambda.Body);
        return !finder.Found;
    }

    private sealed class ParameterUse(ParameterExpression parameter) : ExpressionVisitor
    {
        internal bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            Found |= node == parameter;
            return node;
        }
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

/// <summary>
///     What a query does to its page once the page has been taken — fisher#589. See
///     <see cref="LinqQueryParser.AfterPage" />.
/// </summary>
internal sealed class PageBoundary
{
    /// <summary>Predicates over the page's rows, a terminal's included.</summary>
    public List<ISqlFragment> Wheres { get; } = [];

    /// <summary>A re-sort of the page. The page's own ordering then only breaks ties, as in LINQ.</summary>
    public List<(string Locator, bool Descending)> OrderBys { get; } = [];

    /// <summary><c>Distinct()</c> over a projected page.</summary>
    public bool IsDistinct { get; set; }

    /// <summary>Paging applied to the page.</summary>
    public int? Limit { get; set; }

    /// <inheritdoc cref="Limit" />
    public int? Offset { get; set; }
}
