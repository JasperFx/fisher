# Vector Projections

`VectorProjection<TDoc, TId>` is the half of vector search that *produces* a vector, where
[`VectorSearchAsync`](/documents/querying/vector-search) searches one the application already put on
the document.

```cs
public class ArticleVectors : VectorProjection<ArticleEmbedding, string>
{
    public ArticleVectors(IEmbeddingProvider provider) : base(provider) { }

    protected override void Configure(VectorProjectionMap<ArticleEmbedding, string> map)
    {
        map.Map<ArticlePublished>(e => e.Data.Text, e => e.Data.Id);
        map.Delete<ArticleRetracted>(e => e.Data.Id);
    }
}
```

The document is an ordinary Fisher document implementing `IVectorized<TId>` — `Id`, `Content`,
`ContentHash`, `Embedding` — and declaring `VectorIndex(x => x.Embedding, dimensions)`. That is what
makes what the projection writes searchable with nothing else added.

Register it **asynchronously**:

```cs
opts.Projections.Add(new ArticleVectors(provider), ProjectionLifecycle.Async);
```

::: warning
**Async only.** Embedding is a network call per batch, and an inline projection runs inside the
caller's `SaveChangesAsync` — holding SQLite's single write lock open across a round trip to an
embedding API would block every other writer in the process for its duration.
:::

::: tip
**Unchanged content costs no embedding call.** The content is SHA-256 hashed and the stored hash is
compared before the provider is asked, so re-projecting a stream whose text has not moved is a read
and nothing else. Embedding is the metered part.
:::

`Content` is stored as well as hashed, which is what makes re-embedding possible when you change model
or dimension count — without the text, that would mean replaying the stream.

**A delete takes an id selector, always.** There is no overload defaulting to the stream id: a
projection keyed on a payload member would then write rows under one id and delete under another, and
the delete would silently match nothing.

A content selector returning **null** means "this event carries no content" and skips it. A selector
that **throws** is not caught — it faults the shard, which is what the daemon's error handling is for.

## See also

- [Vector Search](/documents/querying/vector-search) — searching what this writes.
- [Hybrid Search](/documents/querying/hybrid-search) — fusing it with the full-text leg.
