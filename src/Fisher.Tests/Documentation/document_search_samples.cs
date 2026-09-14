using JasperFx.Events.Documents;
using JasperFx.Events.Vectors;

namespace Fisher.Tests.Documentation;

/*
 * The compiled source behind docs/documents/querying/store-agnostic-search.md.
 *
 * See "Documentation samples come from compiled code" in CLAUDE.md.
 */

#region sample_store_agnostic_search_service
// No Fisher types anywhere. The same class compiles against Marten and Polecat.
public class Retriever
{
    private readonly IDocumentReadOperations _session;
    private readonly IEmbeddingProvider _embeddings;

    public Retriever(IDocumentReadOperations session, IEmbeddingProvider embeddings)
    {
        _session = session;
        _embeddings = embeddings;
    }

    public async Task<IReadOnlyList<Memory>> RecallAsync(string question, CancellationToken token)
    {
        var query = await _embeddings.GenerateEmbeddingAsync(question, token);

        return await _session.Search.VectorSearchAsync<Memory>(
            x => x.Embedding, query, limit: 5, token: token);
    }
}
#endregion

public static class document_search_samples
{
    public static async Task scored(IDocumentReadOperations session, ReadOnlyMemory<float> query)
    {
        #region sample_store_agnostic_search_scored
        var matches = await session.Search.VectorSearchWithScoresAsync<Memory>(
            x => x.Embedding, query, limit: 5);

        var confident = matches.Where(m => m.Distance < 0.3).Select(m => m.Document);
        #endregion

        _ = confident;
    }

    public static async Task hybrid(IDocumentReadOperations session, string text, ReadOnlyMemory<float> query)
    {
        #region sample_store_agnostic_search_hybrid
        var hits = await session.Search.HybridSearchAsync<Memory>(
            x => x.Embedding, text, query, limit: 10,
            options: new HybridSearchOptions(K: 30),
            filter: x => !x.Archived);
        #endregion

        _ = hits;
    }
}
