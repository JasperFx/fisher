using System.Security.Cryptography;
using System.Text;
using Fisher.Projections.Vectors;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Vectors;
using Shouldly;

namespace Fisher.Tests.Projections;

/// <summary>
///     fisher#291 and fisher#287 — what changed when <c>VectorProjection</c> moved onto the shared
///     <see cref="VectorProjectionMap{TId}" /> / <see cref="VectorEmbeddingPlan{TId}" /> core, and the
///     async-only refusal that came with it.
/// </summary>
public class vector_projection_shared_core : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("vector-shared-core");
    private readonly RecordingEmbeddings _embeddings = new();
    private readonly RecordingEmbeddings _aggregateEmbeddings = new();
    private DocumentStore _store = null!;
    private IProjectionDaemon? _daemon;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.StreamIdentity = StreamIdentity.AsString;
            options.Schema.For<MemoEmbedding>().VectorIndex(x => x.Embedding, dimensions: 3);
            options.Schema.For<AggregateMemoEmbedding>().VectorIndex(x => x.Embedding, dimensions: 3);
            options.Projections.Add(new MemoVectors(_embeddings), ProjectionLifecycle.Async);
            options.Projections.Add(new AggregateMemoVectors(_aggregateEmbeddings), ProjectionLifecycle.Async);
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

    private async Task ProjectAsync(string streamKey, params object[] events)
    {
        await using (var session = _store.LightweightSession())
        {
            session.Events.Append(streamKey, events);
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
    ///     ⚠️ <b>The shared hash is spelled exactly as Fisher's private one was.</b>
    /// </summary>
    /// <remarks>
    ///     This is the fact that decides whether adopting <see cref="VectorEmbeddingPlan{TId}" /> is
    ///     free or costs a customer a full re-embedding of their corpus at their provider's meter. The
    ///     hash is PERSISTED beside the vector and compared on every page, so a different spelling —
    ///     upper-case hex, base64, a different encoding — would miss on every stored row exactly once
    ///     and call the model for all of them. Both are lowercase hex SHA-256 of the UTF-8 text, so
    ///     nothing re-embeds; pinned rather than asserted in prose because the whole failure is silent.
    /// </remarks>
    [Fact]
    public void the_shared_hash_is_fishers_own_spelling()
    {
        foreach (var content in new[] { "", "the quick brown fox", "naïve — Ünicode ✅", new string('x', 10_000) })
        {
            // The spelling Fisher's private Sha256 used, byte for byte.
            var fisher = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

            VectorEmbeddingPlan<string>.HashOf(content).ShouldBe(fisher);
        }

        // And the value itself, so neither side can drift together.
        VectorEmbeddingPlan<string>.HashOf("abc").ShouldBe(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    /// <summary>
    ///     A stored hash written by the previous implementation is still a hit after the swap.
    /// </summary>
    /// <remarks>
    ///     The end-to-end form of the fact above, and the only one that would notice if the plan ever
    ///     started hashing something other than the content it stores — the row is planted with the
    ///     hash the old code would have written, and the assertion is that the provider is never
    ///     called.
    /// </remarks>
    [Fact]
    public async Task a_hash_written_before_the_swap_still_skips_the_model()
    {
        const string content = "a memo about invoices";

        await using (var session = _store.LightweightSession())
        {
            session.Store(new MemoEmbedding
            {
                Id = "m1",
                Content = content,
                ContentHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
                Embedding = [1, 0, 0]
            });
            await session.SaveChangesAsync(Token);
        }

        await ProjectAsync("stream-1", new MemoWritten("m1", content));

        _embeddings.Texts.ShouldBeEmpty();
    }

    /// <summary>
    ///     <b><c>MapFromAggregate</c> is the new capability, and partial-update events are why.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>MemoRevised</c> is a merge whose fields are null where unchanged. A content selector
    ///         that sees one event has no correct answer for it: returning the new body re-embeds the
    ///         document without its title, and returning null leaves the embedding stale. Building the
    ///         text from the aggregate as it stands after the page is the third answer.
    ///     </para>
    ///     <para>
    ///         Fisher folds the stream live, up to the version of the last triggering event in the
    ///         page — see <c>ApplyAggregatesAsync</c> for why an async snapshot would be wrong.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task content_can_be_built_from_aggregate_state()
    {
        await ProjectAsync("memo-2",
            new MemoWritten("m2", "quarterly report"),
            new MemoTitled("m2", "Q3"),
            new MemoRevised("m2", "quarterly report, revised"));

        await using var session = _store.QuerySession();

        var document = await session.LoadAsync<AggregateMemoEmbedding>("m2", Token);

        document.ShouldNotBeNull();

        // Both fields, from the folded aggregate -- not the merge event's non-null half alone.
        document.Content.ShouldBe("Q3 :: quarterly report, revised");
    }

    /// <summary>
    ///     An <c>Inline</c> registration is refused when the store is built (fisher#287).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Registering a bare <c>IProjection</c> goes through JasperFx's <c>ProjectionGraph.Add</c>,
    ///         which refuses only <c>Live</c> — so this used to succeed and every later
    ///         <c>SaveChangesAsync</c> called the embedding provider while holding SQLite's one write
    ///         lock.
    ///     </para>
    ///     <para>
    ///         Asserted through <c>DocumentStore.For</c> rather than through the registration call,
    ///         because the check is an <c>IValidatedProjection</c> and validation runs when the store
    ///         is constructed — which is the last moment before the mistake costs anything.
    ///     </para>
    /// </remarks>
    [Fact]
    public void an_inline_registration_is_refused_by_name()
    {
        var ex = Should.Throw<InvalidProjectionException>(() => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.Schema.For<MemoEmbedding>().VectorIndex(x => x.Embedding, dimensions: 3);
            options.Projections.Add(new MemoVectors(_embeddings), ProjectionLifecycle.Inline);
        }));

        ex.Message.ShouldContain("MemoVectors");
        ex.Message.ShouldContain("Inline");
        ex.Message.ShouldContain("ProjectionLifecycle.Async");
    }

    /// <summary>The same registration through a base-typed reference is refused too.</summary>
    /// <remarks>
    ///     The refusal reads the lifecycle off the registration rather than off the projection, so it
    ///     cannot be reached around by the spelling of the variable that held it.
    /// </remarks>
    [Fact]
    public void the_refusal_does_not_depend_on_how_the_projection_was_typed()
    {
        Fisher.Projections.IProjection projection = new MemoVectors(_embeddings);

        Should.Throw<InvalidProjectionException>(() => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.Schema.For<MemoEmbedding>().VectorIndex(x => x.Embedding, dimensions: 3);
            options.Projections.Add(projection, ProjectionLifecycle.Inline);
        }));
    }

    /// <summary>Async is what the projection is for, and stays accepted.</summary>
    [Fact]
    public void an_async_registration_is_accepted()
    {
        using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.Schema.For<MemoEmbedding>().VectorIndex(x => x.Embedding, dimensions: 3);
            options.Projections.Add(new MemoVectors(_embeddings), ProjectionLifecycle.Async);
        });

        store.ShouldNotBeNull();
    }
}

