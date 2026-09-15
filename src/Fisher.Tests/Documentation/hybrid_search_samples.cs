using JasperFx.Events.Vectors;

namespace Fisher.Tests.Documentation;

/*
 * The compiled source behind docs/documents/querying/hybrid-search.md.
 *
 * See "Documentation samples come from compiled code" in CLAUDE.md.
 */

public class SupportTicket
{
    public string Id { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string Status { get; set; } = "";
    public float[]? Embedding { get; set; }
}

public static class hybrid_search_samples
{
    public static void declare_both_indexes(StoreOptions opts)
    {
        #region sample_hybrid_declare_both_indexes
        opts.Schema.For<SupportTicket>()
            .FullTextIndex(x => x.Subject, x => x.Body)
            .VectorIndex(x => x.Embedding, dimensions: 768);
        #endregion
    }

    public static async Task search(IQuerySession session, IEmbeddingProvider embeddings)
    {
        #region sample_hybrid_search
        // One string feeds both legs: the full-text leg searches for it, the vector leg for its
        // embedding -- from the same model that produced the stored vectors.
        var text = "invoice XJ-4417";
        var query = await embeddings.GenerateEmbeddingAsync(text);

        var hits = await session.HybridSearchAsync<SupportTicket>(
            x => x.Embedding,   // the declared vector member
            text,               // the full-text leg
            query,              // the vector leg
            limit: 10);
        #endregion

        _ = hits;
    }

    public static async Task search_with_scores(IQuerySession session, string text, ReadOnlyMemory<float> query)
    {
        #region sample_hybrid_search_with_scores
        var matches = await session.HybridSearchWithScoresAsync<SupportTicket>(
            x => x.Embedding, text, query, limit: 10);

        // Larger is better -- the opposite of a distance. With the default K of 60, first place in
        // one leg is worth 1/61 ≈ 0.016, so a floor of 0.03 keeps what both legs ranked near the top.
        var agreed = matches.Where(m => m.Score >= 0.03).Select(m => m.Document);
        #endregion

        _ = agreed;
    }

    public static async Task search_with_a_filter(IQuerySession session, string text, ReadOnlyMemory<float> query)
    {
        #region sample_hybrid_search_filter
        // Applied to BOTH legs, before each leg's candidate depth -- otherwise closed tickets
        // consume the depth and the fused order ranks a set that includes them.
        var open = await session.HybridSearchAsync<SupportTicket>(
            x => x.Embedding, text, query, limit: 10,
            filter: t => t.Status == "open");
        #endregion

        _ = open;
    }

    public static async Task search_with_options(IQuerySession session, string searchBox, ReadOnlyMemory<float> query)
    {
        #region sample_hybrid_search_options
        var hits = await session.HybridSearchAsync<SupportTicket>(
            x => x.Embedding, searchBox, query, limit: 20,
            options: new HybridSearchOptions(
                K: 30,                                  // let the top of each leg count for more
                CandidateDepth: 200,                    // deeper than the default max(20 × 4, 50)
                Distance: DistanceFunction.L2,          // the vector leg's metric, for this call
                TextStyle: HybridTextStyle.WebStyle));  // "quoted phrases", or, -exclusions
        #endregion

        _ = hits;
    }

    public static async Task search_with_column_weights(IQuerySession session, string text,
        ReadOnlyMemory<float> query)
    {
        #region sample_hybrid_search_column_weights
        // FullTextIndex(x => x.Subject, x => x.Body) — one weight per indexed member, in the order
        // the index declared them. A hit in the subject now outweighs one buried in a long body.
        var hits = await session.HybridSearchAsync<SupportTicket>(
            x => x.Embedding, text, query, limit: 20,
            options: new HybridSearchOptions(ColumnWeights: [3.0, 1.0]));
        #endregion

        _ = hits;
    }
}
