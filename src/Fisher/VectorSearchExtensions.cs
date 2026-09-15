using System.Linq.Expressions;
using Fisher.Linq;
using JasperFx.Events.Vectors;

namespace Fisher;

/// <summary>
///     Vector similarity search over a declared embedding member (fisher#241), on Marten.PgVector's
///     API shape so application code written against one store reads the same against the other.
/// </summary>
/// <remarks>
///     <para>
///         Both overloads run one statement built by the same machinery <c>Query&lt;T&gt;()</c> uses:
///         the document's columns, plus <c>fi_vector_distance</c> over the member's
///         <c>json_extract</c> locator and the query vector bound as a float32 BLOB, ordered by that
///         distance, limited. <b>Every filter <c>Query&lt;T&gt;()</c> would apply applies here</b> —
///         conjoined tenancy, the <c>doc_type</c> hierarchy discriminator and soft deletes — because
///         they come from that machinery rather than from a restatement of it (fisher#285).
///     </para>
///     <para>
///         A query vector whose length is not the index's declared <c>dimensions</c> is refused before
///         any SQL runs. A stored vector of the wrong length fails the row at query time rather than
///         scoring it — see <c>VectorFunctions</c>.
///     </para>
///     <para>
///         The store-neutral form of these two methods is
///         <see cref="IDocumentSearchOperations" />, reached through <c>session.Search</c>. These stay
///         as the native spelling; see <see cref="Internal.FisherDocumentSearchOperations" /> for why
///         the shared contract is behind an accessor rather than on the session itself.
///     </para>
/// </remarks>
public static class VectorSearchExtensions
{
    /// <summary>
    ///     The <paramref name="limit" /> documents nearest to <paramref name="query" /> under the
    ///     index's distance (or <paramref name="distance" /> when given), nearest first.
    /// </summary>
    /// <param name="filter">
    ///     An optional predicate, applied BEFORE <paramref name="limit" /> — so the result is the top-k
    ///     of the filtered set rather than the filtered remains of the top-k. Fisher scans every row,
    ///     so there is no recall caveat: it supports and refuses exactly what
    ///     <c>Query&lt;T&gt;().Where(...)</c> does.
    /// </param>
    public static async Task<IReadOnlyList<T>> VectorSearchAsync<T>(
        this IQuerySession session,
        Expression<Func<T, object?>> member,
        ReadOnlyMemory<float> query,
        int limit = 10,
        DistanceFunction? distance = null,
        Expression<Func<T, bool>>? filter = null,
        CancellationToken token = default) where T : notnull
    {
        var matches = await session
            .VectorSearchWithScoresAsync(member, query, limit, distance, filter, token)
            .ConfigureAwait(false);

        return matches.Select(x => x.Document).ToList();
    }

    /// <summary>
    ///     The same search, each document paired with its distance — smaller is closer under every
    ///     metric — for a similarity floor, or for fusing with a full-text ranking.
    /// </summary>
    /// <inheritdoc cref="VectorSearchAsync{T}" />
    public static Task<IReadOnlyList<VectorMatch<T>>> VectorSearchWithScoresAsync<T>(
        this IQuerySession session,
        Expression<Func<T, object?>> member,
        ReadOnlyMemory<float> query,
        int limit = 10,
        DistanceFunction? distance = null,
        Expression<Func<T, bool>>? filter = null,
        CancellationToken token = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(session);

        return ((FisherQueryProvider)session.Query<T>().Provider)
            .VectorSearchAsync(member, query, limit, distance, filter, token);
    }
}
