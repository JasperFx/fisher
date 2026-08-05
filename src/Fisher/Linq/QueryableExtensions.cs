namespace Fisher.Linq;

/// <summary>
///     The async terminal operators for a Fisher query.
/// </summary>
/// <remarks>
///     Extension methods rather than members on <see cref="FisherQueryable{T}" /> because the chain's
///     intermediate operators (<c>Where</c>, <c>OrderBy</c>) come from
///     <see cref="System.Linq.Queryable" /> and hand back a plain <see cref="IQueryable{T}" />; only an
///     extension can be called on the result of those.
///     <para>
///         Each rejects a queryable that is not Fisher's, rather than silently falling back to
///         client-side evaluation — pulling a whole table into memory to satisfy a
///         <c>CountAsync</c> is the kind of help nobody wants.
///     </para>
/// </remarks>
public static class QueryableExtensions
{
    public static Task<IReadOnlyList<T>> ToListAsync<T>(this IQueryable<T> queryable,
        CancellationToken token = default) where T : notnull
        => ProviderFor(queryable).ToListAsync<T>(queryable.Expression, token);

    public static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> queryable,
        CancellationToken token = default) where T : notnull
        => ProviderFor(queryable).FirstAsync<T>(queryable.Expression, enforceSingle: false, required: false, token);

    public static Task<T?> FirstAsync<T>(this IQueryable<T> queryable, CancellationToken token = default)
        where T : notnull
        => ProviderFor(queryable).FirstAsync<T>(queryable.Expression, enforceSingle: false, required: true, token);

    public static Task<T?> SingleOrDefaultAsync<T>(this IQueryable<T> queryable,
        CancellationToken token = default) where T : notnull
        => ProviderFor(queryable).FirstAsync<T>(queryable.Expression, enforceSingle: true, required: false, token);

    public static Task<T?> SingleAsync<T>(this IQueryable<T> queryable, CancellationToken token = default)
        where T : notnull
        => ProviderFor(queryable).FirstAsync<T>(queryable.Expression, enforceSingle: true, required: true, token);

    public static async Task<int> CountAsync<T>(this IQueryable<T> queryable,
        CancellationToken token = default) where T : notnull
        => (int)await ProviderFor(queryable).CountAsync<T>(queryable.Expression, token).ConfigureAwait(false);

    public static Task<long> LongCountAsync<T>(this IQueryable<T> queryable,
        CancellationToken token = default) where T : notnull
        => ProviderFor(queryable).CountAsync<T>(queryable.Expression, token);

    public static Task<bool> AnyAsync<T>(this IQueryable<T> queryable, CancellationToken token = default)
        where T : notnull
        => ProviderFor(queryable).AnyAsync<T>(queryable.Expression, token);

    private static FisherQueryProvider ProviderFor<T>(IQueryable<T> queryable)
        => queryable.Provider as FisherQueryProvider
           ?? throw new InvalidOperationException(
               "This async operator only works on a query created by Fisher's session.Query<T>().");
}
