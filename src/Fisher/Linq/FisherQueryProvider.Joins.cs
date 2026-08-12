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
///     <c>GroupJoin(...).SelectMany(...)</c> and <c>Join(...)</c> over document tables — fisher#25 for
///     one join, fisher#55 for a chain of them.
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
///         set — against one statement the planner can index. A three-table query makes that two extra
///         statements and two stitches, which is why fisher#55 was worth doing rather than leaving as a
///         documented limit.
///     </para>
///     <para>
///         The whole join lives on the ordinary <see cref="Statement" />, so <c>Count</c>, <c>Any</c>,
///         paging and <c>ToSql</c> serve it without knowing it is one. What is here is the translation:
///         each pair of key locators, each inner side's filters, the predicates and ordering keys
///         written after any of the joins, and the reading of N documents out of one row.
///     </para>
///     <para>
///         <b>What a chain needed beyond the first join was one idea, not a rewrite</b> — see
///         <see cref="JoinShape" />. One join can be translated from two lambdas because both of their
///         parameters are documents; a second is written against the shape the first produced, so its
///         outer key names no document until that shape is resolved back to one. Everything else that
///         looked like it would need generalising — the offsets, the left-join null check, the result
///         selector's arity — was the same code already written for a list that happened to have two
///         entries.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Class-level: compiles the rewritten join result selector via Expression.Compile and closes the document-resolving helper over each side's document type via MakeGenericMethod. Both element types flow in from the caller's own GroupJoin call and are preserved per the AOT publishing guide.")]
[UnconditionalSuppressMessage("Trimming", "IL2060:DynamicallyAccessedMembers",
    Justification = "Class-level: MakeGenericMethod over a document type the caller named in its query.")]
public partial class FisherQueryProvider
{
    private const string OuterAlias = "outer_t";
    private const string InnerAlias = "inner_t";

    /// <summary>
    ///     The alias for the n-th joined table, counting the outer side as zero.
    /// </summary>
    /// <remarks>
    ///     <b>The first two names are preserved rather than renumbered to <c>t0</c>/<c>t1</c>.</b>
    ///     <c>ToSql</c> exists to be read, one join is overwhelmingly the common case, and
    ///     <c>outer_t</c>/<c>inner_t</c> say which side is which where a number does not — so a chain
    ///     numbers only from the second joined table on. The cost of renumbering would have been every
    ///     rendered-SQL assertion and every worked example in the documentation changing to describe a
    ///     case almost nobody writes.
    /// </remarks>
    private static string AliasFor(int side)
        => side switch { 0 => OuterAlias, 1 => InnerAlias, _ => InnerAlias + side };

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

    private (Statement Statement, JoinPlan Plan)? JoinFor(Expression expression)
    {
        var (statement, _, _, join) = BuildStatement(SourceTypeFor(expression), expression);

        return join is null ? null : (statement, join);
    }

    /// <summary>
    ///     Turn the parsed joins — either operator, however many — into <see cref="JoinClause" />s on
    ///     the statement, and return what reading the result needs.
    /// </summary>
    /// <remarks>
    ///     Walked in the order they were written, carrying two things forward: the sides accumulated so
    ///     far, and the <see cref="JoinShape" /> the last join produced. The shape is what the next
    ///     join's outer key, and any clause written between the two, are phrased against.
    /// </remarks>
    private JoinPlan ApplyJoin(Statement statement, List<GroupJoinData> joins, ISelectClause outerClause)
    {
        var outerType = joins[0].OuterType;

        var sides = new List<JoinSide>
        {
            new(outerType, OuterAlias, outerClause,
                new MemberFactory(_session.Options, _session.Options.Schema.MappingFor(outerType),
                    OuterAlias),
                Expression.Parameter(outerType, "outer"),
                Offset: 0,
                DataOrdinal: Array.IndexOf(outerClause.SelectFields(), "data"))
        };

        var columns = new List<string>(Qualified(outerClause.SelectFields(), OuterAlias));

        // Before the first join the "shape" is the outer document itself, so its own parameter stands
        // for it and there is nothing to resolve through.
        Expression result = sides[0].Parameter;
        JoinShape? intermediate = null;
        JoinShape? shape = null;

        foreach (var join in joins)
        {
            AssertComplete(join);

            var side = BuildSide(join, sides.Count, columns.Count);

            statement.Joins.Add(new JoinClause
            {
                Table = side.Clause.FromObject,
                Alias = side.Alias,
                OuterKeyLocator = OuterKeyLocatorFor(join, sides, shape),
                InnerKeyLocator = KeyLocatorFor(join.InnerKeySelector, side.Members, "inner"),
                IsLeftJoin = join.IsLeftJoin
            });

            statement.Joins[^1].On.AddRange(InnerConditions(join, side));

            sides.Add(side);
            columns.AddRange(Qualified(side.Clause.SelectFields(), side.Alias));

            (result, intermediate, shape) = Collapse(join, sides, shape);

            ApplyJoinWheres(statement, join, sides, shape, intermediate);
            ApplyJoinOrdering(statement, join, sides, shape, intermediate);
        }

        statement.SelectColumns = string.Join(", ", columns);

        var projection = Expression.Lambda(result, sides.Select(x => x.Parameter));

        return new JoinPlan(
            sides,
            Compile(projection, sides),
            lambda => JoinedMember(lambda, sides, shape!, intermediate));
    }

