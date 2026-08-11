using System.Linq.Expressions;
using Fisher.Internal;
using Fisher.Linq.Joins;
using Fisher.Linq.Members;
using Fisher.Linq.CursorPaging;
using Fisher.Linq.Parsing;
using Fisher.Linq.SqlGeneration;
using Fisher.Storage;
using Fisher.Storage.ClosedShape;
using JasperFx;
using Weasel.Core.SqlGeneration;
using Weasel.Storage;

namespace Fisher.Linq;

/// <summary>
///     Translates a <see cref="FisherQueryable{T}" />'s expression tree into SQL and runs it.
/// </summary>
/// <remarks>
///     <para>
///         Smaller than Polecat's provider of the same job, and the reason is that everything it
///         answers goes through one <see cref="Statement" /> — grouping, projections, paging, soft
///         deletes and joins alike. Anything it cannot translate is refused by name rather than
///         falling back to evaluating in memory.
///     </para>
///     <para>
///         The SELECT and the materialization both come from the existing query-only closed-shape
///         storage rather than being hand-written here: <see cref="ISelectClause.SelectFields" /> gives
///         the column list and <see cref="ISelectClause.BuildSelector" /> the matching
///         <see cref="ISelector{T}" />. That is the seam CLAUDE.md notes was left in place for LINQ,
///         and using it is what keeps the query path's read layout from drifting away from
///         <c>LoadAsync</c>'s.
///     </para>
/// </remarks>
public partial class FisherQueryProvider : IQueryProvider
{
    private readonly FisherSession _session;

    internal FisherQueryProvider(FisherSession session)
    {
        _session = session;
    }

