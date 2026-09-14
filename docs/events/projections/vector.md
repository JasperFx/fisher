# Vector Projections

`VectorProjection<TDoc, TId>` is the half of vector search that *produces* a vector, where
[`VectorSearchAsync`](/documents/querying/vector-search) searches one the application already put on
the document.

## The document

<!-- snippet: sample_vector_projection_document -->
<a id='snippet-sample_vector_projection_document'></a>
```cs
// An ordinary Fisher document. IVectorized<TId> names the four members the projection writes, and
// TId is whatever identity the document has -- a string slug here, not a Guid.
public class ArticleVector : IVectorized<string>
{
    public string Id { get; set; } = "";
    public string? Content { get; set; }
    public string? ContentHash { get; set; }
    public float[]? Embedding { get; set; }
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/vector_projection_samples.cs#L22-L32' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_vector_projection_document' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The projection writes an ordinary Fisher document, not a table of its own. `IVectorized<TId>` names
the four members it writes — `Id`, `Content`, `ContentHash`, `Embedding` — and the document declares
`VectorIndex(x => x.Embedding, dimensions)`. That is what makes what the projection writes searchable
with nothing else added: [`VectorSearchAsync`](/documents/querying/vector-search) and
[`HybridSearchAsync`](/documents/querying/hybrid-search) read it like any other document, and the
migration, soft delete and identity map apply without the projection knowing about any of them.

`Content` is stored as well as hashed, which is what makes re-embedding possible when you change model
or dimension count — without the text, that would mean replaying the stream.

## Mapping events

<!-- snippet: sample_vector_projection -->
<a id='snippet-sample_vector_projection'></a>
```cs
public class ArticleVectorProjection : VectorProjection<ArticleVector, string>
{
    public ArticleVectorProjection(IEmbeddingProvider provider) : base(provider)
    {
    }

