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
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/vector_projection_samples.cs#L43-L53' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_vector_projection_document' title='Start of snippet'>anchor</a></sup>
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
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/vector_projection_samples.cs#L55-L74' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_vector_projection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`Map<TEvent>(content, id)` names the text to embed and the document it belongs to; `Delete<TEvent>(id)`
names the document to remove. Within one page of events the last write for an id wins, so a stream
that revises the same article twice costs one embedding rather than two, and a create followed by a
delete ends deleted.

Mapping one event type twice, or for both content and deletion, is refused when the projection is
constructed — the outcome would otherwise depend on registration order. So is a projection that maps
nothing.

`VectorProjectionMap<TId>` lives in `JasperFx.Events.Vectors` and is shared with every store
([jasperfx#841](https://github.com/JasperFx/jasperfx/issues/841)). It was modelled on Fisher's own,
so nothing about a declaration moved — but the type parameter did: `Configure` now takes
`VectorProjectionMap<TId>` where it used to take `VectorProjectionMap<TDoc, TId>`. The document type
was never used by the map, and dropping it is what lets one map serve a store whose projection writes
something other than a Fisher document.

## Building the text from aggregate state

A content selector sees **one event**, and for a partial-update event that has no right answer.
Given `ArticleRevised { Title = null, Body = "new", Tags = null }`, where null means *unchanged*,
returning the new body re-embeds the article without its title and tags, and returning null leaves
the embedding stale. `MapFromAggregate` is the third answer: build the text from the aggregate as it
stands after the page's events.

<!-- snippet: sample_vector_projection_from_aggregate -->
<a id='snippet-sample_vector_projection_from_aggregate'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/vector_projection_samples.cs#L76-L93' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_vector_projection_from_aggregate' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The aggregate it folds is an ordinary self-aggregating type:

<!-- snippet: sample_vector_projection_aggregate -->
<a id='snippet-sample_vector_projection_aggregate'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/vector_projection_samples.cs#L24-L41' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_vector_projection_aggregate' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

- **Each trigger names the document id it belongs to**, usually `e => e.StreamId` or
  `e => e.StreamKey`, because the document's identity is not always the stream's.
- **Fisher folds the stream live**, up to the version of the last triggering event in the page — one
  read per affected stream per page. Not the head of the stream: a projection replaying history must
  embed the state the page describes, or a rebuild would produce a different embedding than the
  original run did for every stream that has moved on since.
- ⚠️ **Reading an async snapshot would be the wrong answer, which is why Fisher does not.** The
  daemon does not order shards against each other, so a vector projection on one shard can see a
  snapshot another shard has not caught up to — and the embedding is then silently built from stale
  state.
- **A stream that folds to nothing is "nothing to index"**, the same as a content selector returning
  null.
- **Content hashing does the rest.** A triggering event that turns out not to change the built text
  costs no model call at all — which is the common case precisely when the events are partial
  updates.

One aggregate mapping per projection, and an event type cannot be both a trigger and a `Map` or a
`Delete`; both are refused when the projection is constructed.

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
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/vector_projection_samples.cs#L99-L106' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_vector_projection_registration' title='Start of snippet'>anchor</a></sup>
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

::: tip
**The hash is lowercase hex SHA-256 of the UTF-8 text, and that spelling is now the shared one.** It
is *persisted* beside the vector, so a store that changed how it spelled the hash would miss on every
stored row exactly once and re-embed an entire corpus at the provider's meter. Fisher's private hash
and `VectorEmbeddingPlan<TId>.HashOf` are byte-for-byte the same function, so adopting the shared
core re-embeds nothing — pinned by a test rather than asserted here, because the whole failure would
be silent.
:::

## Why it is async-only

::: warning
**Async only.** Embedding is a network call per batch, and an inline projection runs inside the
caller's `SaveChangesAsync` — holding SQLite's single write lock open across a round trip to an
embedding API would block every other writer in the process for its duration.
:::

**Fisher refuses it** ([fisher#287](https://github.com/JasperFx/fisher/issues/287)). Registering a
vector projection with any lifecycle but `Async` fails when the store is built, naming the projection,
the lifecycle it was given and `ProjectionLifecycle.Async` — which is the last moment before the
mistake costs anything. Until Fisher 1.10.0 it was documented and unenforced, so an `Inline`
registration simply worked, on a laptop where the model call is quick, and in production put a
metered network round trip inside every caller's `SaveChangesAsync`. On the daemon the same round
trip holds up only the shard that is waiting for it.

The refusal is an ordinary `IValidatedProjection<StoreOptions>`, which is possible only because
[jasperfx#845](https://github.com/JasperFx/jasperfx/issues/845) shipped: a bare `IProjection` is
registered through a `ProjectionWrapper`, and validity used to be checked against the wrapper rather
than the projection inside it. The same fix means **a hand-written `IProjection` in your own
application that already implemented `IValidatedProjection<StoreOptions>` starts being asked** on
this upgrade, which can surface configuration errors that were silently passing.

## Where it differs from Marten.PgVector

The projection is ported in shape from
[Marten.PgVector's event-sourced vector projection](https://martendb.io/documents/pgvector#event-sourced-vector-projection):
map event types to text, hash the text, skip re-embedding when the hash is unchanged, upsert by the
mapped id. Three things are deliberately different, and all three come from that template rather
than from SQLite.

All three are now the **shared** behaviour rather than Fisher's alone: Fisher's shapes were taken as
the reference when `VectorProjectionMap<TId>` and `VectorEmbeddingPlan<TId>` were lifted into
`JasperFx.Events.Vectors`, so what follows describes what every Critter Stack store does and what
Marten.PgVector's own template did not.

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
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/vector_projection_samples.cs#L111-L127' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_vector_projection_embedding_provider' title='Start of snippet'>anchor</a></sup>
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