    public IQueryable CreateQuery(Expression expression)
    {
        var elementType = expression.Type.GetGenericArguments().FirstOrDefault()
                          ?? throw new BadLinqExpressionException(
                              $"Cannot determine the element type of '{expression.Type.Name}'.");

        return (IQueryable)Activator.CreateInstance(
            typeof(FisherQueryable<>).MakeGenericType(elementType),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [this, expression],
            culture: null)!;
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        => new FisherQueryable<TElement>(this, expression);

    /// <summary>
    ///     Synchronous execution is not supported; see <see cref="FisherQueryable{T}.GetEnumerator" />.
    /// </summary>
    public object Execute(Expression expression) => throw SynchronousExecution();

    /// <inheritdoc cref="Execute(Expression)" />
    public TResult Execute<TResult>(Expression expression) => throw SynchronousExecution();

    private static NotSupportedException SynchronousExecution()
        => new("Fisher does not support synchronous LINQ execution. Use one of the async terminal "
               + "operators — ToListAsync, FirstOrDefaultAsync, CountAsync, AnyAsync.");

    // ---- async execution ----

    internal async Task<IReadOnlyList<T>> ToListAsync<T>(Expression expression, CancellationToken token)
        where T : notnull
    {
        if (JoinFor(expression) is { } joined)
        {
            return await JoinListAsync<T>(joined.Statement, joined.Plan, token).ConfigureAwait(false);
        }

        if (ProjectionFor(expression) is { } projected)
        {
            return await ProjectListAsync<T>(projected, token).ConfigureAwait(false);
        }

        var (statement, selector) = Build<T>(expression);

        var results = new List<T>();
        await using var reader = await ExecuteReaderAsync(statement, token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            results.Add(await selector.ResolveAsync(reader, token).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<IReadOnlyList<T>> ProjectListAsync<T>(
        (Statement Statement, RowProjection Projection) projected, CancellationToken token)
        where T : notnull
    {
        var results = new List<T>();

        await using var reader = await ExecuteReaderAsync(projected.Statement, token).ConfigureAwait(false);

        var values = new object?[projected.Projection.Columns.Length];

        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = reader.IsDBNull(i)
                    ? DefaultFor(projected.Projection.ColumnTypes[i])
                    : CoerceTo(reader.GetValue(i), projected.Projection.ColumnTypes[i]);
            }

            results.Add((T)projected.Projection.Build(values)!);
        }

        return results;
    }

    /// <summary>
    ///     One result, or default when there is none.
    /// </summary>
    /// <param name="expression">The query.</param>
    /// <param name="enforceSingle">
    ///     When true a second matching row is an error, which is what distinguishes <c>Single</c> from
    ///     <c>First</c>. The statement fetches one row more than it needs so the second row can be
    ///     detected without a second round trip.
    /// </param>
    /// <param name="required">When true, no result is an error rather than a default.</param>
    /// <param name="token">Cancellation.</param>
    internal async Task<T?> FirstAsync<T>(Expression expression, bool enforceSingle, bool required,
        CancellationToken token)
        where T : notnull
    {
        if (JoinFor(expression) is { } joined)
        {
            joined.Statement.Limit = enforceSingle ? 2 : 1;

            return OneOf(await JoinListAsync<T>(joined.Statement, joined.Plan, token).ConfigureAwait(false),
                enforceSingle, required);
        }

        if (ProjectionFor(expression) is { } projected)
        {
            projected.Statement.Limit = enforceSingle ? 2 : 1;

            return OneOf(await ProjectListAsync<T>(projected, token).ConfigureAwait(false),
                enforceSingle, required);
        }

        var (statement, selector) = Build<T>(expression);
        statement.Limit = enforceSingle ? 2 : 1;

        await using var reader = await ExecuteReaderAsync(statement, token).ConfigureAwait(false);

        if (!await reader.ReadAsync(token).ConfigureAwait(false))
        {
            return required
                ? throw new InvalidOperationException($"The query returned no {typeof(T).Name}.")
                : default;
        }

        var result = await selector.ResolveAsync(reader, token).ConfigureAwait(false);

        if (enforceSingle && await reader.ReadAsync(token).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"The query returned more than one {typeof(T).Name}.");
        }

        return result;
    }

    /// <summary>
    ///     What <c>First</c>, <c>FirstOrDefault</c>, <c>Single</c> and <c>SingleOrDefault</c> mean once
    ///     the rows are in hand — the shape the projected and joined reads share.
    /// </summary>
    private static T? OneOf<T>(IReadOnlyList<T> rows, bool enforceSingle, bool required)
    {
        if (rows.Count == 0)
        {
            return required
                ? throw new InvalidOperationException($"The query returned no {typeof(T).Name}.")
                : default;
        }

        return enforceSingle && rows.Count > 1
            ? throw new InvalidOperationException($"The query returned more than one {typeof(T).Name}.")
            : rows[0];
    }

    internal async Task<long> CountAsync<T>(Expression expression, CancellationToken token)
        where T : notnull
    {
        var (statement, parser, _, _) = BuildStatement(SourceTypeFor(expression), expression);

        // A projected count has to count the projected rows, and under Distinct that is the whole
        // point of the count — so the projection becomes a subquery rather than having its select
        // list replaced. `select count(*) from (select distinct …)` is the only shape that answers
        // "how many distinct values" correctly.
        if (RowProjection.For(parser) is not null)
        {
            return Convert.ToInt64(await ExecuteScalarAsync(
                new Statement { Subquery = statement, SelectColumns = "count(*)" }, token)
                .ConfigureAwait(false));
        }

        statement.SelectColumns = "count(*)";

        // A count over a paged query has to count the page, not the table, so the paging moves into a
        // subquery. Without this, Take(5).CountAsync() would report every matching row.
        if (statement.Limit.HasValue || statement.Offset.HasValue)
        {
            return Convert.ToInt64(await ExecuteScalarAsync(WrapAsSubquery(statement), token)
                .ConfigureAwait(false));
        }

        statement.OrderBys.Clear();
        return Convert.ToInt64(await ExecuteScalarAsync(statement, token).ConfigureAwait(false));
    }

    /// <summary>
    ///     <c>count(*)</c> over the query's predicates with any paging discarded — what
    ///     <c>ToPagedListAsync</c> reports as the total.
    /// </summary>
    /// <remarks>
    ///     Distinct from <see cref="CountAsync{T}" />, which counts the page when the query is paged.
    ///     Both are right for their caller; conflating them would make one of the two silently wrong.
    /// </remarks>
    internal async Task<long> CountIgnoringPagingAsync<T>(Expression expression, CancellationToken token)
        where T : notnull
    {
        var (statement, parser, _, _) = BuildStatement(SourceTypeFor(expression), expression);

        statement.Limit = null;
        statement.Offset = null;

        if (RowProjection.For(parser) is not null)
        {
            return Convert.ToInt64(await ExecuteScalarAsync(
                new Statement { Subquery = statement, SelectColumns = "count(*)" }, token)
                .ConfigureAwait(false));
        }

        statement.SelectColumns = "count(*)";
        statement.OrderBys.Clear();

        return Convert.ToInt64(await ExecuteScalarAsync(statement, token).ConfigureAwait(false));
    }

    /// <summary>
    ///     One keyset page, plus the cursor that fetches the next.
    /// </summary>
    /// <remarks>
    ///     The ordering key values for the cursor are read off the row, not off the materialized
    ///     document: a key can be any locator, including one no member of the result object exposes
    ///     directly. They are appended to the select list <em>after</em> the document's own columns,
    ///     which is safe because the storage selector resolves from fixed positions starting at 0.
    /// </remarks>
    internal async Task<Pagination.CursorPage<T>> CursorPageAsync<T>(Expression expression,
        int pageSize, string? cursor, CancellationToken token) where T : notnull
    {
        var prepared = PrepareCursorPage<T>(expression, pageSize, cursor);

        var selector = (ISelector<T>)prepared.SelectClause.BuildSelector(_session);
        var items = new List<T>();
        object?[]? lastKeys = null;

        await using (var reader = await ExecuteReaderAsync(prepared.Statement, token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                items.Add(await selector.ResolveAsync(reader, token).ConfigureAwait(false));
                lastKeys = prepared.ReadKeys(reader);
            }
        }

        return new Pagination.CursorPage<T>(items, prepared.NextCursor(items.Count, lastKeys));
    }

    /// <summary>
    ///     A keyset page whose items are the stored JSON rather than materialized documents
    ///     (fisher#28 + fisher#27, reached from fisher#49).
    /// </summary>
    /// <remarks>
    ///     <b>The same preparation as <see cref="CursorPageAsync{T}" />, deliberately shared.</b> The
    ///     cursor's validation, decode, seek predicate and "a short page is the last page" rule are
    ///     subtle enough that two copies would drift — and a drift here is a pager that silently skips
    ///     or repeats rows. Only the select list and the row read differ: <c>data</c> instead of the
    ///     storage's fields, and a string instead of a materialized document.
    /// </remarks>
    internal async Task<Pagination.CursorPage<string>> CursorPageJsonAsync<T>(Expression expression,
        int pageSize, string? cursor, CancellationToken token) where T : notnull
    {
        var prepared = PrepareCursorPage<T>(expression, pageSize, cursor, jsonOnly: true);

        var items = new List<string>();
        object?[]? lastKeys = null;

        await using (var reader = await ExecuteReaderAsync(prepared.Statement, token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                items.Add(reader.GetString(0));
                lastKeys = prepared.ReadKeys(reader);
            }
        }

        return new Pagination.CursorPage<string>(items, prepared.NextCursor(items.Count, lastKeys));
    }

    private PreparedCursorPage PrepareCursorPage<T>(Expression expression, int pageSize, string? cursor,
        bool jsonOnly = false) where T : notnull
    {
        var (statement, parser, selectClause, join) = BuildStatement(SourceTypeFor(expression), expression);

        if (RowProjection.For(parser) is not null)
        {
            throw new BadLinqExpressionException(
                "Keyset pagination returns documents, so it cannot follow a Select. Project the page's "
                + "items after it comes back.");
        }

        if (join is not null)
        {
            throw new BadLinqExpressionException(
                "Keyset pagination returns documents of one type, so it cannot follow a join. Page the "
                + "outer query and join the page's items afterwards, or use ToPagedListAsync.");
        }

        CursorPagination.ValidateOrdering(parser.OrderByMembers);

        if (cursor is not null)
        {
            statement.Wheres.Add(CursorPagination.BuildSeekPredicate(
                parser.OrderBys, CursorPagination.Decode(cursor, parser.OrderByMembers)));
        }

        var fields = jsonOnly ? ["data"] : selectClause.SelectFields();
        var keys = parser.OrderBys.Select(x => x.Locator).ToArray();

        statement.SelectColumns = string.Join(", ", fields.Concat(keys));
        statement.Limit = pageSize;
        statement.Offset = null;

        return new PreparedCursorPage(statement, selectClause, fields.Length, keys.Length, pageSize,
            reader =>
            {
                var read = new object?[keys.Length];

                for (var i = 0; i < keys.Length; i++)
                {
                    var ordinal = fields.Length + i;
                    read[i] = reader.IsDBNull(ordinal)
                        ? null
                        : CoerceTo(reader.GetValue(ordinal), parser.OrderByMembers[i]!.MemberType);
                }

                return read;
            });
    }

    /// <summary>
    ///     Everything a keyset page needs that does not depend on how its rows are materialized.
    /// </summary>
    private sealed record PreparedCursorPage(Statement Statement, ISelectClause SelectClause,
        int FieldCount, int KeyCount, int PageSize,
        Func<System.Data.Common.DbDataReader, object?[]> ReadKeys)
    {
        /// <remarks>
        ///     A short page is the last page. A full one may or may not be, and issuing a cursor for an
        ///     empty next page is cheaper than the extra row-read it would take to know.
        /// </remarks>
        internal string? NextCursor(int count, object?[]? lastKeys)
            => count == PageSize && lastKeys is not null ? CursorPagination.Encode(lastKeys) : null;
    }

    /// <summary>
    ///     The SQL this query would run, with parameter names rather than values.
    /// </summary>
    internal string ToSql<T>(Expression expression) where T : notnull
    {
        var (statement, _, _, _) = BuildStatement(SourceTypeFor(expression), expression);

        var builder = new Weasel.Sqlite.CommandBuilder();
        statement.Apply(builder);

        return builder.Compile().CommandText;
    }

    /// <summary>
    ///     The stored JSON of each matching row, untouched (fisher#28).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>data</c> is TEXT holding exactly what System.Text.Json wrote, so this is a byte-exact
    ///         read — a stronger guarantee than either sibling can make, since <c>jsonb</c> normalises
    ///         whitespace and key order and <c>nvarchar</c> needs an encoding decision.
    ///     </para>
    ///     <para>
    ///         Built through the ordinary statement path, so the tenant, soft-delete and hierarchy
    ///         filters all apply — a JSON read that composed its own <c>select data from …</c> would be
    ///         yet another caller having to remember all three.
    ///     </para>
    /// </remarks>
    internal async Task<IReadOnlyList<string>> JsonRowsAsync<T>(Expression expression, string columns,
        int? limit, CancellationToken token) where T : notnull
    {
        var (statement, parser, _, join) = BuildStatement(SourceTypeFor(expression), expression);

        if (RowProjection.For(parser) is not null || join is not null)
        {
            throw new BadLinqExpressionException(
                "A JSON read returns stored documents, so it cannot follow a Select, a GroupBy or a "
                + "join.");
        }

        statement.SelectColumns = columns;

        if (limit.HasValue)
        {
            statement.Limit = limit;
        }

        var rows = new List<string>();

        await using var reader = await ExecuteReaderAsync(statement, token).ConfigureAwait(false);

        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            rows.Add(reader.IsDBNull(0) ? "null" : reader.GetString(0));

            for (var i = 1; i < reader.FieldCount; i++)
            {
                rows.Add(reader.IsDBNull(i) ? "" : reader.GetString(i));
            }
        }

        return rows;
    }

