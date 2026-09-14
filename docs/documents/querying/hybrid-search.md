# Hybrid Search

Keyword and embedding search fail in **different directions** — full text misses a paraphrase that
shares no tokens, and vector search misses an exact identifier, a product code, or a rare proper noun
the model never learned. `HybridSearchAsync` fuses the two legs so each covers the other's hole.

It needs both indexes declared on the type:

<!-- snippet: sample_hybrid_declare_both_indexes -->
<a id='snippet-sample_hybrid_declare_both_indexes'></a>
```cs
opts.Schema.For<SupportTicket>()
    .FullTextIndex(x => x.Subject, x => x.Body)
    .VectorIndex(x => x.Embedding, dimensions: 768);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/hybrid_search_samples.cs#L23-L27' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_hybrid_declare_both_indexes' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## The call and its scores

<!-- snippet: sample_hybrid_search -->
<a id='snippet-sample_hybrid_search'></a>
```cs
// One string feeds both legs: the full-text leg searches for it, the vector leg for its
// embedding -- from the same model that produced the stored vectors.
var text = "invoice XJ-4417";
var query = await embeddings.GenerateEmbeddingAsync(text);

var hits = await session.HybridSearchAsync<SupportTicket>(
    x => x.Embedding,   // the declared vector member
    text,               // the full-text leg
    query,              // the vector leg
    limit: 10);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/hybrid_search_samples.cs#L32-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_hybrid_search' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The text and the query vector are separate arguments because Fisher never calls a model. The vector
comes from the store-neutral `IEmbeddingProvider` in `JasperFx.Events.Vectors` — the same one
[Vector Search](/documents/querying/vector-search) takes — and it has to come from whatever model
produced the stored vectors.

`HybridSearchAsync` returns the documents, best first. `HybridSearchWithScoresAsync` returns each as a
`HybridMatch<T>` with its fused score:

<!-- snippet: sample_hybrid_search_with_scores -->
<a id='snippet-sample_hybrid_search_with_scores'></a>
```cs
var matches = await session.HybridSearchWithScoresAsync<SupportTicket>(
    x => x.Embedding, text, query, limit: 10);

// Larger is better -- the opposite of a distance. With the default K of 60, first place in
// one leg is worth 1/61 ≈ 0.016, so a floor of 0.03 keeps what both legs ranked near the top.
var agreed = matches.Where(m => m.Score >= 0.03).Select(m => m.Document);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/hybrid_search_samples.cs#L50-L57' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_hybrid_search_with_scores' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

**Larger is better here** — the opposite of `VectorMatch<T>.Distance` and of bm25 — because the score
is a sum of reciprocals that rises with agreement between the legs. The absolute value means little on
its own; what it supports is a floor, or a comparison between results of the same query.

## How the fusion works

Ranking is **reciprocal rank fusion**: `score(d) = Σ 1 / (K + rank(d))` over the legs that found it,
with ranks counted from 1.

::: tip
**RRF rather than a weighted sum of the two scores**, because bm25 and cosine distance are not on a
comparable scale — normalising them means choosing constants that are wrong for somebody's corpus.
RRF uses only each document's *ordinal position*, so the legs need no calibration against each other
and the fusion behaves the same whatever the embedding model or the tokenizer.
:::

- **Each leg is read `CandidateDepth` deep**, which defaults to `max(limit × 4, 50)`. The text leg is
  an ordinary `Query<T>()` with `PlainTextSearch` (or `WebStyleSearch`) ordered by
  `OrderByRelevance()`; the vector leg is `VectorSearchAsync`. The two lists are then fused in memory.
- **`CandidateDepth` has to exceed `limit`, and that is the point.** A document ranked 40th by one leg
  and 1st by the other is exactly the result hybrid search exists to surface; reading only `limit` from
  each leg would never see it.
- **The fusion is over the union**, so a document only one leg found still scores — that is the whole
  idea rather than a tolerance.
- **`K` is the only dial, and there are no per-leg weights.** RRF has nothing to weight but position,
  and weighting one leg would bring back the calibration problem it exists to avoid. Larger `K`
  flattens the difference between ranks; smaller lets the top of each leg dominate. A search where one
  leg should decide is a single-leg search.
- **Ties are broken deterministically** — best rank in either leg, then the identity — so documents
  with equal scores land on the same page on every call.

Both legs run as their own statement and are fused in memory, so the tenant, soft-delete and
hierarchy filters apply — and the vector leg's own refusals (no declared index, wrong member, wrong
query length) carry over unchanged.

One statement was the alternative, and it would mean a join whose plan neither index serves, plus a
second place for those filters to be forgotten.

## Options

<!-- snippet: sample_hybrid_search_options -->
<a id='snippet-sample_hybrid_search_options'></a>
```cs
var hits = await session.HybridSearchAsync<SupportTicket>(
    x => x.Embedding, searchBox, query, limit: 20,
    options: new HybridSearchOptions(
        K: 30,                                  // let the top of each leg count for more
        CandidateDepth: 200,                    // deeper than the default max(20 × 4, 50)
        Distance: DistanceFunction.L2,          // the vector leg's metric, for this call
        TextStyle: HybridTextStyle.WebStyle));  // "quoted phrases", or, -exclusions
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/hybrid_search_samples.cs#L64-L72' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_hybrid_search_options' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

