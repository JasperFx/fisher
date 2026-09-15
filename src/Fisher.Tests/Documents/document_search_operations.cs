using JasperFx;
using JasperFx.Events.Documents;
using JasperFx.Events.Vectors;
using Shouldly;

namespace Fisher.Tests.Documents;

/// <summary>
///     jasperfx#842 / fisher#291 — <see cref="IDocumentReadOperations.Search" />, the store-agnostic
///     route to vector and hybrid search.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every fact here holds the session as the CONTRACT, which is the only caller that can
///         tell whether the member is implemented.</b> <c>Search</c> ships with a throwing default, so
///         a store that never implements it compiles, passes every test written against its own types,
///         and fails only for the consumer the accessor exists for — the non-covariance trap the two
///         <c>Events</c> implementations on <c>FisherSession</c> already record.
///     </para>
/// </remarks>
public class document_search_operations : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("document-search");
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
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var north = _store.LightweightSession("north"))
        {
            north.Store(
                new Passage { Id = "north-east", Text = "points east", Embedding = [1, 0, 0] },
                new Passage { Id = "north-up", Text = "points up", Embedding = [0, 0, 1] });
            await north.SaveChangesAsync(Token);
        }

        await using (var south = _store.LightweightSession("south"))
        {
            south.Store(new Passage { Id = "south-east", Text = "points east", Embedding = [1, 0, 0] });
            await south.SaveChangesAsync(Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    /// <summary>
    ///     The accessor answers rather than throwing, which is the whole of what implementing it means.
    /// </summary>
    [Fact]
    public async Task the_contract_reaches_vector_search()
    {
        await using IDocumentReadOperations session = _store.QuerySession("north");

        var hits = await session.Search.VectorSearchWithScoresAsync<Passage>(
            x => x.Embedding, new float[] { 1, 0, 0 }, limit: 1, token: Token);

        hits.ShouldHaveSingleItem().Document.Id.ShouldBe("north-east");
    }

    /// <summary>
    ///     The document-only forms are shared extensions over the two scored methods, so implementing
    ///     the contract is two methods and the projection to documents cannot differ between stores.
    /// </summary>
    [Fact]
    public async Task the_shared_extensions_come_for_free()
    {
        await using IDocumentReadOperations session = _store.QuerySession("north");

        var vector = await session.Search.VectorSearchAsync<Passage>(
            x => x.Embedding, new float[] { 1, 0, 0 }, limit: 2, token: Token);

        vector.Select(x => x.Id).ShouldBe(["north-east", "north-up"]);

        var hybrid = await session.Search.HybridSearchAsync<Passage>(
            x => x.Embedding, "points east", new float[] { 1, 0, 0 }, limit: 5, token: Token);

        hybrid.ShouldNotBeEmpty();
        hybrid.ShouldAllBe(x => x.Id.StartsWith("north"));
    }

    /// <summary>
    ///     ⚠️ <b>The store's implicit predicates apply through the contract too.</b>
    /// </summary>
    /// <remarks>
    ///     This is the reason the contract forwards to Fisher's own extension methods rather than
    ///     building a second statement: a consumer reaching search through the store-agnostic surface
    ///     is the one least able to notice a missing tenant term. fisher#285 is exactly that leak, and
    ///     a second implementation is a second place for it to come back.
    /// </remarks>
    [Theory]
    [InlineData("north")]
    [InlineData("south")]
    public async Task the_contract_carries_the_stores_own_predicates(string tenant)
    {
        await using IDocumentReadOperations session = _store.QuerySession(tenant);

        var hits = await session.Search.VectorSearchAsync<Passage>(
            x => x.Embedding, new float[] { 1, 0, 0 }, limit: 10, token: Token);

        hits.ShouldAllBe(x => x.Id.StartsWith(tenant));
    }

    /// <summary>The filter reaches through the contract, applied before the limit.</summary>
    [Fact]
    public async Task the_contract_carries_the_filter()
    {
        await using IDocumentReadOperations session = _store.QuerySession("north");

        var hits = await session.Search.VectorSearchAsync<Passage>(
            x => x.Embedding, new float[] { 1, 0, 0 }, limit: 1,
            filter: x => x.Id != "north-east", token: Token);

        hits.ShouldHaveSingleItem().Id.ShouldBe("north-up");
    }

    /// <summary>
    ///     ⚠️ <b>The contract's members did not shadow Fisher's extension methods</b>, which is why it
    ///     lives behind an accessor.
    /// </summary>
    /// <remarks>
    ///     Had <c>VectorSearchWithScoresAsync</c> been put on <c>IQuerySession</c> itself, the instance
    ///     member would win overload resolution over the extension at every existing call site — the
    ///     same names, no compile error, and the store's own predicates decided by whichever body won.
    ///     Asserting it compiles and answers here is what makes the accessor a decision rather than a
    ///     stylistic preference.
    /// </remarks>
    [Fact]
    public async Task the_native_spelling_still_binds_to_fishers_extension()
    {
        await using var session = _store.QuerySession("north");

        var hits = await session.VectorSearchWithScoresAsync<Passage>(
            x => x.Embedding, new float[] { 1, 0, 0 }, limit: 1, token: Token);

        hits.ShouldHaveSingleItem().Document.Id.ShouldBe("north-east");
    }

    public class Passage
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public float[]? Embedding { get; set; }
    }
}