    private static void AssertComplete(GroupJoinData join)
    {
        if (join.IsComplete)
        {
            return;
        }

        throw new BadLinqExpressionException(
            "A GroupJoin must be followed by SelectMany. On its own it yields one grouping per "
            + "outer row, which means reading every inner row of every group; the SelectMany is "
            + "what flattens it into the one-row-per-match shape a SQL join produces. Write "
            + "GroupJoin(...).SelectMany(x => x.Group, (x, inner) => ...), or add "
            + "DefaultIfEmpty() to the group for a left join.");
    }

    /// <summary>
    ///     The joined table this <c>GroupJoin</c> names, as a side of the row.
    /// </summary>
    private JoinSide BuildSide(GroupJoinData join, int index, int offset)
    {
        // Validated rather than trusted: the inner side has to be a Fisher queryable for there to be a
        // table to join to, and this is the message that says so.
        SourceTypeFor(join.InnerSource);

        var mapping = _session.Options.Schema.MappingFor(join.InnerType);
        var storage = ((IStorageSession)_session).StorageFor(join.InnerType);

        if (storage is not ISelectClause clause)
        {
            throw new BadLinqExpressionException(
                $"The storage for '{join.InnerType.Name}' cannot produce a select clause.");
        }

        var alias = AliasFor(index);

        return new JoinSide(
            join.InnerType, alias, clause,
            new MemberFactory(_session.Options, mapping, alias),
            Expression.Parameter(join.InnerType, "inner" + index),
            offset,
            Array.IndexOf(clause.SelectFields(), "data"));
    }

    /// <summary>
    ///     Collapse one more join into the running result, and return the two shapes it produced.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The intermediate is what the join operator's own selector built; the result is that after
    ///         the <c>SelectMany</c>'s or the trailing <c>Select</c>'s selector has been applied to it.
    ///         They are the same thing when the join operator's selector is already the answer, which is
    ///         a plain <c>Join</c> in method syntax with the shape spelled out.
    ///     </para>
    ///     <para>
    ///         <b>A <c>GroupJoin</c>'s second parameter is the group, not a row, and is deliberately
    ///         left unmapped.</b> An expression still naming it is asking about rows the join has
    ///         flattened, and is refused rather than silently answered about the one matched row.
    ///     </para>
    /// </remarks>
    private static (Expression Result, JoinShape Intermediate, JoinShape Shape) Collapse(
        GroupJoinData join, List<JoinSide> sides, JoinShape? previous)
    {
        var inner = sides[^1].Parameter;

        // The join operator's own selector. Its first parameter is the outer document for the first
        // join and the previous shape thereafter; its second is the matched row, or the group.
        //
        // Mapped directly as well as through the member map, because a selector may name the whole
        // shape rather than a member of it — a GroupJoin's own selector writes
        // (y, waters) => new { y, waters }, where y is the entire previous rung. Member accesses still
        // go through the map, which is what folds y.a back to a plain side parameter.
        var direct = new Dictionary<ParameterExpression, Expression>
        {
            [join.IntermediateSelector.Parameters[0]] = previous?.Body ?? sides[0].Parameter
        };

        if (!join.IsGrouped)
        {
            direct[join.IntermediateSelector.Parameters[1]] = inner;
        }

        var over = previous ?? JoinShape.For(join.IntermediateSelector.Parameters[0]);

        var intermediateBody =
            over.Rewrite(join.IntermediateSelector.Body, join.IntermediateSelector.Parameters[0], direct)
            ?? throw Untranslatable(join.IntermediateSelector.Body);

        var intermediate = JoinShape.For(intermediateBody,
            join.IsGrouped ? join.IntermediateSelector.Parameters[1] : null);

        if (join.FinalSelector is null)
        {
            return (intermediateBody, intermediate, intermediate);
        }

        var final = join.FinalSelector;

        var resultBody = intermediate.Rewrite(final.Body, final.Parameters[0],
                             final.Parameters.Count > 1
                                 ? new Dictionary<ParameterExpression, Expression>
                                     { [final.Parameters[1]] = inner }
                                 : null)
                         ?? throw Untranslatable(final.Body);

        return (resultBody, intermediate, JoinShape.For(resultBody));
    }

