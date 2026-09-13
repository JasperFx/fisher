using System.Linq.Expressions;
using Fisher.Internal;
using Fisher.Linq;

namespace Fisher;

/// <summary>
///     How the two legs of a <see cref="HybridSearchExtensions">hybrid search</see> are fused
///     (fisher#262).
/// </summary>
/// <param name="K">
///     Reciprocal rank fusion's smoothing constant. Conventionally 60, which is what the original RRF
///     paper used and what every implementation since has defaulted to. Larger flattens the
///     difference between ranks; smaller makes the top of each leg dominate.
/// </param>
/// <param name="CandidateDepth">
///     How deep to read each leg before fusing. Null takes <c>max(limit × 4, 50)</c>.
///     <b>It has to exceed <c>limit</c>, and that is the whole point</b>: a document ranked 40th by
///     one leg and 1st by the other is exactly the result hybrid search exists to surface, and reading
///     only <c>limit</c> from each leg would never see it.
/// </param>
/// <param name="Distance">
///     Override the vector index's declared distance function, as <c>VectorSearchAsync</c> allows.
/// </param>
/// <param name="TextStyle">How the text is turned into an FTS5 query. See <see cref="HybridTextStyle" />.</param>
public sealed record HybridSearchOptions(
    int K = 60,
    int? CandidateDepth = null,
    JasperFx.Events.Vectors.DistanceFunction? Distance = null,
    HybridTextStyle TextStyle = HybridTextStyle.PlainText);

/// <summary>
///     Which full-text operator the text leg uses.
/// </summary>
/// <remarks>
///     <b>Both members are safe to hand a search box's raw contents, and that is why the list is
///     short.</b> <c>Search</c>'s raw FTS5 syntax is deliberately absent: it can be malformed, and a
///     malformed query in one leg of a fused search fails the whole call — where in a plain
///     <c>Where(x =&gt; x.Search(...))</c> it fails the one thing the caller asked for. The other
///     full-text operators (phrase, prefix, ngram) are reachable through <c>Query&lt;T&gt;()</c> and
///     are not what a hybrid search is usually fed.
/// </remarks>
public enum HybridTextStyle
{
    /// <summary>Every word, in any order, with no query syntax at all. The default.</summary>
    PlainText,

    /// <summary>Quoted phrases, <c>or</c> between alternatives, a leading <c>-</c> to exclude.</summary>
    WebStyle
}

