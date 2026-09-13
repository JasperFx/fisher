using Fisher.Projections.Vectors;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Vectors;
using Shouldly;

namespace Fisher.Tests.Projections;

/// <summary>
///     fisher#261 — <c>VectorProjection</c>, embeddings produced from an event stream.
/// </summary>
/// <remarks>
///     <para>
///         <b>Four of the facts here are regression guards against defects in the template</b>
///         (<c>Marten.PgVector.Projection.VectorProjection</c>) rather than against something Fisher
///         got wrong: the embedding committing outside the caller's transaction, <c>Guid</c>-only
///         identity, a delete that ignores the configured id selector, and a content selector whose
///         exception is swallowed into "no content". They are written as facts here because nothing
///         else would notice if this implementation drifted back toward any of them.
///     </para>
///     <para>
///         The provider is a recording stub. Counting its <em>calls</em> is most of the value: hash
///         skipping is invisible in the resulting documents — the row is identical either way — and
///         only the call count tells a skip from a re-embed.
///     </para>
/// </remarks>
public class vector_projection : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("vector-projection");
    private readonly RecordingEmbeddings _embeddings = new();
    private DocumentStore _store = null!;

    /// <summary>
    ///     One daemon for the fixture, stopped before the file is deleted.
    /// </summary>
    /// <remarks>
    ///     ⚠️ <c>IProjectionDaemon</c> is <c>IDisposable</c> only, so disposing does not await
    ///     in-flight shard work — and a shard's next poll <b>re-creates the database file the fixture
    ///     has already deleted</b>. A daemon per call leaked one under load and not in isolation,
    ///     which is the shape fisher#189 records; this is the pattern the other daemon tests use.
    /// </remarks>
    private IProjectionDaemon? _daemon;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<ArticleEmbedding>().VectorIndex(x => x.Embedding, dimensions: 3);
            options.Projections.Add(new ArticleVectors(_embeddings), ProjectionLifecycle.Async);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_daemon is not null)
        {
            await _daemon.StopAllAsync();
            _daemon.Dispose();
        }

        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task ProjectAsync(params object[] events)
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(streamId, events);
            await session.SaveChangesAsync(Token);
        }

        if (_daemon is null)
        {
            _daemon = await _store.BuildProjectionDaemonAsync();
            await _daemon.StartAllAsync();
        }

        await _store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    ///     An event produces an embedded document, searchable through the ordinary vector surface.
    /// </summary>
    /// <remarks>
    ///     Asserted by <em>searching</em> rather than by loading, because writing the embedding into a
    ///     column no index covers would satisfy a load and is the exact failure the configuration-time
    ///     check exists for.
    /// </remarks>
    [Fact]
    public async Task an_event_becomes_a_searchable_embedding()
    {
        await ProjectAsync(new ArticlePublished("a", "the quick brown fox"));

        await using var session = _store.QuerySession();

        var hits = await session.VectorSearchAsync<ArticleEmbedding>(
            x => x.Embedding, new float[] { 1, 0, 0 }, limit: 5, token: Token);

        var hit = hits.ShouldHaveSingleItem();
        hit.Id.ShouldBe("a");
        hit.Content.ShouldBe("the quick brown fox");
        hit.ContentHash.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    ///     <b>Unchanged content costs no embedding call.</b>
    /// </summary>
    /// <remarks>
    ///     The whole point of the content hash, and invisible in the stored document — the row is
    ///     identical whether it was skipped or re-embedded, so only the provider's call count can tell
    ///     the two apart. Embedding is the metered part; this is what makes re-projecting affordable.
    /// </remarks>
    [Fact]
    public async Task unchanged_content_is_not_embedded_again()
    {
        await ProjectAsync(new ArticlePublished("a", "the quick brown fox"));
        _embeddings.Texts.Count.ShouldBe(1);

        await ProjectAsync(new ArticlePublished("a", "the quick brown fox"));

        _embeddings.Texts.Count.ShouldBe(1, "the content hash was unchanged, so the provider should not have been called again");
    }

    /// <summary>Changed content is embedded again, which is the other half of the same rule.</summary>
    [Fact]
    public async Task changed_content_is_embedded_again()
    {
        await ProjectAsync(new ArticlePublished("a", "the quick brown fox"));
        await ProjectAsync(new ArticlePublished("a", "a slow grey badger"));

        _embeddings.Texts.Count.ShouldBe(2);

        await using var session = _store.QuerySession();
        (await session.LoadAsync<ArticleEmbedding>("a", Token))!.Content.ShouldBe("a slow grey badger");
    }

    /// <summary>
    ///     ⚠️ <b>A delete addresses the row the map wrote, not the stream.</b>
    /// </summary>
    /// <remarks>
    ///     The template's defect, and the sharpest of the four: its delete path reads
    ///     <c>@event.StreamId</c> unconditionally and ignores the configured id selector, so a
    ///     projection keyed on a payload member — as this one is — deletes nothing and the row stays
    ///     in the index forever. This projection is keyed on the article id and appended on a stream
    ///     whose id is a fresh Guid, so a stream-id delete could not possibly match.
    /// </remarks>
    [Fact]
    public async Task a_delete_addresses_the_mapped_id_rather_than_the_stream()
    {
        await ProjectAsync(new ArticlePublished("a", "the quick brown fox"));

        await using (var reading = _store.QuerySession())
        {
            (await reading.LoadAsync<ArticleEmbedding>("a", Token)).ShouldNotBeNull();
        }

        await ProjectAsync(new ArticleRetracted("a"));

        await using var session = _store.QuerySession();
        (await session.LoadAsync<ArticleEmbedding>("a", Token)).ShouldBeNull();
    }

    /// <summary>
    ///     ⚠️ <b>A selector that throws faults the shard rather than reading as "no content".</b>
    /// </summary>
    /// <remarks>
    ///     The template catches everything a selector throws and returns null, which the caller reads
    ///     as an event carrying no content — so a bug in a selector silently drops the document from
    ///     the index with nothing reported. Here it propagates, which is what the daemon's error
    ///     handling exists for and the only outcome an operator can act on. Asserted through the dead
    ///     letter the shard records, since a faulted shard is how "propagates" is observable.
    /// </remarks>
    [Fact]
    public async Task a_selector_that_throws_is_not_swallowed()
    {
        await using var session = _store.LightweightSession();
        var projection = new ThrowingArticleVectors(_embeddings);

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() =>
            projection.ApplyAsync(session,
                [new Event<ArticlePublished>(new ArticlePublished("a", "x"))],
                Token));

        thrown.Message.ShouldBe("boom");

    }

    /// <summary>
    ///     ⚠️ <b>The identity is not Guid-only.</b> This whole class is keyed on <c>string</c>.
    /// </summary>
    /// <remarks>
    ///     Marten's template hardcodes <c>Guid</c> at every layer — the table column, the extraction
    ///     tuple, the hash dictionary — so a string-identified store cannot use it at all. That every
    ///     other fact here passes over a string-keyed document is the evidence; this one states it so
    ///     the property is not merely incidental to the fixture.
    /// </remarks>
    [Fact]
    public async Task the_document_identity_is_not_restricted_to_guid()
    {
        await ProjectAsync(new ArticlePublished("kebab-case-id", "text"));

        await using var session = _store.QuerySession();
        (await session.LoadAsync<ArticleEmbedding>("kebab-case-id", Token)).ShouldNotBeNull();
    }

    /// <summary>
    ///     An index declared on the wrong member is refused rather than written past.
    /// </summary>
    /// <remarks>
    ///     Silent otherwise, and in the worst direction: the projection writes one column and every
    ///     search reads another, which is valid SQL, no error, and an index that is always empty.
    /// </remarks>
    [Fact]
    public async Task a_document_with_no_index_on_the_embedding_member_is_refused()
    {
        using var database = TemporaryDatabase.Create("vector-projection-unindexed");

        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<ArticleEmbedding>();
            options.Projections.Add(new ArticleVectors(new RecordingEmbeddings()), ProjectionLifecycle.Async);
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = store.LightweightSession();
        var projection = new ArticleVectors(new RecordingEmbeddings());

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() =>
            projection.ApplyAsync(session,
                [new Event<ArticlePublished>(new ArticlePublished("a", "x"))],
                Token));

        thrown.Message.ShouldContain("no vector index");
        thrown.Message.ShouldContain(nameof(ArticleEmbedding.Embedding));
    }

    /// <summary>
    ///     A dimension count that disagrees with the provider is refused.
    /// </summary>
    /// <remarks>
    ///     Worse than the wrong member: the rows are written, and every search then refuses at the
    ///     <em>caller's</em> end with a message about the query vector — pointing away from the
    ///     projection that produced them.
    /// </remarks>
    [Fact]
    public async Task a_dimension_mismatch_with_the_provider_is_refused()
    {
        using var database = TemporaryDatabase.Create("vector-projection-dimensions");

        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<ArticleEmbedding>().VectorIndex(x => x.Embedding, dimensions: 8);
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = store.LightweightSession();
        var projection = new ArticleVectors(new RecordingEmbeddings());

        (await Should.ThrowAsync<InvalidOperationException>(() =>
                projection.ApplyAsync(session,
                    [new Event<ArticlePublished>(new ArticlePublished("a", "x"))],
                    Token)))
            .Message.ShouldContain("8 dimensions");
    }

    /// <summary>
    ///     Mapping one event type for content and for deletion is refused at configuration time.
    /// </summary>
    [Fact]
    public void an_event_mapped_both_ways_is_refused()
    {
        var map = new VectorProjectionMap<ArticleEmbedding, string>();
        map.Map<ArticlePublished>(e => e.Data.Text, e => e.Data.Id);

        Should.Throw<InvalidOperationException>(() => map.Delete<ArticlePublished>(e => e.Data.Id))
            .Message.ShouldContain("both write and remove");
    }
}

