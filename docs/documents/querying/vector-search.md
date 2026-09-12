# Vector Search

Fisher searches documents by embedding similarity over a member you declare. Store the vector
the way you store anything else — a `float[]` on the document — and declare it:

<!-- snippet: sample_vector_declare_index -->
<a id='snippet-sample_vector_declare_index'></a>
```cs
opts.Schema.For<Memory>().VectorIndex(x => x.Embedding, dimensions: 768);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/vector_samples.cs#L34-L36' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_vector_declare_index' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

then search it with a query vector from the same model:

<!-- snippet: sample_vector_search -->
<a id='snippet-sample_vector_search'></a>
```cs
// The query vector comes from whatever model produced the stored ones — the
// store-neutral IEmbeddingProvider from JasperFx.Events, here.
var query = await embeddings.GenerateEmbeddingAsync("how does the daemon pick a mode?");

var nearest = await session.VectorSearchAsync<Memory>(x => x.Embedding, query, limit: 5);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/vector_samples.cs#L48-L54' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_vector_search' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The API shape is Marten.PgVector's, and the types are the store-neutral ones from
`JasperFx.Events.Vectors`: `IEmbeddingProvider` for the model, `DistanceFunction` for the metric,
`VectorMatch<T>` for a scored result. Application code written against one store reads the same
against the other.

Fisher never calls a model. Computing the embedding — at write time in your own code, or from an
event stream in a projection — is yours, and `JasperFx.Events.MicrosoftExtensionsAI` adapts any
Microsoft.Extensions.AI generator (OpenAI, Azure OpenAI, Ollama, ONNX) to `IEmbeddingProvider` so
you do not write one by hand.

## Scores

<!-- snippet: sample_vector_search_with_scores -->
<a id='snippet-sample_vector_search_with_scores'></a>
```cs
var matches = await session.VectorSearchWithScoresAsync<Memory>(x => x.Embedding, query, limit: 5);

// Distance is smaller-is-closer under every metric; for cosine it is 1 - similarity.
var confident = matches.Where(m => m.Distance < 0.3).Select(m => m.Document);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/vector_samples.cs#L61-L66' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_vector_search_with_scores' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Every metric is a **distance**: smaller is closer, on every store. That is what lets one
`ORDER BY` serve all three and a similarity floor be one comparison.

| `DistanceFunction` | Computes | Use for |
| :--- | :--- | :--- |
| `Cosine` (default) | `1 − cos θ`, in `[0, 2]` | text embeddings, which are trained for it and usually unit length |
| `L2` | Euclidean distance | when magnitude carries meaning |
| `InnerProduct` | the **negative** inner product | unit vectors, where it equals cosine and is cheaper |

The index pins the default metric; a call can name another:

<!-- snippet: sample_vector_search_metric -->
<a id='snippet-sample_vector_search_metric'></a>
```cs
var byMagnitude = await session.VectorSearchAsync<Memory>(
    x => x.Embedding, query, limit: 5, distance: DistanceFunction.L2);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/vector_samples.cs#L73-L76' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_vector_search_metric' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## The attribute

<!-- snippet: sample_vector_attribute -->
<a id='snippet-sample_vector_attribute'></a>
```cs
[VectorIndex(768, Distance = DistanceFunction.Cosine)]
public float[]? Embedding { get; set; }
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/vector_samples.cs#L24-L27' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_vector_attribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`[VectorIndex(dimensions)]` on a member declares it exactly as `Schema.For<T>().VectorIndex(...)`
does. `Distance` is optional and defaults to cosine.

## How it works, and why there is no side table

The search is one statement on the session's own connection: the document's columns plus
`fi_vector_distance(metric, json_extract(data, '$.embedding'), @query)`, ordered by that distance,
limited. `fi_vector_distance` is an application-defined function Fisher registers on every
connection the store opens — the query vector is bound as a float32 BLOB, the stored one is read
from the JSON, and the metric decides the arithmetic.

**Reading from `data` is the point.** The alternatives were considered and refused:

- A BLOB column kept in step by a trigger, the way the full-text index is, would need the trigger
  to call an application-defined function — and then every writer that is not Fisher (an EF Core
  context sharing the file, the `sqlite3` shell, a restore edited in place) would fail with
  *no such function*.
- A BLOB side table maintained on Fisher's own write path would go silently stale under those
  same writers, which is precisely the failure the full-text design was built to refuse.

A stored embedding that a foreign writer changed ranks by its new value, because nothing is
cached. A document whose embedding is `null` is skipped, not scored. Soft-deleted documents are
excluded as they are from every query. A tenant is its own database, so tenancy costs nothing.

**It is brute force.** The JSON is parsed on every row and the distance computed in full; a
768-float embedding parses in tens of microseconds, so a search is milliseconds at thousands of
rows and under a second at tens of thousands. That is Fisher's scale, and it is the seam a native
index would slot behind if a workload ever outgrows it — `sqlite-vec` is deliberately not taken
on today: it is pre-1.0, has no NuGet package, and would put a native binary per platform behind
a feature that works without one.

## What is refused

| | Why |
| :--- | :--- |
| `VectorSearchAsync` on a type with no declared index | There is nothing to search; the query would otherwise be valid SQL that scans nothing |
| A member that is not the declared one | The search would silently run against the wrong locator |
| A query vector whose length is not the declared `dimensions` | Every stored row would fail, one at a time, inside SQLite |
| Declaring a member that cannot hold a vector (a `string`, an `int`) | Caught when the store is configured, not when the first search returns nothing |
| Declaring the same member twice, or with a dimension count under one | Same |

A **stored** vector whose length differs from the query's fails that row loudly at query time
rather than scoring it wrong — the one check that cannot happen earlier, because the JSON is the
record.

## Not yet

- **No hybrid search** with the full-text index, and no event-sourced vector projection with
  content-hash skipping. Both are on the way as their own pieces.
- **No approximate index.** See above.
