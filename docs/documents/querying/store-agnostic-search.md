# Store-Agnostic Search

[`VectorSearchAsync`](/documents/querying/vector-search) and
[`HybridSearchAsync`](/documents/querying/hybrid-search) are extension methods on Fisher's own
`IQuerySession`, so code that holds a session as the shared `IDocumentReadOperations` contract —
a library, a retrieval service, anything meant to compile against more than one Critter Stack
store — could not reach them at all.

`IDocumentReadOperations.Search` is the route. It hands back an `IDocumentSearchOperations`
(`JasperFx.Events.Vectors`), which every store that has similarity search implements.

<!-- snippet: sample_store_agnostic_search_service -->
<a id='snippet-sample_store_agnostic_search_service'></a>
```cs
// No Fisher types anywhere. The same class compiles against Marten and Polecat.
public class Retriever
{
    private readonly IDocumentReadOperations _session;
    private readonly IEmbeddingProvider _embeddings;

    public Retriever(IDocumentReadOperations session, IEmbeddingProvider embeddings)
    {
        _session = session;
        _embeddings = embeddings;
    }

    public async Task<IReadOnlyList<Memory>> RecallAsync(string question, CancellationToken token)
    {
        var query = await _embeddings.GenerateEmbeddingAsync(question, token);

        return await _session.Search.VectorSearchAsync<Memory>(
            x => x.Embedding, query, limit: 5, token: token);
    }
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/document_search_samples.cs#L12-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_store_agnostic_search_service' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## The two methods, and the two extensions over them

`IDocumentSearchOperations` declares exactly two members — the scored ones — and the document-only
forms are shared extensions over them, so an implementer writes two methods and the projection to
documents cannot differ between stores.

| | Returns |
| :--- | :--- |
| `VectorSearchWithScoresAsync<T>` | `VectorMatch<T>` — smaller distance is closer |
| `HybridSearchWithScoresAsync<T>` | `HybridMatch<T>` — larger score is better |
| `VectorSearchAsync<T>` | the documents |
| `HybridSearchAsync<T>` | the documents |

<!-- snippet: sample_store_agnostic_search_scored -->
<a id='snippet-sample_store_agnostic_search_scored'></a>
```cs
var matches = await session.Search.VectorSearchWithScoresAsync<Memory>(
    x => x.Embedding, query, limit: 5);

var confident = matches.Where(m => m.Distance < 0.3).Select(m => m.Document);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/document_search_samples.cs#L39-L44' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_store_agnostic_search_scored' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Every argument is the one the native spelling takes, including the
[`filter` predicate](/documents/querying/vector-search#filtering):

<!-- snippet: sample_store_agnostic_search_hybrid -->
<a id='snippet-sample_store_agnostic_search_hybrid'></a>
```cs
var hits = await session.Search.HybridSearchAsync<Memory>(
    x => x.Embedding, text, query, limit: 10,
    options: new HybridSearchOptions(K: 30),
    filter: x => !x.Archived);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/document_search_samples.cs#L51-L56' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_store_agnostic_search_hybrid' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Why it is behind an accessor

::: warning
**`Search` is a property, and the contract's members are deliberately *not* on the session itself.**
Fisher's search entry points are already named `VectorSearchWithScoresAsync` and
`HybridSearchWithScoresAsync`, as extension methods. Members of those names on `IQuerySession` would
win overload resolution over the extensions at **every existing call site** — silently, with no
compile error, and with the store's own tenant, soft-delete and hierarchy predicates decided by
whichever body won. One hop makes that collision impossible to have.
:::

Fisher's implementation forwards to those same extension methods rather than building a second
statement, for the reason [fisher#285](https://github.com/JasperFx/fisher/issues/285) records: a
second path to the same search is a second place for the implicit predicates to be forgotten, and a
consumer reaching search through the store-agnostic surface is the one least able to notice.

So everything the native call carries, this carries:

- conjoined tenancy, the `doc_type` hierarchy discriminator and soft deletes
- the vector index's declared metric, and every refusal — no declared index, the wrong member, a
  query vector of the wrong length
- hybrid search's refusal of a type that declares only one of the two indexes

## What a store without search does

The member carries a **throwing** default, naming the implementing type, rather than answering
empty. A store with no similarity search leaves it in place; a store that has it returns an object.
Empty would be indistinguishable from a corpus that matched nothing, which is the wrong direction
for a capability check to fail in.

## See also

- [Vector Search](/documents/querying/vector-search) — the native spelling, and how an index is
  declared.
- [Hybrid Search](/documents/querying/hybrid-search) — the fused form.
- [Vector Projections](/events/projections/vector) — producing the embedding from an event stream.