    /// <summary>
    ///     Which concurrency column the queried type carries, if either.
    /// </summary>
    /// <remarks>
    ///     The two are alternatives rather than a pair — see
    ///     <c>DocumentMapping.AssertConcurrencyIsCoherent</c> — so this is a choice of one, and the
    ///     optimistic-concurrency arm is tested first only because it is the older default.
    /// </remarks>
    internal DocumentVersionSource VersionSourceFor<T>() where T : notnull
    {
        var mapping = _session.Options.Schema.MappingFor(typeof(T));

        if (mapping.UseOptimisticConcurrency)
        {
            return DocumentVersionSource.GuidVersion;
        }

        return mapping.UseNumericRevisions ? DocumentVersionSource.NumericRevision : DocumentVersionSource.None;
    }

    internal async Task<bool> AnyAsync<T>(Expression expression, CancellationToken token)
        where T : notnull
    {
        // Built non-generically so it answers a projected query too — whether any row exists does not
        // depend on what the rows are shaped into.
        var (statement, _, _, _) = BuildStatement(SourceTypeFor(expression), expression);
        statement.SelectColumns = "1";
        statement.OrderBys.Clear();
        statement.IsExistsWrapper = true;

        return Convert.ToInt64(await ExecuteScalarAsync(statement, token).ConfigureAwait(false)) != 0;
    }

    /// <summary>
    ///     The last row of the query — <c>First</c> against the reverse ordering.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         SQLite has no <c>LAST</c>, so "last" only means anything relative to an <c>ORDER BY</c>.
    ///         An unordered query is refused rather than answered from whatever order the table
    ///         happened to yield, which is neither stable nor the same across two runs.
    ///     </para>
    ///     <para>
    ///         <b>A paged query needs the ordering inverted <em>outside</em> the page, not inside it.</b>
    ///         <c>OrderBy(x =&gt; x.Name).Take(3).LastAsync()</c> means the last of those three, so
    ///         inverting in place would answer about the whole table instead — the paged query becomes a
    ///         subquery and the inversion sits on the outer statement. The outer locators still read
    ///         <c>json_extract(data, …)</c>, which works because the subquery selects the document's own
    ///         columns.
    ///     </para>
    /// </remarks>
    internal async Task<T?> LastAsync<T>(Expression expression, bool required, CancellationToken token)
        where T : notnull
    {
        if (JoinFor(expression) is { } joined)
        {
            return await LastJoinedAsync<T>(joined.Statement, joined.Plan, required, token)
                .ConfigureAwait(false);
        }

        if (ProjectionFor(expression) is { } projected)
        {
            return await LastProjectedAsync<T>(projected, required, token).ConfigureAwait(false);
        }

        var (statement, selector) = Build<T>(expression);

        if (statement.OrderBys.Count == 0)
        {
            throw new BadLinqExpressionException(
                "LastAsync requires an OrderBy: SQLite has no LAST, so 'last' is only meaningful "
                + "relative to an ordering. Add an OrderBy, or use FirstAsync against the reverse "
                + "ordering.");
        }

        statement = statement.Limit.HasValue || statement.Offset.HasValue
            ? ReverseOverPage(statement)
            : ReverseInPlace(statement);

        await using var reader = await ExecuteReaderAsync(statement, token).ConfigureAwait(false);

        if (!await reader.ReadAsync(token).ConfigureAwait(false))
        {
            return required
                ? throw new InvalidOperationException($"The query returned no {typeof(T).Name}.")
                : default;
        }

        return await selector.ResolveAsync(reader, token).ConfigureAwait(false);
    }