public record MemoWritten(string Id, string Body);

public record MemoTitled(string Id, string Title);

/// <summary>A merge: null means "unchanged", which is what no single-event selector can embed.</summary>
public record MemoRevised(string Id, string? Body);

public class MemoEmbedding : IVectorized<string>
{
    public string Id { get; set; } = "";
    public string? Content { get; set; }
    public string? ContentHash { get; set; }
    public float[]? Embedding { get; set; }
}

public class AggregateMemoEmbedding : IVectorized<string>
{
    public string Id { get; set; } = "";
    public string? Content { get; set; }
    public string? ContentHash { get; set; }
    public float[]? Embedding { get; set; }
}

public class Memo
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
    public string? Body { get; set; }

    public static Memo Create(MemoWritten e) => new() { Id = e.Id, Body = e.Body };

    public void Apply(MemoTitled e) => Title = e.Title;

    public void Apply(MemoRevised e) => Body = e.Body ?? Body;
}

public class MemoVectors : VectorProjection<MemoEmbedding, string>
{
    public MemoVectors(IEmbeddingProvider provider) : base(provider)
    {
    }

    protected override void Configure(VectorProjectionMap<string> map)
        => map.Map<MemoWritten>(e => e.Data.Body, e => e.Data.Id);
}

public class AggregateMemoVectors : VectorProjection<AggregateMemoEmbedding, string>
{
    public AggregateMemoVectors(IEmbeddingProvider provider) : base(provider)
    {
    }

    protected override void Configure(VectorProjectionMap<string> map)
        => map.MapFromAggregate<Memo>(
            memo => $"{memo.Title} :: {memo.Body}",
            (typeof(MemoWritten), e => ((IEvent<MemoWritten>)e).Data.Id),
            (typeof(MemoTitled), e => ((IEvent<MemoTitled>)e).Data.Id),
            (typeof(MemoRevised), e => ((IEvent<MemoRevised>)e).Data.Id));
}
