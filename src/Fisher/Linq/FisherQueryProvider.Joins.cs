using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Fisher.Internal;
using Fisher.Linq.Joins;
using Fisher.Linq.Members;
using Fisher.Linq.Parsing;
using Fisher.Linq.SqlGeneration;
using Fisher.Storage;
using Weasel.Core.SqlGeneration;
using Weasel.Storage;

namespace Fisher.Linq;

/// <summary>
///     <c>GroupJoin(...).SelectMany(...)</c> over two document tables — fisher#25.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is the LINQ tier where SQLite is the easiest of the three dialects rather than the
///         hardest.</b> A join between two document tables is
///         <c>join fi_doc_order inner_t on json_extract(outer_t.data, '$.id') = inner_t.customer_id</c>
///         — no <c>OPENJSON</c>, no lateral join, and SQLite's planner will use an expression index on
///         either side if fisher#16 declared one.
///     </para>
///     <para>
///         <b>And it is worth more in an embedded store than in either sibling.</b> The usual argument
///         against joins in a document store is that a round trip is cheap next to a join's cost; here
///         there is no round trip to be cheap. The alternative is two statements and a client-side
///         stitch, which costs a second statement preparation and a full materialization of the inner
///         set — against one statement the planner can index.
///     </para>
///     <para>
///         The whole join lives on the ordinary <see cref="Statement" />, so <c>Count</c>, <c>Any</c>,
///         paging and <c>ToSql</c> serve it without knowing it is one. What is here is the translation:
///         the two key locators, the inner side's filters, the predicates and ordering keys written
///         after the join, and the reading of two documents out of one row.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Class-level: compiles the rewritten join result selector via Expression.Compile and closes the document-resolving helper over the inner document type via MakeGenericMethod. Both element types flow in from the caller's own GroupJoin call and are preserved per the AOT publishing guide.")]
[UnconditionalSuppressMessage("Trimming", "IL2060:DynamicallyAccessedMembers",
    Justification = "Class-level: MakeGenericMethod over a document type the caller named in its query.")]
public partial class FisherQueryProvider
{
    private const string OuterAlias = "outer_t";
    private const string InnerAlias = "inner_t";

    /// <summary>
    ///     Whether the chain joins, asked before it is parsed — see <see cref="BuildStatement" />.
    /// </summary>
    private static bool ContainsJoin(Expression expression)
    {
        var current = expression;

        while (current is MethodCallExpression call)
        {
            if (call.Method.Name is "GroupJoin" or "Join")
            {
                return true;
            }

            current = call.Arguments.Count > 0 ? call.Arguments[0] : null!;
        }

        return false;
    }

    /// <summary>
    ///     Refuse an operator that cannot be answered over a join, before anything asks the schema
    ///     about the join's result type.
    /// </summary>
    /// <remarks>
    ///     A joined query's element type is whatever the caller's result selector produced — an
    ///     anonymous type or a record, never a document — so an operator that reaches
    ///     <see cref="BuildStatement" /> with it asks for a mapping that cannot exist, and fails with a
    ///     message about identity members rather than about joins.
    /// </remarks>
    private void RefuseJoin(Expression expression, string operation, string instead)
    {
        if (ContainsJoin(expression))
        {
            throw new BadLinqExpressionException(
                $"Fisher cannot answer {operation} over a join, whose rows hold two documents rather "
                + $"than one. {instead}");
        }
    }

    private (Statement Statement, JoinPlan Plan)? JoinFor(Expression expression)
    {
        var (statement, _, _, join) = BuildStatement(SourceTypeFor(expression), expression);

        return join is null ? null : (statement, join);
    }