    private async Task<T?> LastProjectedAsync<T>(
        (Statement Statement, RowProjection Projection) projected, bool required, CancellationToken token)
        where T : notnull
    {
        if (projected.Statement.OrderBys.Count == 0)
        {
            throw new BadLinqExpressionException(
                "LastAsync requires an OrderBy: SQLite has no LAST, so 'last' is only meaningful "
                + "relative to an ordering.");
        }

        var statement = projected.Statement.Limit.HasValue || projected.Statement.Offset.HasValue
            ? ReverseOverPage(projected.Statement)
            : ReverseInPlace(projected.Statement);

        var rows = await ProjectListAsync<T>((statement, projected.Projection), token).ConfigureAwait(false);

        return rows.Count > 0
            ? rows[0]
            : required
                ? throw new InvalidOperationException($"The query returned no {typeof(T).Name}.")
                : default;
    }

    /// <summary>
    ///     <c>Last</c> over a join.
    /// </summary>
    /// <remarks>
    ///     The unpaged half is the ordinary reversal, which works unchanged because a join's ordering
    ///     locators are already on the statement they are being reversed on. The paged half is not —
    ///     see <see cref="ReverseJoinOverPage" />.
    /// </remarks>
    private async Task<T?> LastJoinedAsync<T>(Statement statement, JoinPlan plan, bool required,
        CancellationToken token) where T : notnull
    {
        if (statement.OrderBys.Count == 0)
        {
            throw new BadLinqExpressionException(
                "LastAsync requires an OrderBy: SQLite has no LAST, so 'last' is only meaningful "
                + "relative to an ordering. Add an OrderBy, or use FirstAsync against the reverse "
                + "ordering.");
        }

        var reversed = statement.Limit.HasValue || statement.Offset.HasValue
            ? ReverseJoinOverPage(statement)
            : ReverseInPlace(statement);

        var rows = await JoinListAsync<T>(reversed, plan, token).ConfigureAwait(false);

        return rows.Count > 0
            ? rows[0]
            : required
                ? throw new InvalidOperationException($"The query returned no {typeof(T).Name}.")
                : default;
    }

    private static Statement ReverseInPlace(Statement statement)
    {
        var reversed = statement.OrderBys.Select(x => (x.Locator, !x.Descending)).ToArray();
        statement.OrderBys.Clear();
        statement.OrderBys.AddRange(reversed);
        statement.Limit = 1;

        return statement;
    }

    private static Statement ReverseOverPage(Statement statement)
    {
        var outer = new Statement { Subquery = statement, SelectColumns = statement.SelectColumns, Limit = 1 };
        outer.OrderBys.AddRange(statement.OrderBys.Select(x => (x.Locator, !x.Descending)));

        return outer;
    }

    /// <summary>
    ///     The same reversal for a paged join, whose ordering keys have to leave the subquery as named
    ///     columns.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="ReverseOverPage" /> works for an unjoined query because its locators read
    ///         <c>json_extract(data, …)</c> and the subquery hands the document's own <c>data</c> column
    ///         straight out. A join's locators are qualified — <c>json_extract(outer_t.data, …)</c> — and
    ///         the alias does not survive into the enclosing scope, so the outer statement fails with
    ///         <c>no such column: outer_t.data</c>. Verified against SQLite 3.51 rather than assumed.
    ///     </para>
    ///     <para>
    ///         So each key is aliased into the page's select list and the outer statement orders by the
    ///         alias — the same trick keyset paging uses to sort on a locator no member of the result
    ///         exposes. The extra trailing columns are harmless: both selectors read from fixed
    ///         positions at the front of the row, and the inner one's offset is counted from the outer
    ///         side's own column count rather than from the row's width.
    ///     </para>
    /// </remarks>
    private static Statement ReverseJoinOverPage(Statement statement)
    {
        var keys = statement.OrderBys
            .Select((x, i) => (Alias: $"order_key_{i}", x.Locator, x.Descending))
            .ToArray();

        statement.SelectColumns = string.Join(", ",
            keys.Select(x => $"{x.Locator} as {x.Alias}").Prepend(statement.SelectColumns));

        var outer = new Statement { Subquery = statement, SelectColumns = "*", Limit = 1 };
        outer.OrderBys.AddRange(keys.Select(x => (x.Alias, !x.Descending)));

        return outer;
    }

    /// <summary>
    ///     A scalar aggregate — <c>sum</c>, <c>min</c>, <c>max</c> or <c>avg</c> over one member.
    /// </summary>
    /// <remarks>
    ///     The same shape as <see cref="CountAsync{T}" />: swap the select list for the aggregate and
    ///     read one scalar. Paging gets the same treatment for the same reason — <c>Take(5).SumAsync()</c>
    ///     sums the page, not the table — except that the subquery has to project the member rather than
    ///     a literal, so it is aliased and the outer aggregates the alias.
    /// </remarks>
    internal async Task<TResult?> AggregateAsync<TResult>(Expression expression,
        AggregateFunction function, LambdaExpression selector, CancellationToken token)
    {
        // Built non-generically, from the chain's own source type rather than from the terminal's
        // element type. A join's element type is the caller's result shape and a projection's is
        // whatever it produced, and neither is a document the schema has a mapping for — asking for one
        // is what used to fail with a message about identity members instead of about the operator.
        var (statement, parser, _, join) = BuildStatement(SourceTypeFor(expression), expression);

        if (RowProjection.For(parser) is not null)
        {
            throw new BadLinqExpressionException(
                $"Fisher cannot answer {function}Async after a Select, whose rows are values rather than "
                + $"documents with columns to aggregate. Aggregate before the projection — "
                + $"Query<T>().{function}Async(x => x.Member).");
        }

        var locator = AggregateLocatorFor(SourceTypeFor(expression), function, selector, join);

        if (statement.Limit.HasValue || statement.Offset.HasValue)
        {
            statement = AggregateOverPage(statement, function, locator);
        }
        else
        {
            // An aggregate's value cannot depend on ordering, and keeping the ORDER BY would make
            // SQLite sort rows it is about to collapse.
            statement.OrderBys.Clear();
            statement.SelectColumns = $"{function.Sql()}({locator})";
        }

        return Coerce<TResult>(await ExecuteScalarAsync(statement, token).ConfigureAwait(false));
    }

