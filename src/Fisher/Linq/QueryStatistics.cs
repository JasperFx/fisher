using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Fisher.Linq;

/// <summary>
///     The total a <c>Stats</c> query fills in — how many rows matched before paging (fisher#202).
/// </summary>
/// <remarks>
///     Mutable and filled by the terminal rather than returned from it, which is what lets the total
///     ride alongside an ordinary <c>IQueryable&lt;T&gt;</c> instead of forcing a wrapper result type.
///     Matches Marten's shape so ported code compiles.
/// </remarks>
public class QueryStatistics
{
    /// <summary>
    ///     How many rows the query's predicates match, ignoring <c>Take</c> and <c>Skip</c>.
    /// </summary>
    /// <remarks>
    ///     Zero until the query has run. There is nothing to distinguish "not run yet" from "matched
    ///     nothing", which is the shape Marten has and the reason this is read after awaiting the
    ///     terminal rather than before.
    /// </remarks>
    public long TotalResults { get; set; }
}

/// <summary>
///     <c>Stats(out QueryStatistics)</c> — a total alongside an arbitrary query (fisher#202).
/// </summary>
/// <remarks>
///     <para>
///         Before this, a real total was reachable only through <c>ToPagedListAsync</c>, whose
///         <c>IPagedList&lt;T&gt;</c> carries one — so a query that was not a page could not ask.
///     </para>
///     <para>
///         <b>The total is a second statement, not <c>count(*) over ()</c>.</b> A window function
///         returns no row at all when the page is past the end, which is exactly when a caller most
///         needs the real total — the same reasoning <c>ToPagedListAsync</c> and the event-store
///         explorer's paging both record.
///     </para>
///     <para>
///         <b>Marten mutates its queryable; Fisher puts the object in the expression tree.</b> Marten's
///         <c>MartenLinqQueryable&lt;T&gt;</c> has a <c>Statistics</c> slot and its own source carries a
///         <c>TODO -- make this be an expression here!</c> beside it. Fisher's <c>FisherQueryable&lt;T&gt;</c>
///         holds no per-query state, and <c>System.Linq</c>'s operators return fresh instances anyway,
///         so a slot would be dropped by the next <c>Where</c>. The marker survives the chain because
///         it <em>is</em> the chain.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2060:DynamicallyAccessedMembers",
    Justification = "Class-level: MakeGenericMethod over this class's own marker methods, which the class itself preserves.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Class-level: LINQ expression construction requires runtime code generation in the general case.")]
public static class StatisticsExtensions
{
    private static readonly MethodInfo StatsMethod =
        typeof(StatisticsExtensions).GetMethod(nameof(Stats),
        [
            typeof(IQueryable<>).MakeGenericType(Type.MakeGenericMethodParameter(0)),
            typeof(QueryStatistics)
        ])!;

    /// <summary>
    ///     Record the unpaged total into <paramref name="statistics"/> when the query runs.
    /// </summary>
    /// <remarks>
    ///     <b>Honoured by the terminals that return rows</b> — <c>ToListAsync</c>,
    ///     <c>ToAsyncEnumerable</c>, the <c>First</c>/<c>Single</c>/<c>Last</c> family, the JSON reads,
    ///     the cursor pages, projections and joins. A scalar terminal (<c>CountAsync</c>,
    ///     <c>AnyAsync</c>, the aggregates) <b>refuses it by name</b>, because there is no second
    ///     number for it to carry and a <see cref="QueryStatistics.TotalResults" /> left at zero would
    ///     be a wrong answer the caller cannot see.
    /// </remarks>
    public static IQueryable<T> Stats<T>(this IQueryable<T> queryable, QueryStatistics statistics)
        => queryable.Provider.CreateQuery<T>(Expression.Call(
            StatsMethod.MakeGenericMethod(typeof(T)), queryable.Expression,
            Expression.Constant(statistics)));

    /// <summary>
    ///     <inheritdoc cref="Stats{T}(IQueryable{T},QueryStatistics)" path="/summary" />
    /// </summary>
    /// <remarks>
    ///     Marten's spelling, so <c>.Stats(out var stats)</c> ports unchanged. The <c>out</c> parameter
    ///     cannot itself be the marker — an expression tree cannot carry a by-ref argument — so this
    ///     creates the object and forwards to the overload that can.
    ///     <inheritdoc cref="Stats{T}(IQueryable{T},QueryStatistics)" path="/remarks" />
    /// </remarks>
    public static IQueryable<T> Stats<T>(this IQueryable<T> queryable, out QueryStatistics statistics)
    {
        statistics = new QueryStatistics();
        return queryable.Stats(statistics);
    }
}