    /// <summary>
    ///     Turn the parsed join — either operator — into a <see cref="JoinClause" /> on the statement,
    ///     and return what reading the result needs.
    /// </summary>
    private JoinPlan ApplyJoin(Statement statement, GroupJoinData join, ISelectClause outerClause)
    {
        if (!join.IsComplete)
        {
            throw new BadLinqExpressionException(
                "A GroupJoin must be followed by SelectMany. On its own it yields one grouping per "
                + "outer row, which means reading every inner row of every group; the SelectMany is "
                + "what flattens it into the one-row-per-match shape a SQL join produces. Write "
                + "GroupJoin(...).SelectMany(x => x.Group, (x, inner) => ...), or add "
                + "DefaultIfEmpty() to the group for a left join.");
        }

        // Validated rather than trusted: the inner side has to be a Fisher queryable for there to be a
        // table to join to, and this is the message that says so.
        SourceTypeFor(join.InnerSource);

        var innerMapping = _session.Options.Schema.MappingFor(join.InnerType);
        var innerStorage = ((IStorageSession)_session).StorageFor(join.InnerType);

        if (innerStorage is not ISelectClause innerClause)
        {
            throw new BadLinqExpressionException(
                $"The storage for '{join.InnerType.Name}' cannot produce a select clause.");
        }

        var outerMembers = new MemberFactory(_session.Options,
            _session.Options.Schema.MappingFor(join.OuterType), OuterAlias);
        var innerMembers = new MemberFactory(_session.Options, innerMapping, InnerAlias);

        var clause = new JoinClause
        {
            Table = innerClause.FromObject,
            Alias = InnerAlias,
            OuterKeyLocator = KeyLocatorFor(join.OuterKeySelector, outerMembers, "outer"),
            InnerKeyLocator = KeyLocatorFor(join.InnerKeySelector, innerMembers, "inner"),
            IsLeftJoin = join.IsLeftJoin
        };

        clause.On.AddRange(InnerConditions(join, innerMapping, innerMembers));
        statement.Joins.Add(clause);

        var outerFields = Qualified(outerClause.SelectFields(), OuterAlias);
        var innerFields = Qualified(innerClause.SelectFields(), InnerAlias);

        statement.SelectColumns = string.Join(", ", outerFields.Concat(innerFields));

        // A plain Join that spelled its result out is already a lambda over the two documents;
        // everything else names members of the shape the join operator built, and is rewritten.
        var projection = join.FinalSelector is null
            ? join.IntermediateSelector
            : JoinResultSelectorRewriter.Rewrite(join.IntermediateSelector, join.FinalSelector,
                join.OuterType, join.InnerType, !join.IsGrouped);

        ApplyJoinWheres(statement, join, projection, outerMembers, innerMembers);
        ApplyJoinOrdering(statement, join, projection, outerMembers, innerMembers);

        return new JoinPlan(
            outerClause, join.OuterType,
            innerClause, join.InnerType,
            outerFields.Length,
            Array.IndexOf(innerClause.SelectFields(), "data"),
            Compile(projection, join.OuterType, join.InnerType));
    }

    /// <summary>
    ///     Everything the inner side must satisfy — its own <c>Where</c> clauses and its half of the
    ///     three implicit filters.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         All of it goes into the <c>ON</c> clause; see <see cref="JoinClause" /> for why that is a
    ///         correctness matter for a left join rather than a preference.
    ///     </para>
    ///     <para>
    ///         The inner query is parsed by the same parser the outer one is, with the inner alias, so
    ///         <c>Query&lt;Order&gt;().Where(x =&gt; x.Total &gt; 100)</c> as the joined query means what
    ///         it says. <b>Polecat drops those predicates silently</b> — it collects only the tenant and
    ///         soft-delete filters for the inner table — which is the failure mode this codebase refuses
    ///         everywhere else: an answer that looks right and quietly includes rows the caller excluded.
    ///         Anything beyond filtering is refused, because ordering or paging <em>within</em> the
    ///         inner set is a question about one outer row's group, which the join has flattened away.
    ///     </para>
    /// </remarks>
    private List<ISqlFragment> InnerConditions(GroupJoinData join, DocumentMapping innerMapping,
        MemberFactory innerMembers)
    {
        var parser = new LinqQueryParser(innerMembers);
        parser.Parse(join.InnerSource);

        if (parser.OrderBys.Count > 0 || parser.Limit.HasValue || parser.Offset.HasValue
            || parser.Projection is not null || parser.GroupByLocator is not null
            || parser.DistinctByLocator is not null || parser.IsDistinct || parser.GroupJoin is not null)
        {
            throw new BadLinqExpressionException(
                "A join's inner query may only filter — Where, and the tenancy and soft-delete "
                + "operators. Ordering, paging, projection and grouping there would apply within one "
                + "outer row's matches, which the join flattens away; put them on the joined query "
                + "instead.");
        }

        var conditions = new List<ISqlFragment>(parser.Wheres);
        var qualifier = InnerAlias + ".";

        ApplyTenantFilter(conditions, parser, innerMapping, qualifier);
        ApplyMetadataFilters(conditions, parser, qualifier);
        ApplyHierarchyFilter(conditions, innerMapping, join.InnerType, qualifier);
        ApplySoftDeleteFilters(conditions, parser, innerMapping, qualifier);

        return conditions;
    }

