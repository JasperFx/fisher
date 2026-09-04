using System.Collections;
using System.Linq.Expressions;
using Fisher.Events.Internal;
using Fisher.Internal;
using Fisher.Linq;
using Fisher.Linq.Members;
using Fisher.Linq.Parsing;
using Fisher.Linq.SqlGeneration;
using JasperFx.Events;
using JasperFx.Events.Documents;

namespace Fisher.Events;

/// <summary>
///     The <see cref="IQueryable{T}" /> of <see cref="StreamState" /> behind
///     <see cref="IReadOnlyEventStore.QueryStreamStates" /> (jasperfx#740) — <c>fi_streams</c> as a
///     composable query surface, executed through the shared
///     <see cref="IDocumentQueryExecutor" /> hook the document read tier already dispatches on.
/// </summary>
/// <remarks>
///     <para>
///         A dedicated provider rather than a detour through <see cref="FisherQueryProvider" />,
///         because the document provider's whole pipeline starts from a
///         <c>DocumentMapping</c>/<c>json_extract</c> world and <see cref="StreamState" /> must never
///         become a document (asking the schema for its mapping would register it and give it a
///         table). What IS shared is everything worth sharing: the
///         <see cref="WhereClauseParser" /> over a <see cref="StreamStateMemberFactory" /> — the same
///         split <c>AssignTagWhere</c> uses for <c>fi_events</c> — the
///         <see cref="Statement" /> renderer, and the <see cref="FisherStreamsRowReader" /> that every
///         other <c>fi_streams</c> read materializes through.
///     </para>
///     <para>
///         Session lifetime follows <see cref="FisherReadOnlyEventStore" />'s rule: the queryable is
///         handed out unexecuted, so each terminator opens a session of its own and disposes it before
///         the result is returned — a captured session would pin a pooled connection for as long as
///         the caller keeps the queryable, which for a polling monitoring tool is forever.
///     </para>
///     <para>
///         The supported operator set is the jasperfx#740 contract, not all of LINQ:
///         <c>Where</c> over every <see cref="StreamState" /> member,
///         <c>OrderBy</c>/<c>OrderByDescending</c>/<c>ThenBy</c>/<c>ThenByDescending</c>,
///         <c>Skip</c>/<c>Take</c>, and the four shared async terminators. Anything else is refused
///         naming the operator — the same never-silently-match-all rule the member factory applies one
///         level down.
///     </para>
/// </remarks>
internal sealed class StreamStateQueryProvider : IQueryProvider, IDocumentQueryExecutor
{
    private readonly DocumentStore _store;
    private readonly string? _tenantId;

    /// <param name="store">The store whose streams table is being queried.</param>
    /// <param name="tenantId">
    ///     Explicit tenant scope, already validated by
    ///     <see cref="FisherReadOnlyEventStore.QueryStreamStates" /> to be null on a store without
    ///     conjoined tenancy.
    /// </param>
    internal StreamStateQueryProvider(DocumentStore store, string? tenantId)
    {
        _store = store;
        _tenantId = tenantId;
    }

    private EventGraph Graph => _store.Options.EventGraph;

    internal IQueryable<StreamState> CreateRoot() => new StreamStateQueryable(this);

    public IQueryable CreateQuery(Expression expression) => CreateQuery<StreamState>(expression);

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
    {
        if (typeof(TElement) != typeof(StreamState))
        {
            throw new BadLinqExpressionException(
                $"QueryStreamStates() only answers StreamState rows — it cannot project to "
                + $"'{typeof(TElement).Name}'. Shape the results after they come back.");
        }

        return (IQueryable<TElement>)(object)new StreamStateQueryable(this, expression);
    }

    /// <inheritdoc cref="FisherQueryProvider.Execute(Expression)" />
    public object Execute(Expression expression) => throw SynchronousExecution();

    public TResult Execute<TResult>(Expression expression) => throw SynchronousExecution();

    private static NotSupportedException SynchronousExecution()
        => new("Fisher does not support synchronous LINQ execution. Use one of the async terminal "
               + "operators — ToListAsync, FirstOrDefaultAsync, CountAsync, AnyAsync.");

    // ---- the shared execution hook ----