    protected override void Configure(VectorProjectionMap<ArticleVector, string> map)
    {
        // The text to embed, and the document it belongs to -- keyed on the payload, not the stream.
        map.Map<ArticleDrafted>(e => e.Data.Body, e => e.Data.Slug);

        // Null means "this event carries no content" and skips it. Throwing faults the shard.
        map.Map<ArticleRevised>(e => e.Data.Body, e => e.Data.Slug);

        // A delete names its id too. There is no overload that defaults to the stream id.
        map.Delete<ArticleWithdrawn>(e => e.Data.Slug);
    }
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/vector_projection_samples.cs#L34-L53' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_vector_projection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`Map<TEvent>(content, id)` names the text to embed and the document it belongs to; `Delete<TEvent>(id)`
names the document to remove. Within one page of events the last write for an id wins, so a stream
that revises the same article twice costs one embedding rather than two, and a create followed by a
delete ends deleted.

Mapping one event type twice, or for both content and deletion, is refused when the projection is
constructed — the outcome would otherwise depend on registration order. So is a projection that maps
nothing.

## Registering it

<!-- snippet: sample_vector_projection_registration -->
<a id='snippet-sample_vector_projection_registration'></a>
```cs
// The index goes on Embedding, at the provider's dimension count -- both are checked on the
// first page the projection runs.
opts.Schema.For<ArticleVector>().VectorIndex(x => x.Embedding, dimensions: provider.Dimensions);

// Async: the model call happens on the daemon, never inside the caller's SaveChangesAsync.
opts.Projections.Add(new ArticleVectorProjection(provider), ProjectionLifecycle.Async);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/vector_projection_samples.cs#L59-L66' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_vector_projection_registration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The projection checks the declaration on the first page it runs — a store is built after its
projections, so there is no schema to ask any sooner. A document with no vector index on `Embedding`,
or one declared with a different dimension count from the provider's, is refused by name. Both fail
silently otherwise: the first writes a member nothing searches, and the second writes rows every
search then refuses with a message about the *caller's* query vector.

An async projection advances only while the [async daemon](/events/projections/async-daemon) runs.

## Unchanged content costs no embedding call

::: tip
**Unchanged content costs no embedding call.** The content is SHA-256 hashed and the stored hash is
compared before the provider is asked, so re-projecting a stream whose text has not moved is a read
and nothing else. Embedding is the metered part.
:::

The comparison is one query for the page's ids, through the session. Only the texts whose hash moved
go to the provider, and they go as **one** `GenerateEmbeddingsAsync` call for the page rather than one
per document — a page of a hundred changed documents is one round trip, not a hundred.

## Why it is async-only

::: warning
**Async only.** Embedding is a network call per batch, and an inline projection runs inside the
caller's `SaveChangesAsync` — holding SQLite's single write lock open across a round trip to an
embedding API would block every other writer in the process for its duration.
:::

Fisher does not refuse `ProjectionLifecycle.Inline` — the projection is an `IProjection` like any
other, and it would run — so this is a rule to keep rather than one the store enforces for you. On the
daemon, the same round trip holds up only the shard that is waiting for it.

## Where it differs from Marten.PgVector

The projection is ported in shape from
[Marten.PgVector's event-sourced vector projection](https://martendb.io/documents/pgvector#event-sourced-vector-projection):
map event types to text, hash the text, skip re-embedding when the hash is unchanged, upsert by the
mapped id. Three things are deliberately different, and all three come from that template rather
than from SQLite.

**The identity is not `Guid`-only.** Marten's hardcodes `Guid` at every layer, so a string-identified
store cannot use it at all. `TId` here is any identity Fisher stores, strong-typed wrappers included.

**A delete takes an id selector, always.** Marten's `Delete<TEvent>()` falls back to the stream id
when no selector is given, and refuses the one combination that cannot work — content keyed on the
event, deletes by stream. Fisher has no selector-less overload at all: a projection keyed on a payload
member would otherwise write rows under one id and delete under another, and the delete would
silently match nothing. Requiring the selector on both sides makes the two incapable of disagreeing;
the common case costs `e => e.StreamId`.

**It commits with the events that produced it.** Marten's reads and writes on a connection of its
own, outside the batch's transaction. Here everything is queued onto the session the daemon hands
over, so the embedding and the projection's progress land in one transaction — and on SQLite a
second connection writing while the batch holds the write lock would block against itself anyway.

One thing that used to differ no longer does: a content selector that **throws** is not caught by
either. Here it faults the shard, which is what the daemon's error handling is for; swallowing the
exception into "no content" would drop the document out of the index with nothing reported anywhere.
To skip an event on purpose, return **null**, which means "this event carries no content".

## Wiring an embedding provider

Fisher never calls a model. The projection takes an `IEmbeddingProvider` from
`JasperFx.Events.Vectors` — a `Dimensions` count and one batch method, `GenerateEmbeddingsAsync`,
returning one vector per text in input order — and a provider that returns a different number of
vectors than it was given texts is refused rather than paired up wrongly.

Write one for whatever model you run, or adapt a Microsoft.Extensions.AI generator with the
`JasperFx.Events.MicrosoftExtensionsAI` package:

<!-- snippet: sample_vector_projection_embedding_provider -->
<a id='snippet-sample_vector_projection_embedding_provider'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/vector_projection_samples.cs#L71-L87' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_vector_projection_embedding_provider' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`AsEmbeddingProvider()` with no argument takes the dimension count from the generator's metadata, and
throws if the generator publishes none — the index needs the number before the first text is
embedded. The adapter checks every vector's length and the one-per-input rule before Fisher sees the
result.

Use the same provider on the query side: its `GenerateEmbeddingAsync(text)` extension produces the
query vector for [`VectorSearchAsync`](/documents/querying/vector-search) and
[`HybridSearchAsync`](/documents/querying/hybrid-search).

## See also

- [Vector Search](/documents/querying/vector-search) — searching what this writes.
- [Hybrid Search](/documents/querying/hybrid-search) — fusing it with the full-text leg.
- [Marten's pgvector support](https://martendb.io/documents/pgvector) — the PostgreSQL sibling, and
  the template this projection was ported from.
- [Polecat's Vector Search](https://polecat.jasperfx.net/documents/querying/vector-search) — the SQL
  Server 2025 sibling.
