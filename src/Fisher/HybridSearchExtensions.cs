using System.Linq.Expressions;
using Fisher.Internal;
using Fisher.Linq;
using JasperFx.Events.Vectors;

namespace Fisher;

/// <summary>
///     Hybrid search: reciprocal rank fusion over the full-text and vector legs (fisher#262).
/// </summary>
/// <remarks>
///     <para>
///         <b>Keyword and embedding search fail in different directions</b>, which is the entire
///         argument for fusing them. Full text misses a paraphrase that shares no tokens; vector
///         search misses an exact identifier, a product code, or a rare proper noun the model never
///         learned. Either alone has a recall hole the other does not.
///     </para>
///     <para>
///         <b>RRF rather than a weighted sum of the two scores</b>, and the fusion itself is
///         <see cref="ReciprocalRankFusion" /> — shared with every other store since jasperfx#844,
///         where Fisher used to keep a private copy. bm25 and cosine distance are not on a comparable
///         scale, and normalising them means choosing constants that are wrong for somebody's corpus.
///         RRF uses only the <em>ordinal position</em> in each leg, so the legs need no calibration
///         against each other and the fusion behaves the same whatever the embedding model or the
///         tokenizer.
///     </para>
///     <para>
///         <b>Two statements, fused in memory, rather than one.</b> Each leg already runs as its own
///         SQL through machinery that is tested — the text leg is an ordinary
///         <c>Query&lt;T&gt;()</c> with the full-text predicate and <c>OrderByRelevance()</c>, the
///         vector leg is <c>VectorSearchAsync</c>, which since fisher#285 is built from that same
///         machinery — so the fuse inherits the tenant, soft-delete and hierarchy filters and the
///         existing refusals without restating any of them. One statement would mean a join whose plan
///         neither index serves, and a second place for those filters to be forgotten, which is how
///         fisher#51 happened.
///     </para>
///     <para>
///         <b>The fusion is over the UNION, so a document in one leg only still scores.</b> That is
///         the point rather than a tolerance: a result the keyword leg alone found is exactly what the
///         vector leg is bad at, and vice versa.
///     </para>
///     <para>
///         ⚠️ <b>A type declaring only one of the two indexes is refused by name.</b> Degrading to the
///         available leg would be friendlier and would silently change what the method means — a
///         caller asking for hybrid search and getting keyword search has no way to find out. It is the
///         same stance <c>VectorSearchAsync</c> takes on an undeclared index, and for the same reason:
///         valid SQL that scans the wrong thing is worse than a refusal that names the fix.
///     </para>
/// </remarks>
public static class HybridSearchExtensions
{
    /// <summary>
    ///     The <paramref name="limit" /> documents ranked highest by reciprocal rank fusion over a
    ///     full-text search for <paramref name="text" /> and a vector search for
    ///     <paramref name="query" />.
    /// </summary>
    /// <param name="filter">
    ///     An optional predicate, applied to BOTH legs before each leg's candidate depth — otherwise
    ///     rows the caller will discard consume the depth, and the fused order is a ranking of a set
    ///     that includes them (jasperfx#843).
    /// </param>
    public static async Task<IReadOnlyList<T>> HybridSearchAsync<T>(
        this IQuerySession session,
        Expression<Func<T, object?>> member,
        string text,
        ReadOnlyMemory<float> query,
        int limit = 10,
        HybridSearchOptions? options = null,
        Expression<Func<T, bool>>? filter = null,
        CancellationToken token = default) where T : notnull
        => (await session.HybridSearchWithScoresAsync(member, text, query, limit, options, filter, token)
                .ConfigureAwait(false))
            .Select(x => x.Document)
            .ToList();