| `HybridSearchOptions` | Default | |
| :--- | :--- | :--- |
| `K` | 60 | RRF's smoothing constant, and at least 1. Larger flattens the difference between ranks |
| `CandidateDepth` | `max(limit × 4, 50)` | How deep each leg is read before fusing |
| `Distance` | the index's | Override the vector leg's metric |
| `TextStyle` | `PlainText` | Or `WebStyle`. Both are safe to hand a search box's raw contents |

`HybridTextStyle` stops at two members because both take raw input. `PlainText` is every word in any
order with no syntax at all; `WebStyle` adds quoted phrases, `or` and a leading `-` to exclude.
`Search`'s raw FTS5 syntax is deliberately absent — it can be malformed, and a malformed query in one
leg of a fused search fails the whole call. Phrase, prefix and ngram search are reachable through
`Query<T>()` and are not what a hybrid search is usually fed.

## What is refused

::: warning
**A type declaring only one of the two indexes is refused, not degraded to the leg it has.** A caller
who asked for hybrid search and silently got keyword search has no way to find out. Declare both
indexes, or call `Query<T>()` with a full-text operator, or `VectorSearchAsync`, for a single-leg
search.
:::

| | Why |
| :--- | :--- |
| A type declaring only one of the two indexes, or neither | `InvalidOperationException` naming the missing index, checked before either leg runs so the message is about hybrid search rather than whichever leg ran first |
| `CandidateDepth` below `limit` | The fusion would have fewer candidates than it is asked to return |
| `K` below 1 | 0 would make the top-ranked document of either leg score infinitely |
| `limit` below 1 | There is nothing to return |
| Anything [`VectorSearchAsync` refuses](/documents/querying/vector-search#what-is-refused) | Carried over from the vector leg rather than restated |
| A `WebStyle` query of only exclusions | Carried over from the [text leg](/documents/querying/linq/full-text#what-is-refused): FTS5's `NOT` narrows a result set rather than negating one |

## See also

- [Full-Text Search](/documents/querying/linq/full-text) — the keyword leg on its own, and the
  operators, tokenizers and relevance ordering behind it.
- [Vector Search](/documents/querying/vector-search) — the embedding leg on its own, and how a vector
  index is declared.
- [Vector Projections](/events/projections/vector) — producing the embedding from an event stream, so
  the document's vector is not something the application maintains by hand.
- [Polecat's Hybrid Search](https://polecat.jasperfx.net/documents/querying/hybrid-search) — the SQL
  Server 2025 sibling of this page.
