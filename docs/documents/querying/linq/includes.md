# Including Related Documents

`Include()` fetches the documents a query's rows point at, in the same call:

<!-- snippet: sample_include_a_related_document -->
<a id='snippet-sample_include_a_related_document'></a>
```cs
var boats = new List<Boat>();

var anglers = await session.Query<IncludedAngler>()
    .Where(x => x.Region == "Shire")
    .Include(x => x.BoatId, boats)
    .ToListAsync();
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/include_samples.cs#L48-L55' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_include_a_related_document' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`anglers` comes back as usual, and `boats` holds each distinct boat those anglers reference. An
`Include` may sit anywhere in the chain — before or after the `Where` — and means the same thing
either way.

## How it works, and why

Fisher resolves an include with **a second `SELECT`**, run on the session's own connection once the
query's rows have been materialized. It does not join.

Marten builds a temporary table of the parent identities and joins the included tables to it, and on
Postgres that is the right design: every extra statement is another network round trip, so
collapsing several reads into one is worth a good deal of machinery. Fisher is embedded and
in-process against a file. There is no round trip to amortise — the second statement costs a prepare
and a b-tree seek — so the temp table would buy nothing and cost the statement builder a whole
second shape to maintain.

Two consequences worth knowing:

- **The identities are read from the loaded documents**, by running your `idSource` lambda over them
  in memory. That lambda is therefore not restricted to what Fisher can translate to SQL. It is also
  why `Include()` cannot follow a `Select`, a `GroupBy` or a join — see
  [what is refused](#what-is-refused).
- **Two statements are two reads.** Inside a transaction, or against SQLite's WAL snapshot, they see
  the same data. Without one, a concurrent writer can land between them.

Identities larger than 500 are chunked across several statements, so an include over a large result
set stays well inside SQLite's parameter limit.

## The two directions

### The parent holds the related document's id

The common case: `Angler.BoatId` holds a `Boat.Id`.

```cs
// a callback per related document
.Include<Angler, Boat>(x => x.BoatId, boat => boats.Add(boat))

// an IList
.Include(x => x.BoatId, boats)

// a dictionary keyed by the related document's own identity
var byId = new Dictionary<Guid, Boat>();
.Include(x => x.BoatId, byId)
```

A **collection** member fans out, so one `Include` can pull in every element:

```cs
.Include(x => x.FavouriteBoatIds, boats)
```

### The related documents point back at the parent

Pass an `idMapping` naming the member of the related document to match. Unlike the id source, this
one becomes SQL, so it has to be a member Fisher can resolve to a column.

```cs
// every catch belonging to the anglers the query returned, flat
.Include(x => x.Id, (Catch c) => c.AnglerId, catches)
```

Or grouped by the angler they belong to:

<!-- snippet: sample_include_grouped_by_the_mapping_member -->
<a id='snippet-sample_include_grouped_by_the_mapping_member'></a>
```cs
var byAngler = new Dictionary<Guid, List<AnglersCatch>>();

var anglers = await session.Query<IncludedAngler>()
    .Include(x => x.Id, (AnglersCatch c) => c.AnglerId, byAngler)
    .ToListAsync();
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/include_samples.cs#L62-L68' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_include_grouped_by_the_mapping_member' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A key with no related documents is **absent** from the dictionary rather than present with an empty
list.

The same shape reads the other way round — the query returns the parents and the include fetches the
children that point at them:

<!-- snippet: sample_include_the_documents_pointing_back -->
<a id='snippet-sample_include_the_documents_pointing_back'></a>
```cs
var crew = new List<Crew>();

await session.Query<Boat>()
    .Where(x => x.Name == "Brandywine Belle")
    .Include(x => x.Id, (Crew c) => c.BoatId, crew)
    .ToListAsync();
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/include_samples.cs#L99-L106' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_include_the_documents_pointing_back' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Filters

Every overload takes an optional trailing predicate over the included type:

<!-- snippet: sample_include_with_a_filter -->
<a id='snippet-sample_include_with_a_filter'></a>
```cs
await session.Query<IncludedAngler>()
    .Include(x => x.BoatId, boats, b => b.Berth == "Hobbiton")
    .ToListAsync();
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/include_samples.cs#L90-L94' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_include_with_a_filter' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The related documents are read through `session.Query<T>()`, so the tenancy, soft-delete and
hierarchy filters apply to them exactly as they apply to any other query. A soft-deleted related
document is not included.

## Several at once

Includes compose; each resolves as its own statement.

<!-- snippet: sample_include_several_at_once -->
<a id='snippet-sample_include_several_at_once'></a>
```cs
await session.Query<IncludedAngler>()
    .Include(x => x.BoatId, boats)
    .Include(x => x.Id, (AnglersCatch c) => c.AnglerId, catches)
    .ToListAsync();
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/include_samples.cs#L78-L83' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_include_several_at_once' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Which terminals resolve includes

`ToListAsync`, `FirstAsync` / `FirstOrDefaultAsync` / `SingleAsync` / `SingleOrDefaultAsync`,
`LastAsync` / `LastOrDefaultAsync`, `ToPagedListAsync` and `ToCursorPageAsync`.

The include follows the rows the terminal actually **returned**, not the rows the query matched — so
`Take(1).Include(...)` includes the one row's relations and nothing else.

## What is refused

Each of these throws a `BadLinqExpressionException` naming what went wrong, rather than leaving the
destination silently empty — an unpopulated list is indistinguishable from a query that legitimately
matched nothing, which is exactly why none of these is allowed to pass quietly.

| Combination | Why |
| :--- | :--- |
| `Include()` with `Select` or `GroupBy` | The rows are values or group aggregates, not the documents the id source was written against |
| `Include()` with `Join` / `GroupJoin` | A joined row is the projected shape, not a document of one type |
| `Include()` with `CountAsync` / `LongCountAsync` / `AnyAsync` | Answers without materializing a document, so there is nothing to read identities from |
| `Include()` with `SumAsync` / `MinAsync` / `MaxAsync` / `AverageAsync` | Same |
| `Include()` with a JSON read or a JSON keyset page | Same — the rows are stored JSON, not documents |
| An id source whose values cannot be held by the matched member | `in (@p0)` against a mistyped parameter is valid SQL that returns nothing |
| A dictionary whose key type is not the related document's identity type | Caught at the `Include` call rather than at execution |

`ToPagedListAsync` is deliberately not on that list: its total is a second statement, but its items
come back through the ordinary document read and the includes resolve against the page.

## Not supported

- **No `Include()` on `LoadAsync`, on a batched query, or on the event store.** It is a LINQ operator
  and nothing else.
- **No dictionary-of-list in the identity direction.** Keying by the related document's own identity
  can only ever produce one-element lists; use the mapping direction, which is what that shape is
  for.
- **No nested includes.** An `Include` on the related documents of an `Include` is not expressible;
  run a second query.
