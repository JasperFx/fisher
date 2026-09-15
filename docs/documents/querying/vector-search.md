# Vector Search

Fisher searches documents by embedding similarity over a member you declare. Store the vector
the way you store anything else — a `float[]` on the document — and declare it:

<!-- snippet: sample_vector_declare_index -->
<a id='snippet-sample_vector_declare_index'></a>
```cs
opts.Schema.For<Memory>().VectorIndex(x => x.Embedding, dimensions: 768);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/vector_samples.cs#L36-L38' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_vector_declare_index' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/vector_samples.cs#L50-L56' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_vector_search' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The API shape is [Marten.PgVector](https://martendb.io/documents/pgvector)'s, and the types are the
store-neutral ones from `JasperFx.Events.Vectors`: `IEmbeddingProvider` for the model,
`DistanceFunction` for the metric, `VectorMatch<T>` for a scored result. Application code written against one store reads the same
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
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/vector_samples.cs#L63-L68' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_vector_search_with_scores' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Every metric is a **distance**: smaller is closer, on every store. That is what lets one
`ORDER BY` serve all three and a similarity floor be one comparison.

| `DistanceFunction` | Computes | Use for |
| :--- | :--- | :--- |
| `Cosine` (default) | `1 − cos θ`, in `[0, 2]` | text embeddings, which are trained for it and usually unit length |
| `L2` | Euclidean distance | when magnitude carries meaning |
| `InnerProduct` | the **negative** inner product | unit vectors, where it equals cosine and is cheaper |

The index pins the default metric — cosine, unless the declaration names another:

<!-- snippet: sample_vector_declare_index_l2 -->
<a id='snippet-sample_vector_declare_index_l2'></a>
```cs
opts.Schema.For<Memory>().VectorIndex(x => x.Embedding, dimensions: 768, DistanceFunction.L2);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/vector_samples.cs#L43-L45' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_vector_declare_index_l2' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and a call can override it:

<!-- snippet: sample_vector_search_metric -->
<a id='snippet-sample_vector_search_metric'></a>
```cs
var byMagnitude = await session.VectorSearchAsync<Memory>(
    x => x.Embedding, query, limit: 5, distance: DistanceFunction.L2);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/vector_samples.cs#L88-L91' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_vector_search_metric' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Filtering

A predicate narrows the search, and it is applied **before** the limit — so the result is the top-k
of the filtered set rather than the filtered remains of the top-k:

<!-- snippet: sample_vector_search_filter -->
<a id='snippet-sample_vector_search_filter'></a>
```cs
// The predicate is applied BEFORE the limit, so this is the nearest five *handbook*
// memories rather than whichever of the nearest five happen to be handbook ones.
var nearest = await session.VectorSearchAsync<Memory>(
    x => x.Embedding, query, limit: 5,
    filter: x => x.Category == "handbook" && !x.Archived);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/vector_samples.cs#L75-L81' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_vector_search_filter' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

It supports and refuses exactly what `Query<T>().Where(...)` does, because it *is* that: the search
is built as an ordinary statement and the predicate is appended to it.

::: tip
**There is no recall caveat on Fisher, and on an approximate index there would be.** A store whose
vector index is approximate applies the filter to whatever the index scan produced, and that scan
has a bound of its own — pgvector's `hnsw.ef_search` defaults to 40 — so a selective filter can
return fewer than `limit` rows. Fisher scans every row, so a selective filter costs the same as any
other and returns the true top-k.
:::

The filter is **in addition to** the store's own predicates below, never instead of them.

## The predicates that always apply

A vector search carries every implicit filter `Query<T>()` carries, from the same code rather than
from a restatement of it:

| | |
| :--- | :--- |
| Conjoined tenancy | A session opened for one tenant reads that tenant's rows |
| The `doc_type` discriminator | `VectorSearchAsync<SubType>` returns that sub-class, not its siblings |
| Soft deletes | A deleted document is excluded, as it is from every query |

::: warning
**Two of those three were missing until [fisher#285](https://github.com/JasperFx/fisher/issues/285).**
The search used to build its own SQL and restated the soft-delete filter alone, so under
[conjoined tenancy](/documents/multi-tenancy) it ranked and returned another tenant's documents, and a
sub-class search read the whole hierarchy's table. It was silent and asymmetric in the worst way: the
tenant owning most of the corpus saw a correct-looking answer with extras. If you are upgrading from
Fisher 1.10.0 or earlier and use conjoined document tenancy with vector or hybrid search, this is a
correctness fix rather than a feature.
:::

## The attribute

<!-- snippet: sample_vector_attribute -->
<a id='snippet-sample_vector_attribute'></a>
```cs
[VectorIndex(768, Distance = DistanceFunction.Cosine)]
public float[]? Embedding { get; set; }
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/vector_samples.cs#L26-L29' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_vector_attribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`[VectorIndex(dimensions)]` on a member declares it exactly as `Schema.For<T>().VectorIndex(...)`
does. `Distance` is optional and defaults to cosine.

Either form accepts a member of any of these types, and refuses anything else when the store is
configured:

- `float[]` or `double[]`
- `ReadOnlyMemory<float>` (nullable or not) or `Memory<float>`
- `List<float>`, `IList<float>`, `IReadOnlyList<float>` or `IEnumerable<float>`

They all serialize to a JSON array of numbers, which is what the search reads.

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
cached. A document whose embedding is `null` is skipped, not scored. The `WHERE` clause and the
materialization both come from the same machinery `Query<T>()` uses, which is what makes the
implicit predicates above apply without being restated — and what makes the next filter added to
LINQ impossible to miss here.

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
| A `filter` the LINQ provider cannot translate | `BadLinqExpressionException`, exactly as `Query<T>().Where(...)` would raise — the filter is that `Where` |

A **stored** vector whose length differs from the query's fails that row loudly at query time
rather than scoring it wrong — the one check that cannot happen earlier, because the JSON is the
record.

## Hybrid search

Keyword and embedding search fail in **different directions**, and fusing them is what makes recall
usable. See [Hybrid Search](/documents/querying/hybrid-search).

## Embeddings from an event stream

`VectorProjection<TDoc, TId>` produces the vector rather than searching one the application already
put on the document — with content-hash skipping, so unchanged text costs no embedding call. See
[Vector Projections](/events/projections/vector).

## Not yet

- **No approximate index.** See above.

## See also

- [Hybrid Search](/documents/querying/hybrid-search) — fusing this with full-text search.
- [Store-Agnostic Search](/documents/querying/store-agnostic-search) — the same two searches reached
  through `IDocumentReadOperations.Search`, without naming Fisher.
- [Vector Projections](/events/projections/vector) — producing the embedding from an event stream.
- [Marten's pgvector support](https://martendb.io/documents/pgvector) — the PostgreSQL sibling, where
  `VectorIndex<T>` declares an HNSW index — one per metric — that Fisher deliberately does without.
- [Polecat's Vector Search](https://polecat.jasperfx.net/documents/querying/vector-search) — the SQL
  Server 2025 sibling.