public record ArticlePublished(string Id, string Text);

public record ArticleRetracted(string Id);

public class ArticleEmbedding : IVectorized<string>
{
    public string Id { get; set; } = "";
    public string? Content { get; set; }
    public string? ContentHash { get; set; }
    public float[]? Embedding { get; set; }
}

/// <summary>Keyed on the article id from the payload, never on the stream.</summary>
public class ArticleVectors : VectorProjection<ArticleEmbedding, string>
{
    public ArticleVectors(IEmbeddingProvider provider) : base(provider)
    {
    }

    protected override void Configure(VectorProjectionMap<ArticleEmbedding, string> map)
    {
        map.Map<ArticlePublished>(e => e.Data.Text, e => e.Data.Id);
        map.Delete<ArticleRetracted>(e => e.Data.Id);
    }
}

/// <summary>A content selector with a bug in it, which is the case the template swallows.</summary>
public class ThrowingArticleVectors : VectorProjection<ArticleEmbedding, string>
{
    public ThrowingArticleVectors(IEmbeddingProvider provider) : base(provider)
    {
    }

    protected override void Configure(VectorProjectionMap<ArticleEmbedding, string> map)
        => map.Map<ArticlePublished>(_ => throw new InvalidOperationException("boom"), e => e.Data.Id);
}

public sealed class RecordingEmbeddings : IEmbeddingProvider
{
    public List<string> Texts { get; } = [];

    public int Dimensions => 3;

    public Task<ReadOnlyMemory<float>[]> GenerateEmbeddingsAsync(string[] texts, CancellationToken ct = default)
    {
        Texts.AddRange(texts);

        // Deterministic and content-dependent, so a re-embed is observable in the row as well as in
        // the call count.
        return Task.FromResult(texts
            .Select(t => new ReadOnlyMemory<float>([t.Length % 7, (t.Length * 3) % 5, 1]))
            .ToArray());
    }
}