    /// <summary>
    ///     The same search, each document paired with its fused score — larger is better — for a
    ///     relevance floor.
    /// </summary>
    /// <inheritdoc cref="HybridSearchAsync{T}" />
    public static async Task<IReadOnlyList<HybridMatch<T>>> HybridSearchWithScoresAsync<T>(
        this IQuerySession session,
        Expression<Func<T, object?>> member,
        string text,
        ReadOnlyMemory<float> query,
        int limit = 10,
        HybridSearchOptions? options = null,
        Expression<Func<T, bool>>? filter = null,
        CancellationToken token = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(text);

        options ??= new HybridSearchOptions();

        var fisher = (FisherSession)session;
        AssertBothLegsAreAvailable<T>(fisher);

        // Every refusal about limit, K and CandidateDepth now lives on the shared options record, so
        // three stores cannot drift apart on which of them still applies (jasperfx#844).
        var depth = options.ResolveCandidateDepth(limit);

        // Resolved up front, for the same reason AssertBothLegsAreAvailable is: the LINQ operator
        // would refuse a wrong-length array too, but its message is about OrderByRelevance, which a
        // hybrid caller never called (fisher#289).
        var weights = options.ResolveColumnWeights(
            fisher.Options.Schema.MappingFor(typeof(T)).FullTextIndex!.ColumnNames.Length);

        // Both legs read through their own tested paths, so the implicit filters and the existing
        // refusals apply without being restated here.
        var textLeg = await TextLegAsync(session, text, options.TextStyle, depth, weights, filter, token)
            .ConfigureAwait(false);
        var vectorLeg = await session
            .VectorSearchAsync(member, query, depth, options.Distance, filter, token).ConfigureAwait(false);

        var storage = fisher.StorageFor<T>();

        return ReciprocalRankFusion.Fuse(textLeg, vectorLeg, storage.IdentityFor, limit, options.K);
    }

    private static Task<IReadOnlyList<T>> TextLegAsync<T>(IQuerySession session, string text,
        HybridTextStyle style, int depth, IReadOnlyList<double>? weights,
        Expression<Func<T, bool>>? filter, CancellationToken token)
        where T : notnull
    {
        var matched = style switch
        {
            HybridTextStyle.WebStyle => session.Query<T>().Where(x => x.WebStyleSearch(text)),
            _ => session.Query<T>().Where(x => x.PlainTextSearch(text))
        };

        if (filter is not null)
        {
            matched = matched.Where(filter);
        }

        // ⚠️ The weights change WHICH documents survive, not just their order. RRF fuses ranks, so
        // this ordering picks who makes the depth cut and how much each survivor contributes — a
        // caller cannot reapply it afterwards, because by then the losers are gone.
        var ranked = weights is null
            ? matched.OrderByRelevance()
            : matched.OrderByRelevance(weights.ToArray());

        return ranked.Take(depth).ToListAsync(token);
    }

    /// <summary>
    ///     Refuse a type that declares only one of the two indexes.
    /// </summary>
    /// <remarks>
    ///     Checked up front rather than left to each leg, so the message is about <em>hybrid</em>
    ///     search rather than about whichever leg happened to run first — a caller told "no vector
    ///     index" by a method they called for its keyword half has to work out the rest themselves.
    /// </remarks>
    private static void AssertBothLegsAreAvailable<T>(FisherSession session)
    {
        var mapping = session.Options.Schema.MappingFor(typeof(T));

        var hasText = mapping.FullTextIndex is not null;
        var hasVector = mapping.VectorIndexes.Count > 0;

        if (hasText && hasVector)
        {
            return;
        }

        var missing = !hasText && !hasVector
            ? "neither a full-text index nor a vector index"
            : hasText
                ? "no vector index"
                : "no full-text index";

        throw new InvalidOperationException(
            $"'{typeof(T).Name}' declares {missing}, so there is no second leg to fuse with and a "
            + "hybrid search cannot mean what it says. It is refused rather than degraded to the "
            + "available leg, because a caller who asked for hybrid search and silently got one half "
            + $"has no way to find out. Declare both — Schema.For<{typeof(T).Name}>().FullTextIndex(...) "
            + $"and .VectorIndex(x => ..., dimensions) — or call Query<{typeof(T).Name}>() with a "
            + "full-text operator, or VectorSearchAsync, for a single-leg search.");
    }
}
