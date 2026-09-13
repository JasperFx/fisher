# Hybrid Search

Keyword and embedding search fail in **different directions** — full text misses a paraphrase that
shares no tokens, and vector search misses an exact identifier, a product code, or a rare proper noun
the model never learned. `HybridSearchAsync` fuses the two legs so each covers the other's hole.

```cs
var hits = await session.HybridSearchAsync<Note>(
    x => x.Embedding,               // the declared vector member
    "invoice XJ-4417",              // the text leg
    await embeddings.Embed("invoice XJ-4417"),
    limit: 10);
```

`HybridSearchWithScoresAsync` returns each document with its fused score. **Larger is better here** —
the opposite of a distance — because the score is a sum of reciprocals that rises with agreement
between the legs.

Ranking is **reciprocal rank fusion**: `score(d) = Σ 1 / (k + rank(d))` over the legs that found it.

::: tip
**RRF rather than a weighted sum of the two scores**, because bm25 and cosine distance are not on a
comparable scale — normalising them means choosing constants that are wrong for somebody's corpus.
RRF uses only each document's *ordinal position*, so the legs need no calibration against each other
and the fusion behaves the same whatever the embedding model or the tokenizer.
:::

| Option | Default | |
| :--- | :--- | :--- |
| `K` | 60 | RRF's smoothing constant. Larger flattens the difference between ranks |
| `CandidateDepth` | `max(limit × 4, 50)` | How deep each leg is read before fusing |
| `Distance` | the index's | Override the vector leg's metric |
| `TextStyle` | `PlainText` | Or `WebStyle`. Both are safe to hand a search box's raw contents |

**`CandidateDepth` has to exceed `limit`, and that is the point.** A document ranked 40th by one leg
and 1st by the other is exactly the result hybrid search exists to surface; reading only `limit` from
each leg would never see it. A depth below `limit` is refused.

The fusion is over the **union**, so a document only one leg found still scores — that is the whole
idea rather than a tolerance.

::: warning
**A type declaring only one of the two indexes is refused, not degraded to the leg it has.** A caller
who asked for hybrid search and silently got keyword search has no way to find out. Declare both
indexes, or call `Query<T>()` with a full-text operator, or `VectorSearchAsync`, for a single-leg
search.
:::

Both legs run as their own statement and are fused in memory, so the tenant, soft-delete and
hierarchy filters apply — and the vector leg's own refusals (no declared index, wrong member, wrong
query length) carry over unchanged.

## See also

- [Full-Text Search](/documents/querying/linq/full-text) — the keyword leg on its own, and the
  operators, tokenizers and relevance ordering behind it.
- [Vector Search](/documents/querying/vector-search) — the embedding leg on its own, and how a vector
  index is declared.
- [Vector Projections](/events/projections/vector) — producing the embedding from an event stream, so
  the document's vector is not something the application maintains by hand.
