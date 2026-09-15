using JasperFx;
using JasperFx.Events.Vectors;
using Shouldly;

namespace Fisher.Tests.Linq;

/// <summary>
///     fisher#289 — per-column weights for the hybrid search's text leg.
/// </summary>
/// <remarks>
///     <para>
///         <b>The weights change WHICH documents survive, not just their order.</b> RRF fuses ranks,
///         so the text leg's ordering decides who makes the <c>CandidateDepth</c> cut and how much
///         each survivor contributes to the fused score. That is why this cannot be left to a caller
///         re-sorting the results: by the time they see them, the documents that lost are gone.
///     </para>
///     <para>
///         The corpus is built so the two weightings <em>disagree</em>. <c>body-heavy</c> repeats the
///         term five times in a long body and wins on equal weights; <c>title-hit</c> has it twice in
///         the title and wins once the title is weighted. A corpus where both weightings agree would
///         pass against the unweighted code, which is the whole failure mode here.
///     </para>
/// </remarks>
public class hybrid_search_column_weights : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("hybrid-weights");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            options.Schema.For<Article>()
                .FullTextIndex(x => x.Title, x => x.Body)
                .VectorIndex(x => x.Embedding, dimensions: 3);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        session.Store(
            // Wins on equal weights: five occurrences in the body. Also the vector leg's nearest, so
            // a test that flips it has to overcome BOTH legs rather than just reordering the text.
            new Article
            {
                Id = "body-heavy",
                Title = "weather",
                Body = "retries retries retries retries retries",
                Embedding = [1, 0, 0]
            },

            // Wins once the title is weighted.
            new Article
            {
                Id = "title-hit", Title = "retries retries", Body = "weather", Embedding = [0.95f, 0.1f, 0]
            },

            new Article { Id = "t1", Title = "retries", Body = "weather", Embedding = [0, 1, 0] },
            new Article { Id = "t2", Title = "retries", Body = "weather weather", Embedding = [0, 0, 1] });

        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        _store.Dispose();
        await _database.DisposeAsync();
    }

    private async Task<string[]> Search(HybridSearchOptions? options)
    {
        await using var session = _store.QuerySession();

        var hits = await session.HybridSearchAsync<Article>(
            x => x.Embedding, "retries", new float[] { 1, 0, 0 }, limit: 4, options, token: Token);

        return hits.Select(x => x.Id).ToArray();
    }

    /// <summary>
    ///     ⚠️ <b>The fact that fails against the unweighted code.</b> Everything else here would pass
    ///     with the weights silently dropped.
    /// </summary>
    [Fact]
    public async Task weighting_the_title_lifts_a_title_match_over_a_body_heavy_one()
    {
        var equal = await Search(null);
        var weighted = await Search(new HybridSearchOptions(ColumnWeights: [10.0, 0.1]));

        // Equal weights: five occurrences in the body win, and the vector leg agrees.
        equal.First().ShouldBe("body-heavy");

        // Title weighted ten to one: the title match overtakes it despite losing the vector leg.
        weighted.First().ShouldBe("title-hit");
    }

    [Fact]
    public async Task the_weights_reach_the_ranking_rather_than_being_accepted_and_dropped()
    {
        // A weaker fact than the one above, kept because it survives a change to the corpus: an
        // ignored weights array gives byte-identical results, which is exactly the defect.
        var equal = await Search(null);
        var weighted = await Search(new HybridSearchOptions(ColumnWeights: [10.0, 0.1]));

        weighted.ShouldNotBe(equal);
    }

    [Fact]
    public async Task the_default_is_still_every_column_weighed_the_same()
    {
        var implicitly_equal = await Search(null);
        var explicitly_equal = await Search(new HybridSearchOptions(ColumnWeights: [1.0, 1.0]));

        explicitly_equal.ShouldBe(implicitly_equal);
    }

    /// <summary>
    ///     Refused up front, so the message is about <c>ColumnWeights</c> rather than about
    ///     <c>OrderByRelevance</c> — a method the caller never called.
    /// </summary>
    [Fact]
    public async Task a_weights_array_that_does_not_match_the_index_is_refused_by_name()
    {
        var tooFew = await Should.ThrowAsync<ArgumentException>(async () =>
            await Search(new HybridSearchOptions(ColumnWeights: [3.0])));

        tooFew.Message.ShouldContain("ColumnWeights");
        tooFew.Message.ShouldContain("1 weights");
        tooFew.Message.ShouldContain("2 column(s)");
        tooFew.Message.ShouldNotContain("OrderByRelevance");

        await Should.ThrowAsync<ArgumentException>(async () =>
            await Search(new HybridSearchOptions(ColumnWeights: [1.0, 2.0, 3.0])));
    }

    [Fact]
    public async Task an_empty_or_non_finite_weights_array_is_refused()
    {
        await Should.ThrowAsync<ArgumentException>(async () =>
            await Search(new HybridSearchOptions(ColumnWeights: [])));

        await Should.ThrowAsync<ArgumentException>(async () =>
            await Search(new HybridSearchOptions(ColumnWeights: [1.0, double.NaN])));
    }

    public class Article
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public float[]? Embedding { get; set; }
    }
}
