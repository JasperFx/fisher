using System.Linq.Expressions;
using JasperFx.Events.Documents;
using JasperFx.Events.Vectors;

namespace Fisher.Internal;

/// <summary>
///     Fisher's <see cref="IDocumentSearchOperations" /> — the store-neutral similarity-search
///     contract, reached through <see cref="IDocumentReadOperations.Search" /> (jasperfx#842,
///     fisher#291).
/// </summary>
/// <remarks>
///     <para>
///         <b>A separate object behind an accessor rather than members on the session, and that is
///         the whole design.</b> Fisher's own entry points are already named
///         <c>VectorSearchWithScoresAsync</c> and <c>HybridSearchWithScoresAsync</c> — as extension
///         methods on <see cref="IQuerySession" />. Members of those names <em>on</em> the session
///         would win overload resolution over the extensions at every existing call site, silently,
///         with no error and different behaviour. Keeping the contract one hop away makes that
///         collision impossible to have.
///     </para>
///     <para>
///         ⚠️ <b>It forwards to the extension methods rather than reimplementing them.</b> A second
///         path to the same search is a second place for the tenant, hierarchy and soft-delete filters
///         to be forgotten, which is exactly the defect fisher#285 records — and a consumer reaching
///         search through the store-agnostic contract is the one least able to notice.
///     </para>
///     <para>
///         Only the two scored methods are here; <c>VectorSearchAsync</c> and <c>HybridSearchAsync</c>
///         come from <c>DocumentSearchExtensions</c> over them, shared with every store so the
///         projection to documents cannot differ between them.
///     </para>
/// </remarks>
internal sealed class FisherDocumentSearchOperations(IQuerySession session) : IDocumentSearchOperations
{
    public Task<IReadOnlyList<VectorMatch<T>>> VectorSearchWithScoresAsync<T>(
        Expression<Func<T, object?>> member,
        ReadOnlyMemory<float> query,
        int limit = 10,
        DistanceFunction? distance = null,
        Expression<Func<T, bool>>? filter = null,
        CancellationToken token = default) where T : notnull
        => session.VectorSearchWithScoresAsync(member, query, limit, distance, filter, token);

    public Task<IReadOnlyList<HybridMatch<T>>> HybridSearchWithScoresAsync<T>(
        Expression<Func<T, object?>> vectorMember,
        string text,
        ReadOnlyMemory<float> query,
        int limit = 10,
        HybridSearchOptions? options = null,
        Expression<Func<T, bool>>? filter = null,
        CancellationToken token = default) where T : notnull
        => session.HybridSearchWithScoresAsync(vectorMember, text, query, limit, options, filter, token);
}
