using Fisher.Linq;
using JasperFx;
using JasperFx.Events.Vectors;
using Shouldly;

namespace Fisher.Tests.Linq;

/// <summary>
///     fisher#285 and fisher#291 — the implicit filters a vector or hybrid search carries, and the
///     <c>filter</c> predicate a caller can add on top of them.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every tenancy fact is asserted in both directions</b>, which is the discipline
///         <c>tenanted_queries</c> records for fisher#51 and the reason a one-sided assertion would
///         not have caught this one either: a search that ignores the tenant predicate looks correct
///         to the tenant owning most of the corpus and returns somebody else's rows to everyone else.
///     </para>
///     <para>
///         The filter facts are deliberately built so that the filtered top-k and the filtered remains
///         of the top-k are <em>different answers</em> — a corpus where the wanted documents are also
///         the nearest ones is satisfied by either behaviour.
///     </para>
/// </remarks>
public class vector_search_filters : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("vector-filters");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            options.Schema.For<Passage>()
                .MultiTenanted()
                .FullTextIndex(x => x.Text)
                .VectorIndex(x => x.Embedding, dimensions: 3);

            options.Schema.For<Article>()
                .AddSubClass<Bulletin>()
                .AddSubClass<Memo>()
                .VectorIndex(x => x.Embedding, dimensions: 3);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var north = _store.LightweightSession("north"))
        {
            north.Store(
                new Passage { Id = "north-east", Text = "points east from the north office", Embedding = [1, 0, 0] },
                new Passage { Id = "north-up", Text = "points up from the north office", Embedding = [0, 0, 1] });
            await north.SaveChangesAsync(Token);
        }

        await using (var south = _store.LightweightSession("south"))
        {
            // Nearer to [1,0,0] than anything north holds, so a search that ignores tenancy ranks it
            // first rather than merely including it.
            south.Store(new Passage { Id = "south-east", Text = "points east from the south office", Embedding = [1, 0, 0] });
            await south.SaveChangesAsync(Token);
        }

        await using (var session = _store.LightweightSession())
        {
            session.Store<Article>(
                new Bulletin { Id = "bulletin-near", Embedding = [1, 0, 0] },
                new Memo { Id = "memo-near", Embedding = [1, 0, 0] },
                new Memo { Id = "memo-far", Embedding = [0, 1, 0] });
            await session.SaveChangesAsync(Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    // ---- fisher#285: the implicit filters ----

    [Theory]
    [InlineData("north", "north-east,north-up")]
    [InlineData("south", "south-east")]
    public async Task a_vector_search_is_scoped_to_the_sessions_tenant(string tenant, string expected)
    {
        await using var session = _store.QuerySession(tenant);

        var hits = await session.VectorSearchAsync<Passage>(
            x => x.Embedding, new float[] { 1, 0, 0 }, limit: 10, token: Token);

        hits.Select(x => x.Id).ShouldBe(expected.Split(','), ignoreOrder: true);

        // And the nearest hit is the tenant's own, not the other tenant's identical vector: a leak
        // here ranks first rather than merely appearing.
        hits[0].Text.ShouldContain(tenant);
    }

    /// <summary>The scored overload is a second statement builder and had the same hole.</summary>
    [Theory]
    [InlineData("north", 2)]
    [InlineData("south", 1)]
    public async Task the_scored_overload_is_scoped_too(string tenant, int expected)
    {
        await using var session = _store.QuerySession(tenant);

        var hits = await session.VectorSearchWithScoresAsync<Passage>(
            x => x.Embedding, new float[] { 1, 0, 0 }, limit: 10, token: Token);

        hits.Count.ShouldBe(expected);
        hits.ShouldAllBe(x => x.Document.Text.Contains(tenant));
    }

    /// <summary>
    ///     The hybrid fuse is over the UNION of the legs, so one unscoped leg is enough to leak.
    /// </summary>
    /// <remarks>
    ///     The text leg went through <c>Query&lt;T&gt;()</c> and was always scoped; the vector leg was
    ///     not. So the other tenant's document arrived through the fusion ranked lower rather than
    ///     first, which is the shape least likely to be noticed.
    /// </remarks>
    [Theory]
    [InlineData("north")]
    [InlineData("south")]
    public async Task a_hybrid_search_leaks_through_neither_leg(string tenant)
    {
        await using var session = _store.QuerySession(tenant);

        var hits = await session.HybridSearchAsync<Passage>(
            x => x.Embedding, "points east office", new float[] { 1, 0, 0 }, limit: 10, token: Token);

        hits.ShouldAllBe(x => x.Text.Contains(tenant));
        hits.ShouldNotBeEmpty();
    }

    /// <summary>
    ///     A search over a sub-class reads its own rows, not its siblings'.
    /// </summary>
    /// <remarks>
    ///     Same root cause as the tenancy leak and less severe only because the rows come back at all:
    ///     the <c>doc_type</c> discriminator was never composed, so a sub-class search read the whole
    ///     hierarchy's table.
    /// </remarks>
    [Fact]
    public async Task a_search_over_a_sub_class_does_not_return_its_siblings()
    {
        await using var session = _store.QuerySession();

        var memos = await session.VectorSearchAsync<Memo>(
            x => x.Embedding, new float[] { 1, 0, 0 }, limit: 10, token: Token);

        memos.Select(x => x.Id).ShouldBe(["memo-near", "memo-far"], ignoreOrder: true);

        var bulletins = await session.VectorSearchAsync<Bulletin>(
            x => x.Embedding, new float[] { 1, 0, 0 }, limit: 10, token: Token);

        bulletins.ShouldHaveSingleItem().Id.ShouldBe("bulletin-near");

        // The base still reads the whole hierarchy, each row as its own type.
        var all = await session.VectorSearchAsync<Article>(
            x => x.Embedding, new float[] { 1, 0, 0 }, limit: 10, token: Token);

        all.Count.ShouldBe(3);
        all.OfType<Bulletin>().Count().ShouldBe(1);
        all.OfType<Memo>().Count().ShouldBe(2);
    }

    // ---- fisher#291 / jasperfx#843: the filter predicate ----

    /// <summary>
    ///     <b>The filter runs before the limit, so the result is the top-k of the filtered set.</b>
    /// </summary>
    /// <remarks>
    ///     The corpus is arranged so the two readings differ: the nearest document to <c>[1,0,0]</c>
    ///     in the north tenant is <c>north-east</c>, which the filter excludes — so a filter applied
    ///     to an already-limited top-1 would return nothing, where the true filtered top-1 is
    ///     <c>north-up</c>. Fisher scans every row, so there is no recall caveat to soften this with.
    /// </remarks>
    [Fact]
    public async Task the_filter_is_applied_before_the_limit()
    {
        await using var session = _store.QuerySession("north");

        var hits = await session.VectorSearchAsync<Passage>(
            x => x.Embedding, new float[] { 1, 0, 0 }, limit: 1,
            filter: x => x.Id != "north-east", token: Token);

        hits.ShouldHaveSingleItem().Id.ShouldBe("north-up");
    }

    /// <summary>The filter is in addition to the store's own predicates, never instead of them.</summary>
    [Fact]
    public async Task the_filter_does_not_replace_the_tenant_predicate()
    {
        await using var session = _store.QuerySession("north");

        var hits = await session.VectorSearchAsync<Passage>(
            x => x.Embedding, new float[] { 1, 0, 0 }, limit: 10,
            filter: x => x.Text.Contains("east"), token: Token);

        hits.ShouldHaveSingleItem().Id.ShouldBe("north-east");
    }

    /// <summary>
    ///     A hybrid filter reaches BOTH legs, which is what keeps the fused order a ranking of the
    ///     filtered set rather than of a set that includes rows the caller will discard.
    /// </summary>
    [Fact]
    public async Task a_hybrid_filter_reaches_both_legs()
    {
        await using var session = _store.QuerySession("north");

        var hits = await session.HybridSearchWithScoresAsync<Passage>(
            x => x.Embedding, "points office", new float[] { 1, 0, 0 }, limit: 10,
            filter: x => x.Id != "north-east", token: Token);

        // north-east is both the nearest vector and a full-text match, so it survives unless the
        // filter reached the leg that found it.
        hits.Select(x => x.Document.Id).ShouldBe(["north-up"]);
    }

    /// <summary>A filter the LINQ provider cannot translate is refused the same way it would be there.</summary>
    [Fact]
    public async Task an_untranslatable_filter_is_refused_as_a_linq_expression_would_be()
    {
        await using var session = _store.QuerySession("north");

        await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.VectorSearchAsync<Passage>(x => x.Embedding, new float[] { 1, 0, 0 },
                filter: x => x.Text.GetHashCode() == 3, token: Token));
    }

    public class Passage
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public float[]? Embedding { get; set; }
    }

    public class Article
    {
        public string Id { get; set; } = "";
        public float[]? Embedding { get; set; }
    }

    public class Bulletin : Article;

    public class Memo : Article;
}