    private static BadLinqExpressionException Untranslatable(Expression body)
        => new($"Fisher cannot translate '{body}' as a join's result. Each part of it has to come from "
               + "one of the joined documents; the group itself is not available, because a join returns "
               + "one row per match rather than a group per outer row.");

    /// <summary>
    ///     The document member a lambda written after a join names, or null when it names none.
    /// </summary>
    /// <remarks>
    ///     The one place a post-join expression is attributed to a side, shared by the ordering, the
    ///     predicates and <see cref="JoinPlan.Member" />. Attribution is <b>by parameter reference, not
    ///     by type</b>: a self-join has the same document type on more than one side, so comparing types
    ///     would resolve every member against the first.
    /// </remarks>
    private static IQueryableMember? JoinedMember(LambdaExpression lambda, List<JoinSide> sides,
        JoinShape shape, JoinShape? intermediate)
        => OntoDocuments(lambda, shape, intermediate)
            is MemberExpression { Expression: ParameterExpression parameter } document
            ? sides.FirstOrDefault(side => side.Parameter == parameter)?.Members.ResolveMember(document)
            : null;

    /// <summary>
    ///     Everything an inner side must satisfy — its own <c>Where</c> clauses and its half of the
    ///     three implicit filters.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         All of it goes into the <c>ON</c> clause; see <see cref="JoinClause" /> for why that is a
    ///         correctness matter for a left join rather than a preference.
    ///     </para>
    ///     <para>
    ///         The inner query is parsed by the same parser the outer one is, with that side's alias, so
    ///         <c>Query&lt;Order&gt;().Where(x =&gt; x.Total &gt; 100)</c> as the joined query means what
    ///         it says. <b>Polecat drops those predicates silently</b> — it collects only the tenant and
    ///         soft-delete filters for its inner table — which is the failure mode this codebase refuses
    ///         everywhere else: an answer that looks right and quietly includes rows the caller excluded.
    ///         Anything beyond filtering is refused, because ordering or paging <em>within</em> an inner
    ///         set is a question about one outer row's group, which the join has flattened away.
    ///     </para>
    /// </remarks>
    private List<ISqlFragment> InnerConditions(GroupJoinData join, JoinSide side)
    {
        var mapping = _session.Options.Schema.MappingFor(join.InnerType);

        var parser = new LinqQueryParser(side.Members);
        parser.Parse(join.InnerSource);

        if (parser.OrderBys.Count > 0 || parser.Limit.HasValue || parser.Offset.HasValue
            || parser.Projection is not null || parser.GroupByLocator is not null
            || parser.DistinctByLocator is not null || parser.IsDistinct || parser.Joins.Count > 0)
        {
            throw new BadLinqExpressionException(
                "A join's inner query may only filter — Where, and the tenancy and soft-delete "
                + "operators. Ordering, paging, projection and grouping there would apply within one "
                + "outer row's matches, which the join flattens away; put them on the joined query "
                + "instead.");
        }

        var conditions = new List<ISqlFragment>(parser.Wheres);
        var qualifier = side.Alias + ".";

        ApplyTenantFilter(conditions, parser, mapping, qualifier);
        ApplyMetadataFilters(conditions, parser, qualifier);
        ApplyHierarchyFilter(conditions, mapping, join.InnerType, qualifier);
        ApplySoftDeleteFilters(conditions, parser, mapping, qualifier);

        return conditions;
    }

