using Fisher.Projections.Vectors;
using JasperFx.Events.MicrosoftExtensionsAI;
using JasperFx.Events.Projections;
using JasperFx.Events.Vectors;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Fisher.Tests.Documentation;

/*
 * The compiled source behind docs/events/projections/vector.md.
 *
 * See "Documentation samples come from compiled code" in CLAUDE.md.
 */

public record ArticleDrafted(string Slug, string Body);

public record ArticleRevised(string Slug, string? Body);

public record ArticleWithdrawn(string Slug);

public record ArticleTagged(string Slug, string Tag);

#region sample_vector_projection_aggregate
// The aggregate the embedded text is built from. An ordinary self-aggregating Fisher type -- the
// projection does not care where the fields came from, only what they hold now.
public class Article
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public List<string> Tags { get; } = [];

    public static Article Create(ArticleDrafted e) => new() { Id = e.Slug, Body = e.Body };

    // The merge: null means "unchanged", which is exactly what a single-event selector cannot embed.
    public void Apply(ArticleRevised e) => Body = e.Body ?? Body;

    public void Apply(ArticleTagged e) => Tags.Add(e.Tag);
}
#endregion

#region sample_vector_projection_document
// An ordinary Fisher document. IVectorized<TId> names the four members the projection writes, and
// TId is whatever identity the document has -- a string slug here, not a Guid.
public class ArticleVector : IVectorized<string>
{
    public string Id { get; set; } = "";
    public string? Content { get; set; }
    public string? ContentHash { get; set; }
    public float[]? Embedding { get; set; }
}
#endregion

#region sample_vector_projection
public class ArticleVectorProjection : VectorProjection<ArticleVector, string>
{
    public ArticleVectorProjection(IEmbeddingProvider provider) : base(provider)
    {
    }

    protected override void Configure(VectorProjectionMap<string> map)
    {
        // The text to embed, and the document it belongs to -- keyed on the payload, not the stream.
        map.Map<ArticleDrafted>(e => e.Data.Body, e => e.Data.Slug);

        // Null means "this event carries no content" and skips it. Throwing faults the shard.
        map.Map<ArticleRevised>(e => e.Data.Body, e => e.Data.Slug);

        // A delete names its id too. There is no overload that defaults to the stream id.
        map.Delete<ArticleWithdrawn>(e => e.Data.Slug);
    }
}
#endregion

#region sample_vector_projection_from_aggregate
public class ArticleAggregateVectors : VectorProjection<ArticleVector, string>
{
    public ArticleAggregateVectors(IEmbeddingProvider provider) : base(provider)
    {
    }

    protected override void Configure(VectorProjectionMap<string> map)
        => map.MapFromAggregate<Article>(
            // The text, built from the aggregate as it stands after this page's events.
            article => $"{article.Title}\n{article.Body}\n{string.Join(", ", article.Tags)}",

            // The events that make it worth rebuilding, each paired with the document id it names.
            (typeof(ArticleDrafted), e => e.StreamKey!),
            (typeof(ArticleRevised), e => e.StreamKey!),
            (typeof(ArticleTagged), e => e.StreamKey!));
}
#endregion

public static class vector_projection_samples
{
    public static void register(StoreOptions opts, IEmbeddingProvider provider)
    {
        #region sample_vector_projection_registration
        // The index goes on Embedding, at the provider's dimension count -- both are checked on the
        // first page the projection runs.
        opts.Schema.For<ArticleVector>().VectorIndex(x => x.Embedding, dimensions: provider.Dimensions);

        // Async: the model call happens on the daemon, never inside the caller's SaveChangesAsync.
        opts.Projections.Add(new ArticleVectorProjection(provider), ProjectionLifecycle.Async);
        #endregion
    }

    public static void from_microsoft_extensions_ai(IServiceCollection services)
    {
        #region sample_vector_projection_embedding_provider
        // Any Microsoft.Extensions.AI generator -- OpenAI, Azure OpenAI, Ollama, ONNX -- registered
        // the way its own package says to.
        services.ConfigureFisher((serviceProvider, options) =>
        {
            var generator = serviceProvider
                .GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

            // From JasperFx.Events.MicrosoftExtensionsAI. Omit dimensions to take them from the
            // generator's metadata; naming them always wins.
            var provider = generator.AsEmbeddingProvider(dimensions: 768);

            options.Schema.For<ArticleVector>()
                .VectorIndex(x => x.Embedding, dimensions: provider.Dimensions);
            options.Projections.Add(new ArticleVectorProjection(provider), ProjectionLifecycle.Async);
        });
        #endregion
    }
}
