using System.Linq.Expressions;
using Fisher.Internal;
using Fisher.Linq.Members;
using Fisher.Linq.Parsing;
using Fisher.Linq.SqlGeneration;
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
        var (statement, selector) = Build<T>(expression);

        var results = new List<T>();
        await using var reader = await ExecuteReaderAsync(statement, token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            results.Add(await selector.ResolveAsync(reader, token).ConfigureAwait(false));
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
        var (statement, _) = Build<T>(expression);
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
        var (statement, _) = Build<T>(expression);
        statement.SelectColumns = "1";
        statement.OrderBys.Clear();
        statement.IsExistsWrapper = true;

        return Convert.ToInt64(await ExecuteScalarAsync(statement, token).ConfigureAwait(false)) != 0;
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

    private (Statement Statement, ISelector<T> Selector) Build<T>(Expression expression) where T : notnull
    {
        var mapping = _session.Options.Schema.For<T>();
        var storage = _session.FisherDatabase.Providers.StorageFor<T>().QueryOnly;

        if (storage is not ISelectClause selectClause)
        {
            throw new BadLinqExpressionException(
                $"The storage for '{typeof(T).Name}' cannot produce a select clause.");
        }

        var parser = new LinqQueryParser(new MemberFactory(_session.Options, mapping));
        parser.Parse(expression);

        var statement = new Statement
        {
            FromTable = selectClause.FromObject,
            SelectColumns = string.Join(", ", selectClause.SelectFields()),
            Limit = parser.Limit,
            Offset = parser.Offset
        };

        statement.OrderBys.AddRange(parser.OrderBys);

        // Route the predicate through the storage so a conjoined table gets its tenant filter, and
        // pick up any default filter the storage imposes. Both are no-ops today, but going around them
        // is how a query path silently stops honouring tenancy the moment it lands.
        foreach (var where in parser.Wheres)
        {
            statement.Wheres.Add(storage.FilterDocuments(where, _session));
        }

        if (storage.DefaultWhereFragment() is { } defaultWhere)
        {
            statement.Wheres.Add(defaultWhere);
        }

        return (statement, (ISelector<T>)selectClause.BuildSelector(_session));
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
