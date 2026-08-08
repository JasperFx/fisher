namespace Fisher.Batching;

/// <summary>
///     A named query, as a class rather than a lambda repeated at call sites (fisher#37).
/// </summary>
/// <remarks>
///     The specification pattern, ported unchanged from Polecat — there is nothing dialect-specific
///     about it. Worth having for the same reason a named method beats an inline predicate: the query
///     gets a name, a home and a test of its own.
/// </remarks>
public interface IQueryPlan<T>
{
    Task<T> Fetch(IQuerySession session, CancellationToken token);
}

/// <summary>
///     The same plan, runnable inside an <see cref="IBatchedQuery" />.
/// </summary>
public interface IBatchQueryPlan<T>
{
    Task<T> Fetch(IBatchedQuery query);
}

/// <summary>
///     A plan that is just a LINQ query, implementing both interfaces from one method.
/// </summary>
/// <remarks>
///     Most plans are this shape, and writing <see cref="Query" /> once is what keeps the batched and
///     unbatched paths from drifting into two different queries with one name.
/// </remarks>
public abstract class QueryListPlan<T> : IQueryPlan<IReadOnlyList<T>>, IBatchQueryPlan<IReadOnlyList<T>>
    where T : notnull
{
    public abstract IQueryable<T> Query(IQuerySession session);

    Task<IReadOnlyList<T>> IQueryPlan<IReadOnlyList<T>>.Fetch(IQuerySession session, CancellationToken token)
        => Linq.QueryableExtensions.ToListAsync(Query(session), token);

    Task<IReadOnlyList<T>> IBatchQueryPlan<IReadOnlyList<T>>.Fetch(IBatchedQuery query)
        => query.Query(Query);
}