    /// <summary>
    ///     A join key, which must be one document member on each side.
    /// </summary>
    /// <remarks>
    ///     Both sides resolve through the ordinary member factories, so a key means the same thing in a
    ///     join as it does in a <c>Where</c> — a Guid compares as the lowercase canonical text both
    ///     sides store, an enum compares in whatever <c>EnumStorage</c> says, and a timestamp key is
    ///     normalised through <c>strftime</c> on both sides rather than on one.
    /// </remarks>
    private static string KeyLocatorFor(LambdaExpression selector, IMemberResolver members, string side)
    {
        var body = selector.Body;

        while (body is UnaryExpression { NodeType: ExpressionType.Convert } unary)
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression member)
        {
            throw new BadLinqExpressionException(
                $"A join's {side} key must be a single document member; '{selector.Body}' is not. A "
                + "composite key would be an equality per member, which Fisher does not translate.");
        }

        return members.ResolveMember(member).TypedLocator;
    }

    /// <summary>
    ///     Ordering named after the join, mapped back to the document member behind it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two shapes, because the two LINQ spellings put the ordering in different places. Method
    ///         syntax orders the <em>projected</em> result — <c>OrderBy(x =&gt; x.Weight)</c> over what
    ///         the result selector produced — while query syntax's <c>orderby</c> clause comes before
    ///         the <c>select</c> and so names the <em>intermediate</em> shape the join built. The first
    ///         is a lookup in the projection's member map; the second is the same rewrite the result
    ///         selector goes through, over the same two parameters.
    ///     </para>
    ///     <para>
    ///         A key that reaches neither is refused by name. Ordering by a computed member — a
    ///         concatenation, an arithmetic expression — would mean sorting on a value SQLite never
    ///         sees, and answering it would require selecting and sorting in memory.
    ///     </para>
    /// </remarks>
    private static void ApplyJoinOrdering(Statement statement, GroupJoinData join,
        LambdaExpression projection, MemberFactory outerMembers, MemberFactory innerMembers)
    {
        if (join.OrderBys.Count == 0)
        {
            return;
        }

        foreach (var (key, descending) in join.OrderBys)
        {
            var body = key.Body;

            while (body is UnaryExpression { NodeType: ExpressionType.Convert } unary)
            {
                body = unary.Operand;
            }

            if (OntoDocuments(join, key, projection)
                is not MemberExpression { Expression: ParameterExpression parameter } document)
            {
                throw new BadLinqExpressionException(
                    $"Fisher cannot order a join by '{key.Body}'. An ordering key has to be a member of "
                    + "one of the joined documents, reached either directly or through a member of the "
                    + "result that came straight from one.");
            }

            // By parameter identity, not by type: a self-join has the same document type on both
            // sides, and comparing types there would resolve every key against the outer table.
            var member = (parameter == projection.Parameters[0] ? outerMembers : innerMembers)
                .ResolveMember(document);

            if (!member.AllowsRangeComparison)
            {
                throw new BadLinqExpressionException(
                    $"Cannot order by the {member.MemberType.Name} member in SQLite: its stored form is "
                    + "not order-preserving, so the rows would come back in a plausible but wrong order.");
            }

            statement.OrderBys.Add((member.TypedLocator, descending));
        }
    }

    /// <summary>
    ///     Predicates written after the join, applied to the statement's <c>WHERE</c>.
    /// </summary>
    /// <remarks>
    ///     The inner query's own predicates go in the <c>ON</c> clause and these do not, and the
    ///     difference is not an inconsistency: an inner-side filter describes which rows the join may
    ///     match, while this one describes which joined rows survive. On a left join that shows as a
    ///     real difference in the answer — the first keeps an unmatched outer row and the second may
    ///     remove it — and in both cases it is what the caller wrote.
    /// </remarks>
    private static void ApplyJoinWheres(Statement statement, GroupJoinData join,
        LambdaExpression projection, MemberFactory outerMembers, MemberFactory innerMembers)
    {
        if (join.Wheres.Count == 0)
        {
            return;
        }

        var parser = new WhereClauseParser(
            new JoinMemberResolver(projection.Parameters[0], outerMembers, innerMembers));

        foreach (var predicate in join.Wheres)
        {
            var body = OntoDocuments(join, predicate, projection)
                       ?? throw new BadLinqExpressionException(
                           $"Fisher cannot translate '{predicate.Body}' as a filter over a join. Every "
                           + "member of it has to belong to one of the joined documents, reached either "
                           + "directly or through a member of the result that came straight from one.");

            statement.Wheres.Add(parser.Parse(body));
        }
    }

    /// <summary>
    ///     A lambda written after the join, re-expressed over the two documents.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Which of the two shapes it names is decided by its parameter's type, not by trying one
    ///         and falling back. Method syntax names the projected result; query syntax's <c>where</c>
    ///         and <c>orderby</c> clauses come before its <c>select</c> and so name the intermediate
    ///         shape the join operator built. Guessing would be ambiguous whenever the two happen to
    ///         share a member name.
    ///     </para>
    ///     <para>
    ///         Both paths land in the same place: an expression over <c>projection</c>'s own two
    ///         parameters, which is what lets a resolved member be attributed to a side by reference.
    ///     </para>
    /// </remarks>
    private static Expression? OntoDocuments(GroupJoinData join, LambdaExpression lambda,
        LambdaExpression projection)
    {
        var body = lambda.Body;

        while (body is UnaryExpression { NodeType: ExpressionType.Convert } unary)
        {
            body = unary.Operand;
        }

        if (lambda.Parameters[0].Type == projection.Body.Type)
        {
            return ProjectedMemberSubstitution.Apply(body, lambda.Parameters[0],
                JoinResultSelectorRewriter.MembersOf(projection.Body));
        }

        return JoinResultSelectorRewriter.TryRewrite(join.IntermediateSelector, lambda,
            projection.Parameters[0], projection.Parameters[1], !join.IsGrouped, out var rewritten)
            ? rewritten!.Body
            : null;
    }

    private static string[] Qualified(string[] fields, string alias)
        => fields.Select(field => $"{alias}.{field}").ToArray();

    /// <summary>
    ///     The rewritten result selector, as something callable with two boxed documents.
    /// </summary>
    /// <remarks>
    ///     Compiled once per query rather than invoked reflectively per row, and the inner parameter is
    ///     nullable because a left join's non-matching row has no inner document —
    ///     <c>DefaultIfEmpty()</c> is exactly the caller saying they expect that.
    /// </remarks>
    private static Func<object, object?, object?> Compile(LambdaExpression projection, Type outerType,
        Type innerType)
    {
        var outer = Expression.Parameter(typeof(object), "outer");
        var inner = Expression.Parameter(typeof(object), "inner");

        var invoked = Expression.Invoke(projection,
            Expression.Convert(outer, outerType), Expression.Convert(inner, innerType));

        return Expression.Lambda<Func<object, object?, object?>>(
            Expression.Convert(invoked, typeof(object)), outer, inner).Compile();
    }

    /// <summary>
    ///     Read the joined rows: the outer document, the inner one where there is one, and the result
    ///     the caller's selector makes of the pair.
    /// </summary>
    private async Task<IReadOnlyList<T>> JoinListAsync<T>(Statement statement, JoinPlan plan,
        CancellationToken token) where T : notnull
    {
        var readOuter = ResolverFor(plan.Outer, plan.OuterType);
        var readInner = ResolverFor(plan.Inner, plan.InnerType);

        var results = new List<T>();

        await using var reader = await ExecuteReaderAsync(statement, token).ConfigureAwait(false);

        // The inner side reads through the same selector a LoadAsync would use, shifted rather than
        // reimplemented — which is what keeps a joined sub-class coming back as its sub-class and a
        // mapped metadata member populated. See OffsetDataReader.
        var innerReader = new OffsetDataReader(reader, plan.InnerOffset, plan.Inner.SelectFields().Length);
        var innerData = plan.InnerOffset + plan.InnerDataOrdinal;

        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var outer = await readOuter(reader, token).ConfigureAwait(false);

            // A left join's non-matching row is all NULLs on the inner side, so nothing may be read
            // from it — the identity column alone would throw before the document ever mattered.
            var inner = reader.IsDBNull(innerData)
                ? DefaultFor(plan.InnerType)
                : await readInner(innerReader, token).ConfigureAwait(false);

            results.Add((T)plan.Project(outer!, inner)!);
        }

        return results;
    }

    private Func<DbDataReader, CancellationToken, Task<object?>> ResolverFor(ISelectClause clause,
        Type documentType)
    {
        var selector = clause.BuildSelector(_session);

        var resolve = (Func<ISelector, DbDataReader, CancellationToken, Task<object?>>)
            typeof(FisherQueryProvider)
                .GetMethod(nameof(ResolveDocumentAsync), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(documentType)
                .CreateDelegate(typeof(Func<ISelector, DbDataReader, CancellationToken, Task<object?>>));

        return (reader, token) => resolve(selector, reader, token);
    }

    private static async Task<object?> ResolveDocumentAsync<T>(ISelector selector, DbDataReader reader,
        CancellationToken token) where T : notnull
        => await ((ISelector<T>)selector).ResolveAsync(reader, token).ConfigureAwait(false);
}
