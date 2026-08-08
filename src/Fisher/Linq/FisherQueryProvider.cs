using System.Linq.Expressions;
using Fisher.Internal;
using Fisher.Linq.Members;
using Fisher.Linq.Parsing;
using Fisher.Linq.SqlGeneration;
using Fisher.Storage;
using Weasel.Core.SqlGeneration;
using Weasel.Storage;

namespace Fisher.Linq;

/// <summary>
///     Translates a <see cref="FisherQueryable{T}" />'s expression tree into SQL and runs it.
/// </summary>
/// <remarks>
///     <para>
///         Far smaller than Polecat's provider, which carries grouping, projections, joins, cursor
///         paging and soft deletes. Fisher answers the operators it has SQL for and refuses the rest by
///         name.
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
public class FisherQueryProvider : IQueryProvider
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
        (Statement Statement, SelectProjection Projection) projected, CancellationToken token)
        where T : notnull
    {
        var results = new List<T>();

        await using var reader = await ExecuteReaderAsync(projected.Statement, token).ConfigureAwait(false);

        var values = new object?[projected.Projection.Locators.Length];

        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = reader.IsDBNull(i)
                    ? null
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
        if (ProjectionFor(expression) is { } projected)
        {
            projected.Statement.Limit = enforceSingle ? 2 : 1;

            var rows = await ProjectListAsync<T>(projected, token).ConfigureAwait(false);

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

    internal async Task<long> CountAsync<T>(Expression expression, CancellationToken token)
        where T : notnull
    {
        var (statement, parser, _) = BuildStatement(SourceTypeFor(expression), expression);

        // A projected count has to count the projected rows, and under Distinct that is the whole
        // point of the count — so the projection becomes a subquery rather than having its select
        // list replaced. `select count(*) from (select distinct …)` is the only shape that answers
        // "how many distinct values" correctly.
        if (parser.Projection is not null)
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

    internal async Task<bool> AnyAsync<T>(Expression expression, CancellationToken token)
        where T : notnull
    {
        // Built non-generically so it answers a projected query too — whether any row exists does not
        // depend on what the rows are shaped into.
        var (statement, _, _) = BuildStatement(SourceTypeFor(expression), expression);
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
        (Statement Statement, SelectProjection Projection) projected, bool required, CancellationToken token)
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
    ///     A scalar aggregate — <c>sum</c>, <c>min</c>, <c>max</c> or <c>avg</c> over one member.
    /// </summary>
    /// <remarks>
    ///     The same shape as <see cref="CountAsync{T}" />: swap the select list for the aggregate and
    ///     read one scalar. Paging gets the same treatment for the same reason — <c>Take(5).SumAsync()</c>
    ///     sums the page, not the table — except that the subquery has to project the member rather than
    ///     a literal, so it is aliased and the outer aggregates the alias.
    /// </remarks>
    internal async Task<TResult?> AggregateAsync<T, TResult>(Expression expression,
        AggregateFunction function, LambdaExpression selector, CancellationToken token)
        where T : notnull
    {
        var (statement, _) = Build<T>(expression);
        var locator = AggregateLocatorFor<T>(function, selector);

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
            SelectColumns = $"{locator} as agg_value",
            Limit = statement.Limit,
            Offset = statement.Offset
        };
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
    private string AggregateLocatorFor<T>(AggregateFunction function, LambdaExpression selector)
        where T : notnull
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

        var member = MemberFactoryFor<T>().ResolveMember(memberExpression);

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
            SelectColumns = "1",
            Limit = statement.Limit,
            Offset = statement.Offset
        };
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
    private (Statement Statement, SelectProjection Projection)? ProjectionFor(Expression expression)
    {
        var (statement, parser, _) = BuildStatement(SourceTypeFor(expression), expression);

        return parser.Projection is null ? null : (statement, parser.Projection);
    }

    private (Statement Statement, ISelector<T> Selector) Build<T>(Expression expression) where T : notnull
    {
        var (statement, parser, selectClause) = BuildStatement(typeof(T), expression);

        if (parser.Projection is not null)
        {
            throw new BadLinqExpressionException(
                $"This query projects with Select, so it does not return {typeof(T).Name} documents.");
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
    private (Statement Statement, LinqQueryParser Parser, ISelectClause SelectClause) BuildStatement(
        Type sourceType, Expression expression)
    {
        var mapping = _session.Options.Schema.MappingFor(sourceType);
        var storage = ((IStorageSession)_session).StorageFor(sourceType);

        if (storage is not ISelectClause selectClause)
        {
            throw new BadLinqExpressionException(
                $"The storage for '{sourceType.Name}' cannot produce a select clause.");
        }

        var parser = new LinqQueryParser(new MemberFactory(_session.Options, mapping));
        parser.Parse(expression);

        var statement = new Statement
        {
            FromTable = selectClause.FromObject,
            SelectColumns = parser.Projection is null
                ? string.Join(", ", selectClause.SelectFields())
                : string.Join(", ", parser.Projection.Locators),
            Limit = parser.Limit,
            Offset = parser.Offset
        };

        statement.OrderBys.AddRange(parser.OrderBys);

        // Route the predicate through the storage so a conjoined table gets its tenant filter. Going
        // around it is how a query path silently stops honouring tenancy the moment it lands.
        foreach (var where in parser.Wheres)
        {
            statement.Wheres.Add(storage.FilterDocuments(where, _session));
        }

        ApplyHierarchyFilter(statement, mapping, sourceType);
        ApplySoftDeleteFilters(statement, parser, storage, mapping);

        return (ApplyDistinct(statement, parser, selectClause), parser, selectClause);
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
            if (parser.Projection is null)
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
    private static void ApplyHierarchyFilter(Statement statement, DocumentMapping mapping, Type queryType)
    {
        if (mapping.IsHierarchy && queryType != mapping.DocumentType)
        {
            statement.Wheres.Add(new LiteralSqlFragment(
                DocumentHierarchy.FilterSqlFor(mapping, queryType)));
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
    private static void ApplySoftDeleteFilters(Statement statement, LinqQueryParser parser,
        Weasel.Storage.IDocumentStorage storage, DocumentMapping mapping)
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
                statement.Wheres.Add(new LiteralSqlFragment(SoftDelete.NotDeletedSql));
                break;

            case SoftDeleteScope.DeletedOnly:
                statement.Wheres.Add(new LiteralSqlFragment(SoftDelete.DeletedSql));
                break;

            case SoftDeleteScope.LiveAndDeleted:
                break;
        }

        // deleted_at is SqliteTimestamp's fixed-width UTC text, chosen so that a string comparison is
        // an instant comparison — the same property fi_events.timestamp relies on, and the reason
        // these need none of the strftime normalisation a document's own DateTimeOffset member does.
        if (parser.DeletedSince is { } since)
        {
            statement.Wheres.Add(new ComparisonFilter(SoftDelete.DeletedAtColumn, ">=",
                SqliteTimestamp.ToDatabaseValue(since)));
        }

        if (parser.DeletedBefore is { } before)
        {
            statement.Wheres.Add(new ComparisonFilter(SoftDelete.DeletedAtColumn, "<",
                SqliteTimestamp.ToDatabaseValue(before)));
        }
    }

    private async Task<System.Data.Common.DbDataReader> ExecuteReaderAsync(Statement statement,
        CancellationToken token)
    {
        var command = await CommandFor(statement, token).ConfigureAwait(false);
        return await command.ExecuteReaderAsync(token).ConfigureAwait(false);
    }

    private async Task<object?> ExecuteScalarAsync(Statement statement, CancellationToken token)
    {
        var command = await CommandFor(statement, token).ConfigureAwait(false);
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false);
    }

    private async Task<Microsoft.Data.Sqlite.SqliteCommand> CommandFor(Statement statement,
        CancellationToken token)
    {
        var builder = new Weasel.Sqlite.CommandBuilder();
        statement.Apply(builder);

        var command = builder.Compile();
        command.Connection = await _session.ConnectionAsync(token).ConfigureAwait(false);
        command.CommandTimeout = _session.Options.CommandTimeout;

        return command;
    }
}
