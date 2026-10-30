using System.Collections;
using System.Data.Common;
using System.Linq.Expressions;
using Fisher.Internal;
using Fisher.Linq;
using Fisher.Linq.Members;
using Fisher.Linq.Parsing;
using Fisher.Linq.SqlGeneration;
using JasperFx.Events.Documents;

namespace Fisher.Events;

/// <summary>
///     The shared half of the event store's two read-only <see cref="IQueryable{T}" /> surfaces —
///     <see cref="StreamStateQueryProvider" /> over <c>fi_streams</c> (jasperfx#740) and
///     <see cref="EventDataQueryProvider{T}" /> over the bodies in <c>fi_events</c> (jasperfx#1073) —
///     executed through the shared <see cref="IDocumentQueryExecutor" /> hook.
/// </summary>
/// <remarks>
///     <para>
///         What a subclass decides is only what differs between the two tables: which
///         <see cref="IMemberResolver" /> turns a member into SQL, the <c>FROM</c> and the select list,
///         the scope every query carries (tenant, and for event bodies the event type), and how one row
///         becomes an element. Everything else is this class, so the two cannot drift apart on the parts
///         of the contract they share: the operator set is <c>Where</c>,
///         <c>OrderBy</c>/<c>OrderByDescending</c>/<c>ThenBy</c>/<c>ThenByDescending</c>,
///         <c>Skip</c>/<c>Take</c> and the four shared async terminators, and anything else is refused
///         naming the operator — the never-silently-match-all rule the member resolvers apply one level
///         down.
///     </para>
///     <para>
///         Session lifetime follows <see cref="FisherReadOnlyEventStore" />'s rule: the queryable is
///         handed out unexecuted, so each terminator opens a session of its own and disposes it before
///         the result is returned — a captured session would pin a pooled connection for as long as the
///         caller keeps the queryable, which for a polling monitoring tool is forever.
///     </para>
///     <para>
///         <b>A tenant scope is a predicate and a database, and this class owns the database half</b>
///         (jasperfx#885). Each execution opens its session for the provider's tenant, so under
///         database-per-tenant the read lands in that tenant's file; the subclass adds the conjoined
///         <c>tenant_id</c> term in <see cref="ApplyScope" />.
///     </para>
/// </remarks>
internal abstract class EventTableQueryProvider<TElement> : IQueryProvider, IDocumentQueryExecutor
    where TElement : class
{
    protected EventTableQueryProvider(DocumentStore store, string? tenantId)
    {
        Store = store;
        TenantId = tenantId;
    }

    protected DocumentStore Store { get; }

    /// <summary>The explicit tenant scope, already validated by <see cref="FisherReadOnlyEventStore" />.</summary>
    protected string? TenantId { get; }

    protected EventGraph Graph => Store.Options.EventGraph;

    /// <summary>The member the queryable came from, for messages — <c>QueryStreamStates()</c>.</summary>
    protected abstract string Origin { get; }

    /// <summary>What a query of this kind is called, for messages — <c>stream-state query</c>.</summary>
    protected abstract string QueryKind { get; }

    /// <summary>The element's name in messages, <c>StreamState</c> or the event type's name.</summary>
    protected virtual string ElementName => typeof(TElement).Name;

    /// <summary>Resolves a member of <typeparamref name="TElement" /> to SQL, for both Where and OrderBy.</summary>
    protected abstract IMemberResolver CreateMemberResolver();

    /// <summary>The bare statement — table and select list — before any operator is applied.</summary>
    protected abstract Statement CreateStatement();

    /// <summary>The scope every query carries whatever the caller composed: the conjoined tenant term, at least.</summary>
    protected abstract void ApplyScope(Statement statement);

    /// <summary>Materialize the current row, or null to skip it.</summary>
    protected abstract TElement? Read(DbDataReader reader, FisherSession session);

    /// <summary>
    ///     Materialize the current row asynchronously. Defaults to <see cref="Read" />; an element whose
    ///     hydration can be asynchronous (an upcast event, fisher#191) overrides this instead.
    /// </summary>
    protected virtual ValueTask<TElement?> ReadAsync(DbDataReader reader, FisherSession session,
        CancellationToken token) => new(Read(reader, session));

    /// <summary>
    ///     Called once the caller's operators are applied, so a subclass can append a tiebreak that
    ///     makes the store's own order deterministic. A count or an existence check clears it.
    /// </summary>
    protected virtual void ApplyFinalOrdering(Statement statement)
    {
    }

    internal IQueryable<TElement> CreateRoot() => new EventTableQueryable(this);

    public IQueryable CreateQuery(Expression expression) => CreateQuery<TElement>(expression);

    public IQueryable<TResult> CreateQuery<TResult>(Expression expression)
    {
        if (typeof(TResult) != typeof(TElement))
        {
            throw new BadLinqExpressionException(
                $"{Origin} only answers {ElementName} rows — it cannot project to "
                + $"'{typeof(TResult).Name}'. Shape the results after they come back.");
        }

        return (IQueryable<TResult>)(object)new EventTableQueryable(this, expression);
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

        var results = new List<TElement>();
        await ExecuteAsync(statement, token, async (reader, session) =>
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (await ReadAsync(reader, session, token).ConfigureAwait(false) is { } element)
                {
                    results.Add(element);
                }
            }
        }).ConfigureAwait(false);

        return (IReadOnlyList<T>)results;
    }

    async Task<T?> IDocumentQueryExecutor.ExecuteFirstOrDefaultAsync<T>(IQueryable<T> queryable,
        CancellationToken token) where T : default
    {
        var statement = Translate(queryable.Expression);
        statement.Limit = 1;

        TElement? result = null;
        await ExecuteAsync(statement, token, async (reader, session) =>
        {
            if (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                result = await ReadAsync(reader, session, token).ConfigureAwait(false);
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
    ///     Turn the expression chain into a renderable <see cref="Statement" />. Calls are unwrapped
    ///     outermost-first and replayed in source order, so LINQ's own semantics fall out: a later
    ///     <c>OrderBy</c> replaces the ordering, a <c>ThenBy</c> extends it.
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
                    $"Unsupported method in a {QueryKind}: "
                    + $"{call.Method.DeclaringType?.Name}.{call.Method.Name}");
            }

            calls.Add(call);
            current = call.Arguments[0];
        }

        if (current is not ConstantExpression { Value: EventTableQueryable })
        {
            throw new BadLinqExpressionException($"The {QueryKind} does not originate from {Origin}.");
        }

        calls.Reverse();

        var members = CreateMemberResolver();
        var parser = new WhereClauseParser(members);

        var statement = CreateStatement();

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
                            $"Skip() after Take() is not supported on a {QueryKind}. Apply Skip before Take.");
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
                        $"Unsupported operator in a {QueryKind}: Queryable.{call.Method.Name}. "
                        + "Supported: Where, OrderBy, OrderByDescending, ThenBy, ThenByDescending, "
                        + "Skip and Take, executed with the async terminators.");
            }
        }

        ApplyScope(statement);
        ApplyFinalOrdering(statement);

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
                    $"Only a {ElementName} member can order a {QueryKind}, not "
                    + $"'{LambdaOf(call).Body}'.");
            }

            var member = members.ResolveMember(memberExpression);

            if (!member.AllowsRangeComparison)
            {
                throw new BadLinqExpressionException(
                    $"'{ElementName}.{memberExpression.Member.Name}' cannot order a {QueryKind}: "
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

    // ---- execution ----

    private async Task ExecuteAsync(Statement statement, CancellationToken token,
        Func<DbDataReader, FisherSession, Task> read)
    {
        await using var session = (FisherSession)Store.LightweightSession(TenantId);

        var command = await CommandFor(statement, session, token).ConfigureAwait(false);

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        await read(reader, session).ConfigureAwait(false);
    }

    private async Task<object?> ExecuteScalarAsync(Statement statement, CancellationToken token)
    {
        await using var session = (FisherSession)Store.LightweightSession(TenantId);

        var command = await CommandFor(statement, session, token).ConfigureAwait(false);

        return await command.ExecuteScalarAsync(token).ConfigureAwait(false);
    }

    private static async Task<Microsoft.Data.Sqlite.SqliteCommand> CommandFor(Statement statement,
        FisherSession session, CancellationToken token)
    {
        var builder = new Weasel.Sqlite.CommandBuilder();
        statement.Apply(builder);

        var command = builder.Compile();
        command.Connection = await session.EventConnectionAsync(token).ConfigureAwait(false);
        command.CommandTimeout = session.Options.CommandTimeout;

        return command;
    }

    /// <summary>
    ///     The queryable itself. Ordered rather than plain so <c>ThenBy</c> is reachable, the same
    ///     reasoning as <see cref="FisherQueryable{T}" />.
    /// </summary>
    private sealed class EventTableQueryable : IOrderedQueryable<TElement>
    {
        internal EventTableQueryable(EventTableQueryProvider<TElement> provider)
        {
            Provider = provider;
            Expression = Expression.Constant(this);
        }

        internal EventTableQueryable(EventTableQueryProvider<TElement> provider, Expression expression)
        {
            Provider = provider;
            Expression = expression;
        }

        public Type ElementType => typeof(TElement);
        public Expression Expression { get; }
        public IQueryProvider Provider { get; }

        /// <inheritdoc cref="FisherQueryable{T}.GetEnumerator" />
        public IEnumerator<TElement> GetEnumerator()
            => throw new NotSupportedException(
                "Fisher does not support synchronous LINQ enumeration. Use ToListAsync() or one of the "
                + "other async terminal operators.");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
