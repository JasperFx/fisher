using System.Linq.Expressions;

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

    // ---- predicate overloads ----
    //
    // Each is the predicate-free operator against a Where the caller did not have to write, so they
    // compose rather than duplicating anything. Marten and Polecat both carry them.

    public static Task<int> CountAsync<T>(this IQueryable<T> queryable,
        Expression<Func<T, bool>> predicate, CancellationToken token = default) where T : notnull
        => queryable.Where(predicate).CountAsync(token);

    public static Task<long> LongCountAsync<T>(this IQueryable<T> queryable,
        Expression<Func<T, bool>> predicate, CancellationToken token = default) where T : notnull
        => queryable.Where(predicate).LongCountAsync(token);

    public static Task<bool> AnyAsync<T>(this IQueryable<T> queryable,
        Expression<Func<T, bool>> predicate, CancellationToken token = default) where T : notnull
        => queryable.Where(predicate).AnyAsync(token);

    public static Task<T?> FirstAsync<T>(this IQueryable<T> queryable,
        Expression<Func<T, bool>> predicate, CancellationToken token = default) where T : notnull
        => queryable.Where(predicate).FirstAsync(token);

    public static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> queryable,
        Expression<Func<T, bool>> predicate, CancellationToken token = default) where T : notnull
        => queryable.Where(predicate).FirstOrDefaultAsync(token);

    public static Task<T?> SingleAsync<T>(this IQueryable<T> queryable,
        Expression<Func<T, bool>> predicate, CancellationToken token = default) where T : notnull
        => queryable.Where(predicate).SingleAsync(token);

    public static Task<T?> SingleOrDefaultAsync<T>(this IQueryable<T> queryable,
        Expression<Func<T, bool>> predicate, CancellationToken token = default) where T : notnull
        => queryable.Where(predicate).SingleOrDefaultAsync(token);

    // ---- Last ----

    /// <summary>
    ///     The last row of an ordered query.
    /// </summary>
    /// <remarks>
    ///     Requires an <c>OrderBy</c>, and says so if there is none: SQLite has no <c>LAST</c>, so
    ///     "last" is only meaningful relative to an ordering, and answering from whatever order the
    ///     table happened to yield would be neither stable nor repeatable.
    /// </remarks>
    public static Task<T?> LastAsync<T>(this IQueryable<T> queryable, CancellationToken token = default)
        where T : notnull
        => ProviderFor(queryable).LastAsync<T>(queryable.Expression, required: true, token);

    /// <inheritdoc cref="LastAsync{T}(IQueryable{T},CancellationToken)" />
    public static Task<T?> LastOrDefaultAsync<T>(this IQueryable<T> queryable,
        CancellationToken token = default) where T : notnull
        => ProviderFor(queryable).LastAsync<T>(queryable.Expression, required: false, token);

    /// <inheritdoc cref="LastAsync{T}(IQueryable{T},CancellationToken)" />
    public static Task<T?> LastAsync<T>(this IQueryable<T> queryable, Expression<Func<T, bool>> predicate,
        CancellationToken token = default) where T : notnull
        => queryable.Where(predicate).LastAsync(token);

    /// <inheritdoc cref="LastAsync{T}(IQueryable{T},CancellationToken)" />
    public static Task<T?> LastOrDefaultAsync<T>(this IQueryable<T> queryable,
        Expression<Func<T, bool>> predicate, CancellationToken token = default) where T : notnull
        => queryable.Where(predicate).LastOrDefaultAsync(token);

    // ---- scalar aggregates ----
    //
    // Overload sets match Polecat's, which match Marten's. Sum is closed over the four numeric result
    // types rather than being generic, because the CLR return type is what decides whether SQLite's
    // integer or real result is correct to cast to. Min/Max stay open: a string minimum is a real
    // answer, and so is a timestamp's.

    /// <summary>Sum a member across the query. Zero over no rows.</summary>
    /// <remarks>
    ///     SQLite's <c>sum()</c> returns NULL over no rows where <c>count()</c> returns 0, so the
    ///     empty case is coerced to <c>default</c> rather than being cast — an unguarded cast is the
    ///     obvious bug here and it only shows up on an empty result.
    /// </remarks>
    public static Task<int> SumAsync<T>(this IQueryable<T> queryable, Expression<Func<T, int>> selector,
        CancellationToken token = default) where T : notnull
        => Aggregate<T, int>(queryable, AggregateFunction.Sum, selector, token);

    /// <inheritdoc cref="SumAsync{T}(IQueryable{T},Expression{Func{T,int}},CancellationToken)" />
    public static Task<long> SumAsync<T>(this IQueryable<T> queryable, Expression<Func<T, long>> selector,
        CancellationToken token = default) where T : notnull
        => Aggregate<T, long>(queryable, AggregateFunction.Sum, selector, token);

    /// <summary>
    ///     Sum a decimal member. Accurate to <see cref="double" /> precision, not decimal precision.
    /// </summary>
    /// <remarks>
    ///     SQLite has no decimal type. A decimal member is stored as a JSON number and
    ///     <c>json_extract</c> hands it back as REAL, which is the same reason <c>SqliteTypeFor</c>
    ///     declares REAL for a duplicated decimal column. Stated rather than implied, because the CLR
    ///     return type says <see cref="decimal" /> and would otherwise suggest more precision than the
    ///     sum actually carries.
    /// </remarks>
    public static Task<decimal> SumAsync<T>(this IQueryable<T> queryable,
        Expression<Func<T, decimal>> selector, CancellationToken token = default) where T : notnull
        => Aggregate<T, decimal>(queryable, AggregateFunction.Sum, selector, token);

    /// <inheritdoc cref="SumAsync{T}(IQueryable{T},Expression{Func{T,int}},CancellationToken)" />
    public static Task<double> SumAsync<T>(this IQueryable<T> queryable,
        Expression<Func<T, double>> selector, CancellationToken token = default) where T : notnull
        => Aggregate<T, double>(queryable, AggregateFunction.Sum, selector, token);

    /// <summary>
    ///     The smallest value of a member, or <c>default</c> over no rows.
    /// </summary>
    /// <remarks>
    ///     Open over <typeparamref name="TResult" /> because ordering is what <c>min</c> needs, not
    ///     arithmetic — a string minimum and a timestamp minimum are both real answers. A member whose
    ///     stored form does not order is refused by name, the same guard <c>OrderBy</c> applies.
    /// </remarks>
    public static Task<TResult?> MinAsync<T, TResult>(this IQueryable<T> queryable,
        Expression<Func<T, TResult>> selector, CancellationToken token = default) where T : notnull
        => Aggregate<T, TResult>(queryable, AggregateFunction.Min, selector, token);

    /// <inheritdoc cref="MinAsync{T,TResult}" />
    public static Task<TResult?> MaxAsync<T, TResult>(this IQueryable<T> queryable,
        Expression<Func<T, TResult>> selector, CancellationToken token = default) where T : notnull
        => Aggregate<T, TResult>(queryable, AggregateFunction.Max, selector, token);

    /// <summary>
    ///     The mean of a member, or 0 over no rows.
    /// </summary>
    /// <remarks>
    ///     Returns <see cref="double" /> for every overload because SQLite's <c>avg()</c> is always
    ///     REAL — the same choice Polecat makes, for the same reason.
    /// </remarks>
    public static Task<double> AverageAsync<T>(this IQueryable<T> queryable,
        Expression<Func<T, int>> selector, CancellationToken token = default) where T : notnull
        => Aggregate<T, double>(queryable, AggregateFunction.Average, selector, token);

    /// <inheritdoc cref="AverageAsync{T}(IQueryable{T},Expression{Func{T,int}},CancellationToken)" />
    public static Task<double> AverageAsync<T>(this IQueryable<T> queryable,
        Expression<Func<T, long>> selector, CancellationToken token = default) where T : notnull
        => Aggregate<T, double>(queryable, AggregateFunction.Average, selector, token);

    /// <inheritdoc cref="AverageAsync{T}(IQueryable{T},Expression{Func{T,int}},CancellationToken)" />
    public static Task<double> AverageAsync<T>(this IQueryable<T> queryable,
        Expression<Func<T, decimal>> selector, CancellationToken token = default) where T : notnull
        => Aggregate<T, double>(queryable, AggregateFunction.Average, selector, token);

    /// <inheritdoc cref="AverageAsync{T}(IQueryable{T},Expression{Func{T,int}},CancellationToken)" />
    public static Task<double> AverageAsync<T>(this IQueryable<T> queryable,
        Expression<Func<T, double>> selector, CancellationToken token = default) where T : notnull
        => Aggregate<T, double>(queryable, AggregateFunction.Average, selector, token);

    private static async Task<TResult> Aggregate<T, TResult>(IQueryable<T> queryable,
        AggregateFunction function, LambdaExpression selector, CancellationToken token) where T : notnull
        => (await ProviderFor(queryable)
            .AggregateAsync<T, TResult>(queryable.Expression, function, selector, token)
            .ConfigureAwait(false))!;

    private static FisherQueryProvider ProviderFor<T>(IQueryable<T> queryable)
        => queryable.Provider as FisherQueryProvider
           ?? throw new InvalidOperationException(
               "This async operator only works on a query created by Fisher's session.Query<T>().");
}