    private static Statement AggregateOverPage(Statement statement, AggregateFunction function,
        string locator)
    {
        var inner = new Statement
        {
            FromTable = statement.FromTable,
            // Carried for the same reason WrapAsSubquery carries them: aggregating a paged join has to
            // aggregate the joined rows, and the locator is qualified with an alias that only exists
            // while the join is in scope. Dropping them would fail with "no such column" for an
            // inner-side member and silently aggregate the outer table alone for an outer-side one.
            FromAlias = statement.FromAlias,
            SelectColumns = $"{locator} as agg_value",
            Limit = statement.Limit,
            Offset = statement.Offset
        };
        inner.Joins.AddRange(statement.Joins);
        inner.Wheres.AddRange(statement.Wheres);
        inner.OrderBys.AddRange(statement.OrderBys);

        return new Statement { Subquery = inner, SelectColumns = $"{function.Sql()}(agg_value)" };
    }

    /// <summary>
    ///     Resolve the aggregated member, refusing one whose stored form makes the aggregate
    ///     meaningless.
    /// </summary>
    /// <remarks>
    ///     Two different guards, which is why <see cref="AggregateFunction" /> is an enum rather than a
    ///     SQL string. <c>Min</c>/<c>Max</c> need only that the member orders — a string minimum is a
    ///     real answer — so they reuse the same <c>AllowsRangeComparison</c> check <c>OrderBy</c>
    ///     applies. <c>Sum</c>/<c>Average</c> need an actual number: SQLite's <c>sum</c> over text
    ///     quietly returns 0 rather than failing, so a string-stored enum would report a plausible total
    ///     for a column that has none.
    /// </remarks>
    /// <param name="join">
    ///     The join, when there is one. Over a join the selector names a member of the caller's result
    ///     shape rather than of a document, so it goes through the same mapping the post-join
    ///     <c>Where</c> and <c>OrderBy</c> use — and the two guards below then apply unchanged, because
    ///     what they ask about is the resolved member and not how it was reached.
    /// </param>
    private string AggregateLocatorFor(Type sourceType, AggregateFunction function,
        LambdaExpression selector, JoinPlan? join)
    {
        var body = selector.Body;

        while (body is UnaryExpression { NodeType: ExpressionType.Convert } unary)
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression memberExpression)
        {
            throw new BadLinqExpressionException(
                $"{function}Async is only supported over a document member.");
        }

        var member = join is null
            ? new MemberFactory(_session.Options, _session.Options.Schema.MappingFor(sourceType))
                .ResolveMember(memberExpression)
            : join.Member(selector)
              ?? throw new BadLinqExpressionException(
                  $"Fisher cannot {function.Sql()} '{selector.Body}' over a join. An aggregated value has "
                  + "to be a member of one of the joined documents, reached either directly or through a "
                  + "member of the result that came straight from one.");

        if (function.RequiresANumber() && !IsNumeric(member.MemberType))
        {
            throw new BadLinqExpressionException(
                $"Cannot {function.Sql()} the {member.MemberType.Name} member '{memberExpression.Member.Name}': "
                + "it is not a number. SQLite's sum() and avg() return 0 over non-numeric values rather "
                + "than failing, so this would report a plausible total for a column that has none.");
        }

        if (!function.RequiresANumber() && !member.AllowsRangeComparison)
        {
            throw new BadLinqExpressionException(
                $"Cannot take the {function.Sql()} of the {member.MemberType.Name} member "
                + $"'{memberExpression.Member.Name}' in SQLite: its stored form is not order-preserving, "
                + "so the answer would be plausible but wrong. For an enum, storing it as an integer "
                + "(StoreOptions.Serializer.EnumStorage) makes ordering meaningful.");
        }

