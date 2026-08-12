using System.Linq.Expressions;
using JasperFx.Events.Documents;

namespace Fisher.Linq;

/// <summary>
///     The provider's half of the store-agnostic document contract (fisher#68 / jasperfx#647).
/// </summary>
/// <remarks>
///     <para>
///         <see cref="IQueryable{T}" /> has no asynchronous execution path of its own, so every
///         LINQ-capable library invents one and JasperFx's shared terminators
///         (<c>ToListAsync</c> / <c>FirstOrDefaultAsync</c> / <c>CountAsync</c> / <c>AnyAsync</c> in
///         <see cref="DocumentQueryableExtensions" />) dispatch through this interface on the
///         <em>provider</em>. It hangs off the provider rather than off <see cref="FisherQueryable{T}" />
///         because <see cref="IQueryable.Provider" /> is preserved by definition across every
///         <c>System.Linq</c> operator, where the queryable's own type is preserved only by convention.
///     </para>
///     <para>
///         <b>Each of the four reads <c>queryable.Expression</c> rather than any expression this
///         provider was built with</b>, because the shared extensions compose <c>Queryable.Where</c>
///         onto the queryable before calling through — that is how the predicate overloads are free for
///         an implementer, and reading a captured expression instead would silently drop the predicate.
///     </para>
///     <para>
///         All four land on the same four provider methods Fisher's own <see cref="QueryableExtensions" />
///         terminals call, so the shared surface and Fisher's are one execution path rather than two
///         that can drift. Deliberately no predicate overloads: the shared extensions narrow the
///         queryable themselves, so a store implements four primitives and nothing more.
///     </para>
///     <para>
///         The contract's <c>T</c> is unconstrained where Fisher's public terminals are
///         <c>where T : notnull</c>, so the four provider methods behind them were widened to match.
///         Nothing downstream wanted the constraint — <c>Weasel.Storage.ISelector&lt;T&gt;</c> is
///         unconstrained and the projected path casts through <c>object?</c> — so it was a claim being
///         made rather than a requirement being met, and dropping it cascaded to nothing. Fisher's own
///         public terminals keep it, since there the constraint is a useful signal to a caller.
///     </para>
/// </remarks>
public partial class FisherQueryProvider : IDocumentQueryExecutor
{
    Task<IReadOnlyList<T>> IDocumentQueryExecutor.ExecuteToListAsync<T>(
        IQueryable<T> queryable, CancellationToken token)
        => ToListAsync<T>(queryable.Expression, token);

    Task<T?> IDocumentQueryExecutor.ExecuteFirstOrDefaultAsync<T>(
        IQueryable<T> queryable, CancellationToken token) where T : default
        => FirstAsync<T>(queryable.Expression, enforceSingle: false, required: false, token);

    async Task<int> IDocumentQueryExecutor.ExecuteCountAsync<T>(
        IQueryable<T> queryable, CancellationToken token)
        => (int)await CountAsync<T>(queryable.Expression, token).ConfigureAwait(false);

    Task<bool> IDocumentQueryExecutor.ExecuteAnyAsync<T>(
        IQueryable<T> queryable, CancellationToken token)
        => AnyAsync<T>(queryable.Expression, token);
}
