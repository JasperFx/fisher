using Fisher.Storage.Vectors;
using JasperFx.Events.Vectors;

namespace Fisher.Tests.Documentation;

/*
 * The compiled source behind docs/documents/querying/vector-search.md.
 *
 * See "Documentation samples come from compiled code" in CLAUDE.md.
 */

public class Memory
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public float[]? Embedding { get; set; }
}

public class Snippet
{
    public Guid Id { get; set; }

    #region sample_vector_attribute
    [VectorIndex(768, Distance = DistanceFunction.Cosine)]
    public float[]? Embedding { get; set; }
    #endregion
}

public static class vector_samples
{
    public static void declare_index(StoreOptions opts)
    {
        #region sample_vector_declare_index
        opts.Schema.For<Memory>().VectorIndex(x => x.Embedding, dimensions: 768);
        #endregion
    }

    public static void declare_index_with_metric(StoreOptions opts)
    {
        #region sample_vector_declare_index_l2
        opts.Schema.For<Memory>().VectorIndex(x => x.Embedding, dimensions: 768, DistanceFunction.L2);
        #endregion
    }

    public static async Task search(IQuerySession session, IEmbeddingProvider embeddings)
    {
        #region sample_vector_search
        // The query vector comes from whatever model produced the stored ones — the
        // store-neutral IEmbeddingProvider from JasperFx.Events, here.
        var query = await embeddings.GenerateEmbeddingAsync("how does the daemon pick a mode?");

        var nearest = await session.VectorSearchAsync<Memory>(x => x.Embedding, query, limit: 5);
        #endregion

        _ = nearest;
    }

    public static async Task search_with_scores(IQuerySession session, ReadOnlyMemory<float> query)
    {
        #region sample_vector_search_with_scores
        var matches = await session.VectorSearchWithScoresAsync<Memory>(x => x.Embedding, query, limit: 5);

        // Distance is smaller-is-closer under every metric; for cosine it is 1 - similarity.
        var confident = matches.Where(m => m.Distance < 0.3).Select(m => m.Document);
        #endregion

        _ = confident;
    }

    public static async Task search_with_another_metric(IQuerySession session, ReadOnlyMemory<float> query)
    {
        #region sample_vector_search_metric
        var byMagnitude = await session.VectorSearchAsync<Memory>(
            x => x.Embedding, query, limit: 5, distance: DistanceFunction.L2);
        #endregion

        _ = byMagnitude;
    }
}