        return member.TypedLocator;
    }

    private static bool IsNumeric(Type type)
    {
        var inner = Nullable.GetUnderlyingType(type) ?? type;

        // Enums are excluded on purpose even when stored as integers: their numeric value is an
        // identifier, so a total of it is arithmetic on labels.
        return !inner.IsEnum && Type.GetTypeCode(inner) is TypeCode.Byte or TypeCode.SByte
            or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32
            or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double
            or TypeCode.Decimal;
    }

    /// <summary>
    ///     Turn the scalar SQLite handed back into <typeparamref name="TResult" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Empty first.</b> <c>sum</c>, <c>min</c>, <c>max</c> and <c>avg</c> all return NULL over
    ///         no rows where <c>count</c> returns 0, so an empty result becomes <c>default</c> rather
    ///         than being cast. (<c>total()</c> would return 0.0 for an empty <c>sum</c>, but it is
    ///         always REAL and so is the wrong tool for the int and decimal overloads.)
    ///     </para>
    ///     <para>
    ///         <b>Then the three types <c>Convert.ChangeType</c> cannot do</b>, which are exactly the
    ///         ones Fisher encodes rather than storing natively — so a naive cast is broken precisely
    ///         where this store differs from its siblings, and nowhere else:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <description>
    ///                 <b>An enum comes back as INTEGER</b> and needs <see cref="Enum.ToObject(Type,long)" />;
    ///                 <c>Convert.ChangeType(long, someEnum)</c> throws <see cref="InvalidCastException" />.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <b>A timestamp comes back as TEXT</b>, in the <c>strftime</c> form
    ///                 <see cref="Members.TimestampMember" />'s locator produces — fixed width, UTC,
    ///                 and with no <c>Z</c> suffix — so it is parsed by
    ///                 <see cref="Storage.SqliteTimestamp.FromDatabaseValue" />, whose
    ///                 <c>AssumeUniversal</c> is what makes the missing suffix correct rather than
    ///                 local. <see cref="DateTimeOffset" /> is not <see cref="IConvertible" /> at all.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <b>A Guid comes back as TEXT</b>, and <see cref="Guid" /> is not
    ///                 <see cref="IConvertible" /> either.
    ///             </description>
    ///         </item>
    ///     </list>
    /// </remarks>
    private static TResult? Coerce<TResult>(object? raw)
    {
        if (raw is null or DBNull)
        {
            return default;
        }

        return (TResult)CoerceTo(raw, typeof(TResult))!;
    }

    /// <summary>
    ///     What a NULL column becomes for a projection's target type.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A projected column is NULL more often than it looks: <c>json_extract</c> yields NULL for
    ///         an absent key, and <c>sum</c>/<c>min</c>/<c>max</c>/<c>avg</c> over an empty group yield
    ///         NULL too. The projection's compiled body unboxes each value to the member's declared
    ///         type, and unboxing a null to a non-nullable value type is a
    ///         <see cref="NullReferenceException" /> from inside generated code — with nothing in the
    ///         message to say which column or why.
    ///     </para>
    ///     <para>
    ///         So a value type gets its default, which is also what deserializing the document would
    ///         have produced for an absent key, and what the aggregate terminals already return over no
    ///         rows. A reference type stays null.
    ///     </para>
    /// </remarks>
    private static object? DefaultFor(Type type)
        => type.IsValueType && Nullable.GetUnderlyingType(type) is null
            ? Activator.CreateInstance(type)
            : null;

    /// <summary>
    ///     The same conversions, for a column type only known at runtime — what a
    ///     <see cref="SelectProjection" /> reads each of its columns through.
    /// </summary>
    private static object? CoerceTo(object? raw, Type type)
    {
        if (raw is null or DBNull)
        {
            return null;
        }

        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (target.IsInstanceOfType(raw))
        {
            return raw;
        }

        if (target.IsEnum)
        {
            return Enum.ToObject(target,
                Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture));
        }

        if (target == typeof(DateTimeOffset))
        {
            return Storage.SqliteTimestamp.FromDatabaseValue((string)raw);
        }

        if (target == typeof(DateTime))
        {
            return Storage.SqliteTimestamp.FromDatabaseValue((string)raw).UtcDateTime;
        }

        if (target == typeof(Guid))
        {
            return Guid.Parse((string)raw);
        }

        return Convert.ChangeType(raw, target, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Rebuilds a paged statement as <c>select count(*) from (…) </c> so the count applies to the
    ///     page.
    /// </summary>
    private static Statement WrapAsSubquery(Statement statement)
    {
        var inner = new Statement
        {
            FromTable = statement.FromTable,
            // Carried because a count over a paged join has to count the joined rows. Dropping them
            // would count the outer table instead — the same number for a one-to-one join and a
            // silently different one for every other.
            FromAlias = statement.FromAlias,
            SelectColumns = "1",
            Limit = statement.Limit,
            Offset = statement.Offset
        };
        inner.Joins.AddRange(statement.Joins);
        inner.Wheres.AddRange(statement.Wheres);
        inner.OrderBys.AddRange(statement.OrderBys);

        return new Statement { Subquery = inner, SelectColumns = "count(*)" };
    }

    private MemberFactory MemberFactoryFor<T>() where T : notnull
        => new(_session.Options, _session.Options.Schema.For<T>().Mapping);

    /// <summary>
    ///     The document type the chain started from.
    /// </summary>
    /// <remarks>
    ///     A projection makes the queryable's element type diverge from the document's —
    ///     <c>Query&lt;Catch&gt;().Select(x =&gt; x.Species)</c> is an <c>IQueryable&lt;string&gt;</c> —
    ///     so the terminal operators can no longer take the document type from their own type
    ///     parameter. The root of every Fisher chain is a <c>ConstantExpression</c> holding the
    ///     original <see cref="FisherQueryable{T}" />, which is where the answer lives.
    /// </remarks>
    private static Type SourceTypeFor(Expression expression)
    {
        var current = expression;

        while (current is MethodCallExpression call && call.Arguments.Count > 0)
        {
            current = call.Arguments[0];
        }

        return current is ConstantExpression { Value: IQueryable queryable }
            ? queryable.ElementType
            : throw new BadLinqExpressionException(
                "This query did not start from session.Query<T>(), so Fisher cannot tell which document "
                + "type it is over.");
    }

    /// <summary>
    ///     The statement and the projection for a projected query, or null when the query returns
    ///     documents.
    /// </summary>
    private (Statement Statement, RowProjection Projection)? ProjectionFor(Expression expression)
    {
        var (statement, parser, _, _) = BuildStatement(SourceTypeFor(expression), expression);

        return RowProjection.For(parser) is { } projection ? (statement, projection) : null;
    }

    private (Statement Statement, ISelector<T> Selector) Build<T>(Expression expression) where T : notnull
    {
        var (statement, parser, selectClause, join) = BuildStatement(typeof(T), expression);

        if (RowProjection.For(parser) is not null)
        {
            throw new BadLinqExpressionException(
                $"This query projects with Select, so it does not return {typeof(T).Name} documents.");
        }

        // Everything reaching here reads whole documents of one type through one selector, which a
        // joined row is not. ToListAsync, the First/Single family and LastAsync each take the join path
        // before they get here, and the aggregates and CountAsync never build a selector at all; the
        // rest are refused rather than silently answering about the outer table alone.
        if (join is not null)
        {
            throw new BadLinqExpressionException(
                "Fisher cannot answer this operator over a join. A joined query supports ToListAsync, "
                + "the First/Single/Last families, the scalar aggregates, CountAsync, AnyAsync, "
                + "ToPagedListAsync and ToSql.");
        }

        return (statement, (ISelector<T>)selectClause.BuildSelector(_session));
    }

    /// <summary>
    ///     Everything about a query that does not depend on the <em>result</em> type — which is
    ///     everything except materialization.
    /// </summary>
    /// <remarks>
    ///     Non-generic on purpose. A projection's result type is not the document type, so the generic
    ///     <see cref="Build{T}" /> cannot serve both; splitting here is what avoids reflecting over a
    ///     runtime type to build a statement.
    /// </remarks>
    private (Statement Statement, LinqQueryParser Parser, ISelectClause SelectClause, JoinPlan? Join)
        BuildStatement(Type sourceType, Expression expression)
    {
        var mapping = _session.Options.Schema.MappingFor(sourceType);
        var storage = ((IStorageSession)_session).StorageFor(sourceType);

        if (storage is not ISelectClause selectClause)
        {
            throw new BadLinqExpressionException(
                $"The storage for '{sourceType.Name}' cannot produce a select clause.");
        }

        // A join has to be known before the chain is parsed, because it is what decides whether every
        // locator the parse produces is qualified with a table alias. Cheaper than parsing twice, and
        // the alternative — qualifying rendered SQL afterwards — is the mistake MemberFactory's own
        // doc comment records.
        var joining = ContainsJoin(expression);

        var parser = new LinqQueryParser(
            new MemberFactory(_session.Options, mapping, joining ? OuterAlias : null));
        parser.Parse(expression);

        var statement = new Statement
        {
            FromTable = selectClause.FromObject,
            FromAlias = joining ? OuterAlias : null,
            SelectColumns = RowProjection.For(parser) is { } projection
                ? string.Join(", ", projection.Columns)
                : string.Join(", ", selectClause.SelectFields()),
            GroupBy = parser.GroupByLocator,
            Limit = parser.Limit,
            Offset = parser.Offset,
            NonStaleTimeout = parser.NonStaleTimeout
        };

        statement.OrderBys.AddRange(parser.OrderBys);

        statement.Wheres.AddRange(parser.Wheres);
        statement.Havings.AddRange(parser.Havings);

        var qualifier = joining ? OuterAlias + "." : string.Empty;

        ApplyTenantFilter(statement.Wheres, parser, mapping, qualifier);
        ApplyMetadataFilters(statement.Wheres, parser, qualifier);
        ApplyHierarchyFilter(statement.Wheres, mapping, sourceType, qualifier);
        ApplySoftDeleteFilters(statement.Wheres, parser, mapping, qualifier);

        var join = parser.GroupJoin is null ? null : ApplyJoin(statement, parser.GroupJoin, selectClause);

        return (ApplyDistinct(statement, parser, selectClause), parser, selectClause, join);
    }

    /// <summary>
    ///     <c>Distinct</c> and <c>DistinctBy</c>, which are different enough to be separate operators
    ///     rather than overloads.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b><c>Distinct()</c> is refused on an unprojected query</b>, and that is not
    ///         conservatism. DISTINCT over a document's own columns compares whole serialized JSON
    ///         strings byte for byte — so two documents equal in every member but written by different
    ///         serializer settings, or with members in a different order, count as distinct. It would
    ///         look right on small test data and be wrong in production.
    ///     </para>
    ///     <para>
    ///         <b><c>DistinctBy</c> is the operator for documents</b>, and it needs a window function
    ///         rather than a DISTINCT: one row per key, keeping the whole document. SQLite has had
    ///         <c>row_number()</c> since 3.25, so Polecat's shape ports directly.
    ///     </para>
    /// </remarks>
    private static Statement ApplyDistinct(Statement statement, LinqQueryParser parser,
        ISelectClause selectClause)
    {
        if (parser.IsDistinct)
        {
            if (RowProjection.For(parser) is null)
            {
                throw new BadLinqExpressionException(
                    "Distinct() requires a Select. Over whole documents it would compare serialized JSON "
                    + "strings byte for byte rather than comparing documents, which is almost never what "
                    + "was meant — use DistinctBy(x => x.Key) to deduplicate documents by a member.");
            }

            statement.IsDistinct = true;
        }

        if (parser.DistinctByLocator is null)
        {
            return statement;
        }

        var fields = string.Join(", ", selectClause.SelectFields());

        var inner = new Statement
        {
            FromTable = statement.FromTable,
            SelectColumns =
                $"{fields}, row_number() over (partition by {parser.DistinctByLocator} "
                + $"order by {parser.DistinctByLocator}) as fi_rn"
        };
        inner.Wheres.AddRange(statement.Wheres);

        // Ordering and paging belong outside: they apply to the deduplicated rows, not to the
        // partitioned ones.
        var outer = new Statement
        {
            Subquery = inner,
            SelectColumns = fields,
            Limit = statement.Limit,
            Offset = statement.Offset
        };
        outer.Wheres.Add(new LiteralSqlFragment("fi_rn = 1"));
        outer.OrderBys.AddRange(statement.OrderBys);

        return outer;
    }

    /// <summary>
    ///     Scope the query to a tenant — once per statement, independent of everything else.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>fisher#51.</b> This used to be applied by wrapping each caller predicate through
    ///         <c>IDocumentStorage.FilterDocuments</c>, which meant a query with no <c>Where</c> got no
    ///         tenant term at all — so <c>Query&lt;T&gt;()</c> on a conjoined type returned every
    ///         tenant's rows. That is a cross-tenant read, and a silent one: the tenant owning most of
    ///         the data sees a correct-looking answer with extras, and a tenant with none sees somebody
    ///         else's.
    ///     </para>
    ///     <para>
    ///         <b>It is the same mistake <see cref="ApplyHierarchyFilter" /> already documents</b>, whose
    ///         doc comment names both halves of it — composing a filter into <c>FilterDocuments</c>
    ///         repeats it per predicate <em>and omits it from a query with none</em>. fisher#17 learned
    ///         that for the <c>doc_type</c> discriminator; the tenant filter had the same shape and was
    ///         not revisited. All three implicit filters now read the same way, one statement-level pass
    ///         each, so no query shape can drop one.
    ///     </para>
    ///     <para>
    ///         Being its own pass is also what lets <c>AnyTenant()</c> and <c>TenantIsOneOf(...)</c>
    ///         <em>replace</em> the term rather than add to it, which is impossible while it is welded
    ///         to each predicate.
    ///     </para>
    /// </remarks>
    private void ApplyTenantFilter(List<ISqlFragment> wheres, LinqQueryParser parser,
        DocumentMapping mapping, string qualifier)
    {
        if (!mapping.IsConjoined)
        {
            // No tenant_id column to filter on, so an operator asking about tenancy has nothing to
            // mean. Refused rather than silently ignored — the same rule the soft-delete operators
            // follow against a type that is not soft-deleted.
            if (parser.TenantScope != TenantScope.Current)
            {
                throw new BadLinqExpressionException(
                    $"'{mapping.DocumentType.Name}' is not multi-tenanted, so it has no tenant_id column "
                    + "for AnyTenant() or TenantIsOneOf() to have an opinion about. Register it with "
                    + "Schema.For<T>().MultiTenanted() if it should be.");
            }

            return;
        }

        switch (parser.TenantScope)
        {
            case TenantScope.Current:
                wheres.Add(new TenantFilterFragment(_session.TenantId, qualifier));
                break;

            case TenantScope.NamedTenants:
                wheres.Add(new WhereInFilter(qualifier + StorageConstants.TenantIdColumn,
                    parser.TenantIds!.Cast<object>().ToList()));
                break;

            case TenantScope.AnyTenant:
                break;
        }
    }

    /// <summary>
    ///     <c>ModifiedSince</c> / <c>ModifiedBefore</c> — bounds on when the row was last written.
    /// </summary>
    /// <remarks>
    ///     A plain comparison against <c>last_modified</c>, with the bound rendered through
    ///     <see cref="SqliteTimestamp" />. None of the <c>strftime</c> normalisation a document's own
    ///     <see cref="DateTimeOffset" /> member needs (fisher#1): the column already holds the
    ///     fixed-width UTC form, chosen so a string comparison <em>is</em> an instant comparison. The
    ///     same asymmetry as <c>DeletedSince</c> / <c>DeletedBefore</c>, and for the same reason.
    /// </remarks>
    private static void ApplyMetadataFilters(List<ISqlFragment> wheres, LinqQueryParser parser,
        string qualifier)
    {
        if (parser.ModifiedSince is { } since)
        {
            wheres.Add(new ComparisonFilter($"{qualifier}last_modified", ">=",
                SqliteTimestamp.ToDatabaseValue(since)));
        }

        if (parser.ModifiedBefore is { } before)
        {
            wheres.Add(new ComparisonFilter($"{qualifier}last_modified", "<",
                SqliteTimestamp.ToDatabaseValue(before)));
        }
    }

    /// <summary>
    ///     Narrow a hierarchy query to the sub-class being asked for.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Once per statement, and independent of everything else — which is the fix for two
    ///         separate ways of getting this wrong. Composing it into <c>FilterDocuments</c> repeats it
    ///         for every caller predicate and omits it from a query with none; hanging it off the
    ///         soft-delete branch omits it for a type that is not soft-deleted, and for the
    ///         <c>DeletedOnly</c> and <c>MaybeDeleted</c> scopes of one that is.
    ///     </para>
    ///     <para>
    ///         Nothing to add when the queried type <em>is</em> the hierarchy's base: every row in the
    ///         table is one.
    ///     </para>
    /// </remarks>
    private static void ApplyHierarchyFilter(List<ISqlFragment> wheres, DocumentMapping mapping,
        Type queryType, string qualifier)
    {
        if (mapping.IsHierarchy && queryType != mapping.DocumentType)
        {
            wheres.Add(new LiteralSqlFragment(
                DocumentHierarchy.FilterSqlFor(mapping, queryType, qualifier)));
        }
    }

    /// <summary>
    ///     Add whichever of <c>is_deleted = 0</c> / <c>= 1</c> the query asked for, plus any
    ///     <c>deleted_at</c> bound.
    /// </summary>
    /// <remarks>
    ///     A soft-delete operator against a type that is not soft-deleted is refused rather than
    ///     ignored: there is no column to answer from, so <c>IsDeleted()</c> would come back empty and
    ///     <c>MaybeDeleted()</c> would come back complete, both of which look like real answers.
    /// </remarks>
    private static void ApplySoftDeleteFilters(List<ISqlFragment> wheres, LinqQueryParser parser,
        DocumentMapping mapping, string qualifier)
    {
        if (!mapping.IsSoftDeleted)
        {
            if (parser.UsedSoftDeleteOperator)
            {
                throw new BadLinqExpressionException(
                    $"'{mapping.DocumentType.Name}' is not configured for soft deletes, so it has no "
                    + "deletion state to query. Mark it with [SoftDeleted], implement ISoftDeleted, or "
                    + "call StoreOptions.Schema.For<T>().SoftDeleted().");
            }

            return;
        }

        switch (parser.SoftDeleteScope)
        {
            case SoftDeleteScope.LiveOnly:
                // SoftDelete owns the SQL, which is the same place the storage's DefaultWhereFragment
                // reads it from — so the query path and the load path still cannot drift. Taken from
                // there rather than from the storage because for a hierarchy sub-class that fragment is
                // a composite, and the discriminator half is added once per statement above rather than
                // once per soft-delete scope.
                wheres.Add(new LiteralSqlFragment(SoftDelete.NotDeletedSqlFor(qualifier)));
                break;

            case SoftDeleteScope.DeletedOnly:
                wheres.Add(new LiteralSqlFragment(SoftDelete.DeletedSqlFor(qualifier)));
                break;

            case SoftDeleteScope.LiveAndDeleted:
                break;
        }

        // deleted_at is SqliteTimestamp's fixed-width UTC text, chosen so that a string comparison is
        // an instant comparison — the same property fi_events.timestamp relies on, and the reason
        // these need none of the strftime normalisation a document's own DateTimeOffset member does.
        if (parser.DeletedSince is { } since)
        {
            wheres.Add(new ComparisonFilter(qualifier + SoftDelete.DeletedAtColumn, ">=",
                SqliteTimestamp.ToDatabaseValue(since)));
        }

        if (parser.DeletedBefore is { } before)
        {
            wheres.Add(new ComparisonFilter(qualifier + SoftDelete.DeletedAtColumn, "<",
                SqliteTimestamp.ToDatabaseValue(before)));
        }
    }

    /// <remarks>
    ///     <b>The span covers building and executing the statement, not materializing the rows</b>,
    ///     and that boundary is deliberate rather than convenient. Every terminal reads rows after this
    ///     returns, so covering materialization would mean a span per terminal — five copies of the
    ///     same three lines, one of which would eventually be forgotten. And the question the span
    ///     exists to answer is where the time went waiting for SQLite, which is entirely inside it.
    /// </remarks>
    private async Task<System.Data.Common.DbDataReader> ExecuteReaderAsync(Statement statement,
        CancellationToken token)
    {
        using var activity = StartQueryActivity(statement);

        var command = await CommandFor(statement, token).ConfigureAwait(false);
        return await command.ExecuteReaderAsync(token).ConfigureAwait(false);
    }

    /// <inheritdoc cref="ExecuteReaderAsync" />
    private async Task<object?> ExecuteScalarAsync(Statement statement, CancellationToken token)
    {
        using var activity = StartQueryActivity(statement);

        var command = await CommandFor(statement, token).ConfigureAwait(false);
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    ///     A span around one LINQ execution, or null when nothing is listening (fisher#48).
    /// </summary>
    /// <remarks>
    ///     Opened here rather than at each terminal because this is the one place every terminal
    ///     converges — a span per terminal would be five copies of the same three lines, and one of
    ///     them would eventually be forgotten.
    /// </remarks>
    private System.Diagnostics.Activity? StartQueryActivity(Statement statement)
    {
        var activity = Internal.FisherTracing.StartOperation(Internal.FisherTracing.Query, _session.Options);

        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag("db.collection.name", statement.FromTable);
            activity.SetTag("fisher.tenant", _session.TenantId);
        }

        return activity;
    }

    private async Task<Microsoft.Data.Sqlite.SqliteCommand> CommandFor(Statement statement,
        CancellationToken token)
    {
        // QueryForNonStaleData: wait for the daemon before reading, not as part of the SQL. Read
        // through the subquery chain so a count, a page, an aggregate or a reversal carries it without
        // its wrap site having to remember to.
        if (statement.EffectiveNonStaleTimeout is { } timeout)
        {
            await _session.FisherDatabase.WaitForNonStaleProjectionDataAsync(timeout).ConfigureAwait(false);
        }

        var builder = new Weasel.Sqlite.CommandBuilder();
        statement.Apply(builder);

        var command = builder.Compile();
        await _session.ConfigureCommandAsync(command, token).ConfigureAwait(false);

        return command;
    }
}