/// <summary>
///     A document and the fused score that ranked it.
/// </summary>
/// <remarks>
///     <b>Larger is better</b>, which is the opposite of <c>VectorMatch&lt;T&gt;.Distance</c> and of
///     bm25 — an RRF score is a sum of reciprocals, so it rises with agreement between the legs. The
///     absolute value means little on its own; what it supports is a floor, or a comparison between
///     results of the same query.
/// </remarks>
public sealed record HybridMatch<T>(T Document, double Score);

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
///         <b>RRF rather than a weighted sum of the two scores.</b> bm25 and cosine distance are not
///         on a comparable scale, and normalising them means choosing constants that are wrong for
///         somebody's corpus. RRF uses only the <em>ordinal position</em> in each leg, so the legs need
///         no calibration against each other and the fusion behaves the same whatever the embedding
///         model or the tokenizer.
///     </para>
///     <para>
///         <b>Two statements, fused in memory, rather than one.</b> Each leg already runs as its own
///         SQL through machinery that is tested — the text leg is an ordinary
///         <c>Query&lt;T&gt;()</c> with the full-text predicate and <c>OrderByRelevance()</c>, the
///         vector leg is <c>VectorSearchAsync</c> — so the fuse inherits the tenant, soft-delete and
///         hierarchy filters and the existing refusals without restating any of them. One statement
///         would mean a join whose plan neither index serves, and a second place for those filters to
///         be forgotten, which is how fisher#51 happened.
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
    public static async Task<IReadOnlyList<T>> HybridSearchAsync<T>(
        this IQuerySession session,
        Expression<Func<T, object?>> member,
        string text,
        ReadOnlyMemory<float> query,
        int limit = 10,
        HybridSearchOptions? options = null,
        CancellationToken token = default) where T : notnull
        => (await session.HybridSearchWithScoresAsync(member, text, query, limit, options, token)
                .ConfigureAwait(false))
            .Select(x => x.Document)
            .ToList();

    /// <summary>
    ///     The same search, each document paired with its fused score — larger is better — for a
    ///     relevance floor.
    /// </summary>
    public static async Task<IReadOnlyList<HybridMatch<T>>> HybridSearchWithScoresAsync<T>(
        this IQuerySession session,
        Expression<Func<T, object?>> member,
        string text,
        ReadOnlyMemory<float> query,
        int limit = 10,
        HybridSearchOptions? options = null,
        CancellationToken token = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(text);
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit must be at least 1");

        options ??= new HybridSearchOptions();

        if (options.K < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.K,
                "K must be at least 1. It is reciprocal rank fusion's smoothing constant, and 0 would "
                + "make the top-ranked document of either leg score infinitely.");
        }

        var fisher = (FisherSession)session;
        AssertBothLegsAreAvailable<T>(fisher);

        var depth = options.CandidateDepth ?? Math.Max(limit * 4, 50);

        if (depth < limit)
        {
            throw new ArgumentOutOfRangeException(nameof(options), depth,
                $"CandidateDepth ({depth}) is below limit ({limit}), so the fusion would have fewer "
                + "candidates than it is asked to return. It exists to read DEEPER than limit: a "
                + "document ranked low by one leg and first by the other is what hybrid search is for.");
        }

        // Both legs read through their own tested paths, so the implicit filters and the existing
        // refusals apply without being restated here.
        var textLeg = await TextLegAsync<T>(session, text, options.TextStyle, depth, token).ConfigureAwait(false);
        var vectorLeg = await session
            .VectorSearchAsync(member, query, depth, options.Distance, token).ConfigureAwait(false);

        return Fuse(fisher, textLeg, vectorLeg, options.K, limit);
    }

    private static Task<IReadOnlyList<T>> TextLegAsync<T>(IQuerySession session, string text,
        HybridTextStyle style, int depth, CancellationToken token) where T : notnull
    {
        var matched = style switch
        {
            HybridTextStyle.WebStyle => session.Query<T>().Where(x => x.WebStyleSearch(text)),
            _ => session.Query<T>().Where(x => x.PlainTextSearch(text))
        };

        return matched.OrderByRelevance().Take(depth).ToListAsync(token);
    }

    /// <summary>
    ///     Reciprocal rank fusion: <c>score(d) = Σ 1 / (k + rank(d))</c> over the legs that found it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Ranks are 1-based, which is what makes <c>k</c> mean what the literature says it means.
    ///     </para>
    ///     <para>
    ///         <b>Ties are broken deterministically, and that is not tidiness.</b> Two documents found
    ///         at the same rank by one leg and by neither in the other have identical scores, which is
    ///         common rather than exotic; without a total order the page they land on differs between
    ///         runs, which is the shape the LINQ keyset paging guard exists for. Best rank first, then
    ///         the identity, which is unique by construction.
    ///     </para>
    /// </remarks>
    private static IReadOnlyList<HybridMatch<T>> Fuse<T>(FisherSession session,
        IReadOnlyList<T> textLeg, IReadOnlyList<T> vectorLeg, int k, int limit) where T : notnull
    {
        var storage = session.StorageFor<T>();
        var fused = new Dictionary<object, Candidate<T>>();

        void Accumulate(IReadOnlyList<T> leg)
        {
            for (var i = 0; i < leg.Count; i++)
            {
                var document = leg[i];
                var id = storage.IdentityFor(document);
                var rank = i + 1;

                if (fused.TryGetValue(id, out var existing))
                {
                    // The instance kept is the first leg's, deliberately: both legs materialise
                    // through the same selector over the same row, so they are equal documents, and
                    // keeping one makes reference identity within a result stable.
                    existing.Score += 1.0 / (k + rank);
                    existing.BestRank = Math.Min(existing.BestRank, rank);
                }
                else
                {
                    fused[id] = new Candidate<T>(document, 1.0 / (k + rank), rank, id);
                }
            }
        }

        Accumulate(textLeg);
        Accumulate(vectorLeg);

        return fused.Values
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.BestRank)
            .ThenBy(x => x.Id.ToString(), StringComparer.Ordinal)
            .Take(limit)
            .Select(x => new HybridMatch<T>(x.Document, x.Score))
            .ToList();
    }

    private sealed class Candidate<T>(T document, double score, int bestRank, object id)
    {
        public T Document { get; } = document;
        public double Score { get; set; } = score;
        public int BestRank { get; set; } = bestRank;
        public object Id { get; } = id;
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