    async Task<IReadOnlyList<T>> IDocumentQueryExecutor.ExecuteToListAsync<T>(IQueryable<T> queryable,
        CancellationToken token)
    {
        var statement = Translate(queryable.Expression);

        var results = new List<StreamState>();
        await ExecuteAsync(statement, token, async (reader, graph, isGuid) =>
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                results.Add(FisherStreamsRowReader.Read(reader, graph, isGuid));
            }
        }).ConfigureAwait(false);

        return (IReadOnlyList<T>)results;
    }

    async Task<T?> IDocumentQueryExecutor.ExecuteFirstOrDefaultAsync<T>(IQueryable<T> queryable,
        CancellationToken token) where T : default
    {
        var statement = Translate(queryable.Expression);
        statement.Limit = 1;

        StreamState? result = null;
        await ExecuteAsync(statement, token, async (reader, graph, isGuid) =>
        {
            if (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                result = FisherStreamsRowReader.Read(reader, graph, isGuid);
            }
        }).ConfigureAwait(false);

        return (T?)(object?)result;
    }

    async Task<int> IDocumentQueryExecutor.ExecuteCountAsync<T>(IQueryable<T> queryable,
        CancellationToken token)
    {
        var statement = Translate(queryable.Expression);

        // A count over a paged query has to count the page, not the table — the same rule as the
        // document provider's CountAsync.
        if (statement.Limit.HasValue || statement.Offset.HasValue)
        {
            statement.SelectColumns = "1";
            statement = new Statement { Subquery = statement, SelectColumns = "count(*)" };
        }
        else
        {
            statement.SelectColumns = "count(*)";
            statement.OrderBys.Clear();
        }

        return Convert.ToInt32(await ExecuteScalarAsync(statement, token).ConfigureAwait(false));
    }

    async Task<bool> IDocumentQueryExecutor.ExecuteAnyAsync<T>(IQueryable<T> queryable,
        CancellationToken token)
    {
        var statement = Translate(queryable.Expression);
        statement.SelectColumns = "1";
        statement.OrderBys.Clear();
        statement.IsExistsWrapper = true;

        return Convert.ToInt64(await ExecuteScalarAsync(statement, token).ConfigureAwait(false)) != 0;
    }

    // ---- translation ----

    /// <summary>
    ///     Turn the expression chain into a renderable <see cref="Statement" /> over
    ///     <c>fi_streams</c>. Calls are unwrapped outermost-first and replayed in source order, so
    ///     LINQ's own semantics fall out: a later <c>OrderBy</c> replaces the ordering, a
    ///     <c>ThenBy</c> extends it.
    /// </summary>
    private Statement Translate(Expression expression)
    {
        var calls = new List<MethodCallExpression>();
        var current = expression;

        while (current is MethodCallExpression call)
        {
            if (call.Method.DeclaringType != typeof(Queryable))
            {
                throw new BadLinqExpressionException(
                    $"Unsupported method in a stream-state query: "
                    + $"{call.Method.DeclaringType?.Name}.{call.Method.Name}");
            }

            calls.Add(call);
            current = call.Arguments[0];
        }

        if (current is not ConstantExpression { Value: StreamStateQueryable })
        {
            throw new BadLinqExpressionException(
                "The stream-state query does not originate from QueryStreamStates().");
        }

        calls.Reverse();

        var parser = new WhereClauseParser(new StreamStateMemberFactory(Graph));

        var statement = new Statement
        {
            FromTable = Graph.StreamsTableName,
            SelectColumns = FisherStreamsRowReader.SelectColumns
        };

        foreach (var call in calls)
        {
            switch (call.Method.Name)
            {
                case nameof(Queryable.Where):
                    statement.Wheres.Add(parser.Parse(LambdaOf(call).Body));
                    break;

                case nameof(Queryable.OrderBy):
                    statement.OrderBys.Clear();
                    statement.OrderBys.Add((OrderingLocator(call), false));
                    break;

                case nameof(Queryable.OrderByDescending):
                    statement.OrderBys.Clear();
                    statement.OrderBys.Add((OrderingLocator(call), true));
                    break;

                case nameof(Queryable.ThenBy):
                    AssertOrdered(call);
                    statement.OrderBys.Add((OrderingLocator(call), false));
                    break;

                case nameof(Queryable.ThenByDescending):
                    AssertOrdered(call);
                    statement.OrderBys.Add((OrderingLocator(call), true));
                    break;

                case nameof(Queryable.Skip):
                    // Consecutive skips compose additively; a skip after a take would re-window the
                    // page and is refused rather than quietly re-interpreted.
                    if (statement.Limit.HasValue)
                    {
                        throw new BadLinqExpressionException(
                            "Skip() after Take() is not supported on a stream-state query. Apply "
                            + "Skip before Take.");
                    }

                    statement.Offset = (statement.Offset ?? 0)
                                       + (int)WhereClauseParser.ExtractValue(call.Arguments[1])!;
                    break;

                case nameof(Queryable.Take):
                    var take = (int)WhereClauseParser.ExtractValue(call.Arguments[1])!;
                    statement.Limit = statement.Limit.HasValue
                        ? Math.Min(statement.Limit.Value, take)
                        : take;
                    break;

                default:
                    throw new BadLinqExpressionException(
                        $"Unsupported operator in a stream-state query: Queryable.{call.Method.Name}. "
                        + "Supported: Where, OrderBy, OrderByDescending, ThenBy, ThenByDescending, "
                        + "Skip and Take, executed with the async terminators.");
            }
        }

        ApplyTenantScope(statement);

        return statement;

        void AssertOrdered(MethodCallExpression call)
        {
            if (statement.OrderBys.Count == 0)
            {
                throw new BadLinqExpressionException(
                    $"Queryable.{call.Method.Name} requires a preceding OrderBy or OrderByDescending.");
            }
        }

        string OrderingLocator(MethodCallExpression call)
        {
            if (StripConvert(LambdaOf(call).Body) is not MemberExpression memberExpression)
            {
                throw new BadLinqExpressionException(
                    $"Only a StreamState member can order a stream-state query, not "
                    + $"'{LambdaOf(call).Body}'.");
            }

            var member = new StreamStateMemberFactory(Graph).ResolveMember(memberExpression);

            if (!member.AllowsRangeComparison)
            {
                throw new BadLinqExpressionException(
                    $"'StreamState.{memberExpression.Member.Name}' cannot order a stream-state query: "
                    + "its stored form is not order-preserving.");
            }

            return member.TypedLocator;
        }

        static Expression StripConvert(Expression expression)
            => expression is UnaryExpression { NodeType: ExpressionType.Convert } unary
                ? unary.Operand
                : expression;

        static LambdaExpression LambdaOf(MethodCallExpression call)
            => (LambdaExpression)((UnaryExpression)call.Arguments[1]).Operand;
    }

    /// <summary>
    ///     Under conjoined tenancy the queryable is always tenant-scoped: to the explicit tenant when
    ///     <c>QueryStreamStates(tenantId)</c> named one, and to the default session scope otherwise —
    ///     the same rule <see cref="EventOperations.QueryEventsAsync(EventQuery, CancellationToken)" />
    ///     applies to <see cref="EventQuery.TenantId" />. On a single-tenancy store there is no
    ///     tenant dimension worth filtering on, and a non-null tenant was already refused upstream.
    /// </summary>
    private void ApplyTenantScope(Statement statement)
    {
        if (Graph.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined)
        {
            statement.Wheres.Add(new ComparisonFilter("tenant_id", "=",
                _tenantId ?? JasperFx.StorageConstants.DefaultTenantId));
        }
    }

    // ---- execution ----

    private async Task ExecuteAsync(Statement statement, CancellationToken token,
        Func<System.Data.Common.DbDataReader, EventGraph, bool, Task> read)
    {
        await using var session = (FisherSession)_store.LightweightSession();

        var command = await CommandFor(statement, session, token).ConfigureAwait(false);

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        await read(reader, Graph, Graph.StreamIdentity == StreamIdentity.AsGuid).ConfigureAwait(false);
    }

    private async Task<object?> ExecuteScalarAsync(Statement statement, CancellationToken token)
    {
        await using var session = (FisherSession)_store.LightweightSession();

        var command = await CommandFor(statement, session, token).ConfigureAwait(false);

        return await command.ExecuteScalarAsync(token).ConfigureAwait(false);
    }

    private async Task<Microsoft.Data.Sqlite.SqliteCommand> CommandFor(Statement statement,
        FisherSession session, CancellationToken token)
    {
        var builder = new Weasel.Sqlite.CommandBuilder();
        statement.Apply(builder);

        var command = builder.Compile();
        command.Connection = await session.ConnectionAsync(token).ConfigureAwait(false);
        command.CommandTimeout = session.Options.CommandTimeout;

        return command;
    }

    /// <summary>
    ///     The queryable itself. Ordered rather than plain so <c>ThenBy</c> is reachable, the same
    ///     reasoning as <see cref="FisherQueryable{T}" />.
    /// </summary>
    private sealed class StreamStateQueryable : IOrderedQueryable<StreamState>
    {
        internal StreamStateQueryable(StreamStateQueryProvider provider)
        {
            Provider = provider;
            Expression = Expression.Constant(this);
        }

        internal StreamStateQueryable(StreamStateQueryProvider provider, Expression expression)
        {
            Provider = provider;
            Expression = expression;
        }

        public Type ElementType => typeof(StreamState);
        public Expression Expression { get; }
        public IQueryProvider Provider { get; }

        /// <inheritdoc cref="FisherQueryable{T}.GetEnumerator" />
        public IEnumerator<StreamState> GetEnumerator()
            => throw new NotSupportedException(
                "Fisher does not support synchronous LINQ enumeration. Use ToListAsync() or one of the "
                + "other async terminal operators.");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
