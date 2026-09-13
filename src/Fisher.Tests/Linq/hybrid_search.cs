using JasperFx;
using Shouldly;

namespace Fisher.Tests.Linq;

/// <summary>
///     fisher#262 — <c>HybridSearchAsync</c>, reciprocal rank fusion over the full-text and vector
///     legs.
/// </summary>
/// <remarks>
///     <para>
///         <b>The facts worth having are the ones a single-leg search would also pass.</b> Almost any
///         assertion about a hybrid search over a corpus where both legs agree is satisfied by either
///         leg alone, so the corpus here is built so the legs <em>disagree</em>: a document only the
///         keyword leg can find (an exact identifier no embedding places), one only the vector leg can
///         find (a paraphrase sharing no tokens), and one both rank middling that the fusion should
///         lift above them.
///     </para>
///     <para>
///         That last one is the whole argument for RRF over a weighted score sum, and it is the fact
///         to read first if this ever goes red.
///     </para>
/// </remarks>
public class hybrid_search : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("hybrid");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            options.Schema.For<Note>()
                .FullTextIndex(x => x.Text)
                .VectorIndex(x => x.Embedding, dimensions: 3);

            // One leg each, for the refusals.
            options.Schema.For<TextOnly>().FullTextIndex(x => x.Text);
            options.Schema.For<VectorOnly>().VectorIndex(x => x.Embedding, dimensions: 3);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        session.Store(
            // Keyword-only: the exact token is in the text, and its embedding points away from the
            // query. This is the recall hole vector search has.
            new Note { Id = "invoice", Text = "invoice XJ-4417 reconciled", Embedding = [0, 0, 1] },

            // Vector-only: no shared token with the query text at all, embedding points at it. The
            // recall hole keyword search has.
            new Note { Id = "paraphrase", Text = "a bill was settled", Embedding = [1, 0, 0] },

            // Middling in both, and top in neither. The fusion should lift it.
            new Note { Id = "both", Text = "invoice handling", Embedding = [0.8f, 0.2f, 0] },

            new Note { Id = "unrelated-a", Text = "weather report", Embedding = [0, 1, 0] },
            new Note { Id = "unrelated-b", Text = "shipping manifest", Embedding = [0, 0.9f, 0] });

        session.Store(new TextOnly { Id = "t", Text = "searchable" });
        session.Store(new VectorOnly { Id = "v", Embedding = [1, 0, 0] });

        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        _store.Dispose();
        await _database.DisposeAsync();
    }

    /// <summary>
    ///     <b>The union, not the intersection.</b> A document only one leg can find still comes back.
    /// </summary>
    /// <remarks>
    ///     Both halves are asserted because they fail for opposite reasons: <c>invoice</c> is absent
    ///     if the fusion intersects, and <c>paraphrase</c> is absent if the text leg is the only one
    ///     being read at all.
    /// </remarks>
    [Fact]
    public async Task a_document_only_one_leg_finds_is_still_returned()
    {
        await using var session = _store.QuerySession();

        var hits = await session.HybridSearchAsync<Note>(
            x => x.Embedding, "invoice", new float[] { 1, 0, 0 }, limit: 5, token: Token);

        var ids = hits.Select(x => x.Id).ToArray();

        ids.ShouldContain("invoice");
        ids.ShouldContain("paraphrase");
    }

    /// <summary>
    ///     <b>The fact that makes fusion worth doing.</b> A document ranked middling by both legs
    ///     outranks one that is first in a single leg and absent from the other.
    /// </summary>
    /// <remarks>
    ///     This is what RRF buys and what neither leg can produce alone, so it is the discriminating
    ///     assertion in the file: a "hybrid" search that simply concatenated the legs, or returned
    ///     one of them, satisfies everything else here and fails this.
    /// </remarks>
    [Fact]
    public async Task agreement_between_the_legs_outranks_a_single_leg_winner()
    {
        await using var session = _store.QuerySession();

        var hits = await session.HybridSearchWithScoresAsync<Note>(
            x => x.Embedding, "invoice", new float[] { 1, 0, 0 }, limit: 5, token: Token);

        var ranked = hits.Select(x => x.Document.Id).ToList();

        ranked.ShouldContain("both");
        ranked.IndexOf("both").ShouldBeLessThan(ranked.IndexOf("unrelated-a"));

        // Scores rise with agreement, so the document both legs found scores above one only a single
        // leg did. Larger is better here, unlike a distance.
        var both = hits.Single(x => x.Document.Id == "both").Score;
        var single = hits.Single(x => x.Document.Id == "invoice").Score;
        both.ShouldBeGreaterThan(single);
    }

    /// <summary>
    ///     The candidate depth is read deeper than <c>limit</c>, or the fusion never sees the document
    ///     it exists to surface.
    /// </summary>
    /// <remarks>
    ///     Asserted as a refusal rather than by counting rows: a depth below <c>limit</c> is a
    ///     configuration that cannot do the job, and answering it with a short list would look like a
    ///     small corpus.
    /// </remarks>
    [Fact]
    public async Task a_candidate_depth_below_the_limit_is_refused()
    {
        await using var session = _store.QuerySession();

        var thrown = await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            session.HybridSearchAsync<Note>(x => x.Embedding, "invoice", new float[] { 1, 0, 0 },
                limit: 10, options: new HybridSearchOptions(CandidateDepth: 3), token: Token));

        thrown.Message.ShouldContain("CandidateDepth");
    }

    /// <summary>
    ///     ⚠️ <b>A type with only one leg is refused, not degraded.</b>
    /// </summary>
    /// <remarks>
    ///     Both directions, because the message differs and because a guard written for one would
    ///     leave the other falling through to whichever leg ran first — where the failure is about
    ///     that leg rather than about hybrid search, which is the thing a caller cannot act on.
    /// </remarks>
    [Fact]
    public async Task a_type_with_only_one_leg_is_refused_by_name()
    {
        await using var session = _store.QuerySession();

        (await Should.ThrowAsync<InvalidOperationException>(() =>
                session.HybridSearchAsync<TextOnly>(x => x.Id, "searchable", new float[] { 1, 0, 0 },
                    token: Token)))
            .Message.ShouldContain("no vector index");

        (await Should.ThrowAsync<InvalidOperationException>(() =>
                session.HybridSearchAsync<VectorOnly>(x => x.Embedding, "searchable",
                    new float[] { 1, 0, 0 }, token: Token)))
            .Message.ShouldContain("no full-text index");
    }

    /// <summary>
    ///     The refusals the vector leg already makes carry over, rather than being restated.
    /// </summary>
    [Fact]
    public async Task the_vector_legs_own_refusals_still_apply()
    {
        await using var session = _store.QuerySession();

        await Should.ThrowAsync<ArgumentException>(() =>
            session.HybridSearchAsync<Note>(x => x.Embedding, "invoice",
                new float[] { 1, 0 }, token: Token));
    }

    /// <summary>
    ///     <c>limit</c> bounds the result, and the fused order is stable across calls.
    /// </summary>
    /// <remarks>
    ///     Stability matters more than it looks: documents found at the same rank by one leg and by
    ///     neither in the other have identical scores, which is common rather than exotic, and without
    ///     a total order the page a caller gets differs between runs. The tiebreak is best rank, then
    ///     identity.
    /// </remarks>
    [Fact]
    public async Task the_result_is_bounded_and_the_order_is_stable()
    {
        await using var session = _store.QuerySession();

        var first = await session.HybridSearchAsync<Note>(
            x => x.Embedding, "invoice", new float[] { 1, 0, 0 }, limit: 3, token: Token);
        var second = await session.HybridSearchAsync<Note>(
            x => x.Embedding, "invoice", new float[] { 1, 0, 0 }, limit: 3, token: Token);

        first.Count.ShouldBe(3);
        first.Select(x => x.Id).ShouldBe(second.Select(x => x.Id));
    }

    /// <summary>
    ///     Soft-deleted documents are excluded, because both legs run through paths that filter them.
    /// </summary>
    /// <remarks>
    ///     The dividend of fusing two existing readers rather than writing a third statement: the
    ///     implicit filters apply without this method restating any of them, which is what fisher#51
    ///     established is the only safe arrangement.
    /// </remarks>
    [Fact]
    public async Task the_implicit_filters_apply_to_both_legs()
    {
        await using (var writing = _store.LightweightSession())
        {
            writing.Delete<Note>("invoice");
            writing.Delete<Note>("paraphrase");
            await writing.SaveChangesAsync(Token);
        }

        await using var session = _store.QuerySession();

        var ids = (await session.HybridSearchAsync<Note>(
            x => x.Embedding, "invoice", new float[] { 1, 0, 0 }, limit: 5, token: Token))
            .Select(x => x.Id).ToArray();

        ids.ShouldNotContain("invoice");
        ids.ShouldNotContain("paraphrase");
    }

    public class Note
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public float[]? Embedding { get; set; }
    }

    public class TextOnly
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
    }

    public class VectorOnly
    {
        public string Id { get; set; } = "";
        public float[]? Embedding { get; set; }
    }
}
