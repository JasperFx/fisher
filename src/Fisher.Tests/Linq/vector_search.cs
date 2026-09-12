using System.Text.Json;
using Fisher.Storage.Vectors;
using JasperFx;
using JasperFx.Events.Vectors;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Fisher.Tests.Linq;

/// <summary>
///     Vector search over a declared embedding member — fisher#241.
/// </summary>
/// <remarks>
///     <para>
///         The search reads the embedding straight out of <c>data</c>, so the tests that matter are
///         the ones proving it cannot drift: a document updated past Fisher with raw SQL ranks by its
///         NEW vector, and a soft-deleted one is not returned. Every refusal is pinned by name, because
///         each replaces a search that would otherwise have returned nothing.
///     </para>
/// </remarks>
public class vector_search : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("vectors");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Passage>().SoftDeleted()
                .VectorIndex(x => x.Embedding, dimensions: 3);
            options.Schema.For<Tagged>();
            options.Schema.For<Unindexed>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        session.Store(
            new Passage { Id = "east", Text = "points east", Embedding = [1, 0, 0] },
            new Passage { Id = "north", Text = "points north", Embedding = [0, 1, 0] },
            new Passage { Id = "north-east", Text = "between", Embedding = [0.7f, 0.7f, 0] },
            new Passage { Id = "far-east", Text = "same direction, further", Embedding = [5, 0, 0] },
            new Passage { Id = "blank", Text = "no embedding", Embedding = null });
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        _store.Dispose();
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task nearest_first_by_cosine_and_the_limit_holds()
    {
        await using var session = _store.QuerySession();

        var nearest = await session.VectorSearchAsync<Passage>(x => x.Embedding, new float[] { 1, 0, 0 }, limit: 3, token: Token);

        // Cosine ignores magnitude: east and far-east tie at 0, then north-east, then north.
        nearest.Select(x => x.Id).Take(2).ShouldBe(["east", "far-east"], ignoreOrder: true);
        nearest[2].Id.ShouldBe("north-east");
        nearest.Count.ShouldBe(3);
    }

    [Fact]
    public async Task scores_are_distances_smaller_is_closer_under_every_metric()
    {
        await using var session = _store.QuerySession();
        var query = new float[] { 1, 0, 0 };

        var cosine = await session.VectorSearchWithScoresAsync<Passage>(x => x.Embedding, query, limit: 4, token: Token);
        cosine.Select(m => m.Distance).ShouldBeInOrder();
        cosine[0].Distance.ShouldBe(0, 1e-6);
        cosine.Single(m => m.Document.Id == "north").Distance.ShouldBe(1, 1e-6);

        // L2 does not ignore magnitude: far-east is now far.
        var l2 = await session.VectorSearchWithScoresAsync<Passage>(x => x.Embedding, query, limit: 4, DistanceFunction.L2, Token);
        l2[0].Document.Id.ShouldBe("east");
        l2.Single(m => m.Document.Id == "far-east").Distance.ShouldBe(4, 1e-6);

        // Inner product is negated so it is still a distance: far-east is the best match.
        var inner = await session.VectorSearchWithScoresAsync<Passage>(x => x.Embedding, query, limit: 4, DistanceFunction.InnerProduct, Token);
        inner[0].Document.Id.ShouldBe("far-east");
        inner[0].Distance.ShouldBe(-5, 1e-6);
    }

    [Fact]
    public async Task a_document_with_no_embedding_is_skipped_not_scored()
    {
        await using var session = _store.QuerySession();

        var all = await session.VectorSearchAsync<Passage>(x => x.Embedding, new float[] { 0, 0, 1 }, limit: 10, token: Token);

        all.Select(x => x.Id).ShouldNotContain("blank");
        all.Count.ShouldBe(4);
    }

    [Fact]
    public async Task a_soft_deleted_document_is_not_returned()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Delete<Passage>("east");
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.QuerySession();
        var nearest = await query.VectorSearchAsync<Passage>(x => x.Embedding, new float[] { 1, 0, 0 }, limit: 1, token: Token);
        nearest.Single().Id.ShouldBe("far-east");
    }

    [Fact]
    public async Task a_write_that_bypassed_fisher_still_ranks_by_the_new_vector()
    {
        // The reason there is no side table: reading from data cannot drift.
        await using (var conn = new SqliteConnection(_database.ConnectionString))
        {
            await conn.OpenAsync(Token);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "update fi_doc_passage set data = json_set(data, '$.embedding', json('[0,0,1]')) where id = 'north'";
            (await cmd.ExecuteNonQueryAsync(Token)).ShouldBe(1);
        }

        await using var session = _store.QuerySession();
        var nearest = await session.VectorSearchAsync<Passage>(x => x.Embedding, new float[] { 0, 0, 1 }, limit: 1, token: Token);
        nearest.Single().Id.ShouldBe("north");
    }

    [Fact]
    public async Task the_index_default_metric_is_used_unless_the_call_names_one()
    {
        var database = TemporaryDatabase.Create("vectors-l2");
        await using var _ = database;
        using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Passage>().VectorIndex(x => x.Embedding, 3, DistanceFunction.L2);
        });
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = store.LightweightSession();
        session.Store(new Passage { Id = "near", Embedding = [1, 0, 0] }, new Passage { Id = "far", Embedding = [5, 0, 0] });
        await session.SaveChangesAsync(Token);

        (await session.VectorSearchAsync<Passage>(x => x.Embedding, new float[] { 1, 0, 0 }, 1, token: Token)).Single().Id.ShouldBe("near");
        (await session.VectorSearchAsync<Passage>(x => x.Embedding, new float[] { 1, 0, 0 }, 1, DistanceFunction.InnerProduct, Token)).Single().Id.ShouldBe("far");
    }

    [Fact]
    public async Task the_attribute_form_declares_the_index_and_camel_case_paths_are_honoured()
    {
        await using var session = _store.LightweightSession();
        session.Store(new Tagged { Id = 1, MyVector = [0, 1] }, new Tagged { Id = 2, MyVector = [1, 0] });
        await session.SaveChangesAsync(Token);

        // "myVector" in the JSON under the default naming policy; the locator must say so too.
        var nearest = await session.VectorSearchAsync<Tagged>(x => x.MyVector, new float[] { 1, 0 }, 1, token: Token);
        nearest.Single().Id.ShouldBe(2);
    }

    [Fact]
    public async Task refusals_name_the_problem()
    {
        await using var session = _store.QuerySession();

        var noIndex = await Should.ThrowAsync<InvalidOperationException>(() =>
            session.VectorSearchAsync<Unindexed>(x => x.Embedding, new float[] { 1, 0, 0 }, token: Token));
        noIndex.Message.ShouldContain("'Unindexed' declares no vector index");
        noIndex.Message.ShouldContain("VectorIndex(x => x.Embedding, dimensions)");

        var wrongMember = await Should.ThrowAsync<InvalidOperationException>(() =>
            session.VectorSearchAsync<Passage>(x => x.Text, new float[] { 1, 0, 0 }, token: Token));
        wrongMember.Message.ShouldContain("'Passage.Text' is not a declared vector member");
        wrongMember.Message.ShouldContain("Declared: Embedding");

        var wrongLength = await Should.ThrowAsync<ArgumentException>(() =>
            session.VectorSearchAsync<Passage>(x => x.Embedding, new float[] { 1, 0 }, token: Token));
        wrongLength.Message.ShouldContain("has 2 dimensions but 'Passage.Embedding' was declared with 3");

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            session.VectorSearchAsync<Passage>(x => x.Embedding, new float[] { 1, 0, 0 }, limit: 0, token: Token));
    }

    [Fact]
    public void declaring_is_refused_by_name_for_the_wrong_member_type_dimensions_or_twice()
    {
        Should.Throw<InvalidOperationException>(() => DocumentStore.For(o =>
            {
                o.ConnectionString = _database.ConnectionString;
                o.Schema.For<Passage>().VectorIndex(x => x.Text, 3);
            }))
            .Message.ShouldContain("'Passage.Text' is a String, which cannot hold an embedding");

        Should.Throw<ArgumentOutOfRangeException>(() => DocumentStore.For(o =>
            {
                o.ConnectionString = _database.ConnectionString;
                o.Schema.For<Passage>().VectorIndex(x => x.Embedding, 0);
            }))
            .Message.ShouldContain("needs a positive dimension count");

        Should.Throw<InvalidOperationException>(() => DocumentStore.For(o =>
            {
                o.ConnectionString = _database.ConnectionString;
                o.Schema.For<Passage>().VectorIndex(x => x.Embedding, 3).VectorIndex(x => x.Embedding, 4);
            }))
            .Message.ShouldContain("already carries a vector index (3 dimensions, Cosine)");
    }

    [Fact]
    public void the_distance_function_itself_is_pinned()
    {
        var q = VectorFunctions.ToBlob(new float[] { 1, 0, 0 });

        VectorFunctions.Distance("cosine", "[1,0,0]", q)!.Value.ShouldBe(0, 1e-9);
        VectorFunctions.Distance("cosine", "[0,1,0]", q)!.Value.ShouldBe(1, 1e-9);
        VectorFunctions.Distance("l2", "[0,1,0]", q)!.Value.ShouldBe(Math.Sqrt(2), 1e-9);
        VectorFunctions.Distance("inner", "[2,0,0]", q)!.Value.ShouldBe(-2, 1e-9);
        VectorFunctions.Distance("cosine", null, q).ShouldBeNull();
        VectorFunctions.Distance("cosine", "[0,0,0]", q)!.Value.ShouldBe(1, 1e-9); // a zero vector is maximally far, not NaN

        Should.Throw<ArgumentException>(() => VectorFunctions.Distance("cosine", "[1,0]", q))
            .Message.ShouldContain("the stored vector has 2 dimensions but the query has 3");
        Should.Throw<ArgumentException>(() => VectorFunctions.Distance("manhattan", "[1,0,0]", q))
            .Message.ShouldContain("metric 'manhattan'");
        Should.Throw<ArgumentException>(() => VectorFunctions.Distance("cosine", "{\"not\":\"an array\"}", q))
            .Message.ShouldContain("not a JSON array");
    }

    public class Passage
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public float[]? Embedding { get; set; }
    }

    public class Tagged
    {
        public int Id { get; set; }

        [VectorIndex(2)]
        public float[] MyVector { get; set; } = [];
    }

    public class Unindexed
    {
        public Guid Id { get; set; }
        public float[]? Embedding { get; set; }
    }
}