    /// <summary>
    ///     A join's outer key, which for every join after the first is a member reached through the
    ///     shape the previous one produced.
    /// </summary>
    /// <remarks>
    ///     This is the member of fisher#55 that the single-join code could not have: <c>x =&gt;
    ///     x.catch.WaterId</c> names <c>x.catch</c>, which is not a document until the shape says which
    ///     side it came from. Once resolved it is an ordinary member of an ordinary side, and the same
    ///     <see cref="KeyLocatorFor" /> serves it.
    /// </remarks>
    private static string OuterKeyLocatorFor(GroupJoinData join, List<JoinSide> sides, JoinShape? shape)
    {
        var selector = join.OuterKeySelector;

        if (shape is null)
        {
            return KeyLocatorFor(selector, sides[0].Members, "outer");
        }

        var body = shape.Rewrite(selector.Body, selector.Parameters[0])
                   ?? throw new BadLinqExpressionException(
                       $"Fisher cannot translate '{selector.Body}' as a join key. A join after the first "
                       + "keys off the shape the one before it produced, so its outer key has to be a "
                       + "member of one of the documents already joined — reached through a member of "
                       + "that shape which came straight from one.");

        return KeyLocatorFor(Expression.Lambda(body), new JoinMemberResolver(sides), "outer");
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
    ///     Ordering named after a join, mapped back to the document member behind it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two shapes, because the two LINQ spellings put the ordering in different places. Method
    ///         syntax orders the <em>projected</em> result — <c>OrderBy(x =&gt; x.Weight)</c> over what
    ///         the result selector produced — while query syntax's <c>orderby</c> clause comes before
    ///         the <c>select</c> and so names the <em>intermediate</em> shape the join built.
    ///     </para>
    ///     <para>
    ///         A key that reaches neither is refused by name. Ordering by a computed member — a
    ///         concatenation, an arithmetic expression — would mean sorting on a value SQLite never
    ///         sees, and answering it would require selecting and sorting in memory.
    ///     </para>
    /// </remarks>
    private static void ApplyJoinOrdering(Statement statement, GroupJoinData join, List<JoinSide> sides,
        JoinShape shape, JoinShape? intermediate)
    {
        foreach (var (key, descending) in join.OrderBys)
        {
            var member = JoinedMember(key, sides, shape, intermediate);

            if (member is null)
            {
                throw new BadLinqExpressionException(
                    $"Fisher cannot order a join by '{key.Body}'. An ordering key has to be a member of "
                    + "one of the joined documents, reached either directly or through a member of the "
                    + "result that came straight from one.");
            }

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
    ///     Predicates written after a join, applied to the statement's <c>WHERE</c>.
    /// </summary>
    /// <remarks>
    ///     An inner query's own predicates go in the <c>ON</c> clause and these do not, and the
    ///     difference is not an inconsistency: an inner-side filter describes which rows the join may
    ///     match, while this one describes which joined rows survive. On a left join that shows as a
    ///     real difference in the answer — the first keeps an unmatched outer row and the second may
    ///     remove it — and in both cases it is what the caller wrote.
    /// </remarks>
    private static void ApplyJoinWheres(Statement statement, GroupJoinData join, List<JoinSide> sides,
        JoinShape shape, JoinShape? intermediate)
    {
        if (join.Wheres.Count == 0)
        {
            return;
        }

        var parser = new WhereClauseParser(new JoinMemberResolver(sides));

        foreach (var predicate in join.Wheres)
        {
            var body = OntoDocuments(predicate, shape, intermediate)
                       ?? throw new BadLinqExpressionException(
                           $"Fisher cannot translate '{predicate.Body}' as a filter over a join. Every "
                           + "member of it has to belong to one of the joined documents, reached either "
                           + "directly or through a member of the result that came straight from one.");

            statement.Wheres.Add(parser.Parse(body));
        }
    }

    /// <summary>
    ///     A lambda written after a join, re-expressed over the joined documents.
    /// </summary>
    /// <remarks>
    ///     Which of the two shapes it names is decided by its parameter's type, not by trying one and
    ///     falling back. Method syntax names the projected result; query syntax's <c>where</c> and
    ///     <c>orderby</c> clauses come before its <c>select</c> and so name the intermediate shape the
    ///     join operator built. Guessing would be ambiguous whenever the two happen to share a member
    ///     name.
    /// </remarks>
    private static Expression? OntoDocuments(LambdaExpression lambda, JoinShape shape,
        JoinShape? intermediate)
    {
        var body = lambda.Body;

        while (body is UnaryExpression { NodeType: ExpressionType.Convert } unary)
        {
            body = unary.Operand;
        }

        var parameter = lambda.Parameters[0];

        if (parameter.Type == shape.Type)
        {
            return shape.Rewrite(body, parameter);
        }

        return intermediate is not null && parameter.Type == intermediate.Type
            ? intermediate.Rewrite(body, parameter)
            : null;
    }

    private static string[] Qualified(string[] fields, string alias)
        => fields.Select(field => $"{alias}.{field}").ToArray();

    /// <summary>
    ///     The rewritten result selector, as something callable with one boxed document per side.
    /// </summary>
    /// <remarks>
    ///     Compiled once per query rather than invoked reflectively per row. Every side but the outer is
    ///     nullable, because a left join's non-matching row has no document there —
    ///     <c>DefaultIfEmpty()</c> is exactly the caller saying they expect that.
    /// </remarks>
    private static Func<object?[], object?> Compile(LambdaExpression projection, List<JoinSide> sides)
    {
        var documents = Expression.Parameter(typeof(object?[]), "documents");

        var arguments = sides.Select((side, index) => (Expression)Expression.Convert(
            Expression.ArrayIndex(documents, Expression.Constant(index)), side.DocumentType));

        return Expression.Lambda<Func<object?[], object?>>(
            Expression.Convert(Expression.Invoke(projection, arguments), typeof(object)),
            documents).Compile();
    }

    /// <summary>
    ///     Read the joined rows: one document per side, where there is one, and the result the caller's
    ///     selector makes of them.
    /// </summary>
    private async Task<IReadOnlyList<T>> JoinListAsync<T>(Statement statement, JoinPlan plan,
        CancellationToken token)
    {
        var readers = plan.Sides.Select(ResolverFor).ToArray();

        var results = new List<T>();

        await using var reader = await ExecuteReaderAsync(statement, token).ConfigureAwait(false);

        // Every side but the outer reads through the same selector a LoadAsync would use, shifted
        // rather than reimplemented — which is what keeps a joined sub-class coming back as its
        // sub-class and a mapped metadata member populated. See OffsetDataReader.
        var views = plan.Sides
            .Select(side => side.Offset == 0
                ? (DbDataReader)reader
                : new OffsetDataReader(reader, side.Offset, side.Clause.SelectFields().Length))
            .ToArray();

        var documents = new object?[plan.Sides.Count];

        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            for (var i = 0; i < plan.Sides.Count; i++)
            {
                var side = plan.Sides[i];

                // A left join's non-matching row is all NULLs on that side, so nothing may be read from
                // it — the identity column alone would throw before the document ever mattered.
                documents[i] = reader.IsDBNull(side.Offset + side.DataOrdinal)
                    ? DefaultFor(side.DocumentType)
                    : await readers[i](views[i], token).ConfigureAwait(false);
            }

            results.Add((T)plan.Project(documents)!);
        }

        return results;
    }

    private Func<DbDataReader, CancellationToken, Task<object?>> ResolverFor(JoinSide side)
    {
        var selector = side.Clause.BuildSelector(_session);

        var resolve = (Func<ISelector, DbDataReader, CancellationToken, Task<object?>>)
            typeof(FisherQueryProvider)
                .GetMethod(nameof(ResolveDocumentAsync), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(side.DocumentType)
                .CreateDelegate(typeof(Func<ISelector, DbDataReader, CancellationToken, Task<object?>>));

        return (reader, token) => resolve(selector, reader, token);
    }

    private static async Task<object?> ResolveDocumentAsync<T>(ISelector selector, DbDataReader reader,
        CancellationToken token) where T : notnull
        => await ((ISelector<T>)selector).ResolveAsync(reader, token).ConfigureAwait(false);
}
