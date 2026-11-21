# Migration Guide

Fisher's API mirrors [Marten](https://martendb.io)'s and
[Polecat](https://polecat.jasperfx.net)'s, so most code ports by changing a registration line and a
connection string. This page is about the parts that do not.

## Registration

```cs
// Marten
builder.Services.AddMarten(opts => opts.Connection("Host=…"));

// Polecat
builder.Services.AddPolecat(opts => opts.Connection("Server=…"));

// Fisher
builder.Services.AddFisher(opts => opts.Connection("Data Source=app.db"));
```

## Name changes

| Marten / Polecat | Fisher |
| :--- | :--- |
| `AddMarten` / `AddPolecat` | `AddFisher` |
| `IConfigureMarten` / `IConfigurePolecat` | `IConfigureFisher` |
| `ConfigureMarten` / `ConfigurePolecat` | `ConfigureFisher` |
| `AddMartenStore<T>` / `AddPolecatStore<T>` | `AddFisherStore<T>` |
| `DocumentMetadata` (the read result) | `StoredDocumentMetadata` |
| `IChangeSet.Deleted` is `IEnumerable<IDeletion>` | `IEnumerable<IDocumentDeletion>` |

::: tip
The last two are collision avoidance rather than preference. Fisher already has a `DocumentMetadata`
one namespace away doing the opposite job, and `Weasel.Storage.IDeletion` is already in scope as the
storage *operation* that deletes. In both cases the members are unchanged, so a body ports; only a
declaration naming the type has to be edited.
:::

## Schemas become prefixes

```cs
opts.DatabaseSchemaName = "reporting";   // reporting_fi_doc_order, not reporting.fi_doc_order
```

SQLite has no schemas. Nothing renders as qualified SQL, and the prefix is what isolates two logical
stores in one file.

## Marten features Fisher does not have

These are absent rather than different, so a ported file naming one will not compile. They are listed
here because **Fisher's parity work was measured against Polecat, not Marten** — a feature Marten has
and Polecat does not was invisible to that tracking, which is exactly the set below and exactly the set
a migrating Marten user hits first.

| Marten | Status on Fisher | Instead |
| :--- | :--- | :--- |
| Compiled queries — `ICompiledQuery<T>`, `ICompiledListQuery<T>`, `ICompiledQuery<TDoc,TOut>` | **Absent, and decided** — see [fisher#195](https://github.com/JasperFx/fisher/issues/195) for the measurement. | Nothing. Building the SQL is 4–11% of an ordinary Fisher query and 22.5% of the cheapest one it can run; the work is real but the absolute saving is ~2 µs. A filter-shape query plan cache would collect nearly all of it with no public API, which is the move that comes first. |
| Child-collection LINQ | **Partly landed** ([fisher#166](https://github.com/JasperFx/fisher/issues/166)). | See the [operators page](/documents/querying/linq/operators) — the shipped and missing halves are below. |

### Child collections: what ships and what does not

Querying *into* a document's own collection members works over correlated `json_each` sub-queries:

- `Contains(value)` over **scalar** element collections — strings, numbers, Guids, enums, bools.
- `Any()`, and `Any(c => …)` with a predicate over a complex element's own members, or over a scalar
  element itself (`x.Numbers.Any(n => n > 3)`, `x.Tags.Any(t => t.StartsWith("a"))`).
- `All(c => …)`, vacuously true over an absent, empty or JSON-null collection.
- `Count()` compared to a value in either operand order, the `.Count` property, an array's `.Length`,
  and `Count(c => …)`.
- Nesting — `x.Stops.Any(s => s.Cargo.Contains("fuel"))` — one alias deeper per level.
- `SelectMany(x => x.Children)` over values, enums, strings and child documents, with `Where`, ordering,
  paging, `Distinct` over values and a `Select` after it (fisher#641).

Still missing against Marten, and refused by name rather than answered wrongly:

- **Dictionary members.** `IDictionary<,>` and `IReadOnlyDictionary<,>` are excluded outright, so
  querying a dictionary member is not expressible at all.
- **Some shapes around `SelectMany` over a child collection** (fisher#641 added the operator): a second
  `SelectMany`, `Distinct` over child documents or after a result selector, ordering after a result selector
  or a `Select`, and `LastAsync` or streaming over it. See the
  [operators page](/documents/querying/linq/operators#selectmany-over-a-collection).
- **`Select` projecting a child collection.**
- A string method on a null member (`!x.Name.StartsWith("a")`) follows **SQL null semantics**, since
  C# would throw: the row matches neither the method nor its negation. A comparison answers as C#
  does — `x.N != 3` and `!(x.N > 3)` are true when `x.N` is null.

::: tip
The one to check in ported code is the last bullet, because it compiles either way. The rest are
`BadLinqExpressionException` at the call, naming the operator — Fisher's LINQ surface refuses rather
than falling back to client-side evaluation, which is the invariant, not the size of the surface.
:::

### A note on string ordering

Marten's string-named ordering — `OrderBy(string property, StringComparer)` — exists **only on its
batched queryable**, not on `IQueryable`, so it is a narrower difference than it looks. Fisher's
batched query takes a lambda instead (`Query<T>(session => session.Query<T>().OrderBy(x => x.Name))`),
which expresses the same thing with the member checked at compile time.

## What is not here, and will not be

Unlike the list above, these are settled decisions rather than unbuilt features.

| | Why |
| :--- | :--- |
| **A message bus** | The [side-effect seam](/events/projections/side-effects) exists; delivery is a bus integration's job here as on both siblings. |
| **Table partitioning** | SQLite has no partition functions or schemes. So `PartitionOn`, `MultiTenantedWithPartitioning`, `SoftDeletedWithPartitioning*` and `DoNotPartition` have nothing to mean. |
| **Row-level security** | `UseRowLevelSecurity` / `DisableRowLevelSecurity` are PostgreSQL policies. SQLite has no such concept; isolate with [database-per-tenant](/configuration/multitenancy#database-per-tenant), which is a file per tenant. |
| **GIN indexes over the JSON body** | `GinIndexJsonData` and its member form are PostgreSQL's. SQLite indexes an [expression](/documents/indexing/indexes) instead, which is cheaper and needs no column. |
| **`UniqueIndexType` / `TenancyScope` / `IsConcurrent` / index sort order and casing** | Every one of them describes a *computed column* and a PostgreSQL index. A Fisher index is an expression index and a duplicated field is a `VIRTUAL` generated column that cannot drift, so there is nothing for `Computed` vs `DuplicatedField` to choose between, no direction worth naming, and no casing to apply — SQLite's default collation is case-sensitive and the [string operators](/documents/querying/linq/strings) are ordinal to match. |
| **`PropertySearching`, `DdlTemplate`, `StructuralTyped`, per-type `DatabaseSchemaName`** | Not SQLite concepts. `DatabaseSchemaName` is store-wide here and folds into the table prefix. |
| **`UseIdentityKey`** | A database-assigned identity would need the write path to read the id back rather than assign it client-side. [`IdStrategy`](/documents/identity#supplying-the-identity-strategy) is the seam for a custom strategy; a database-assigned one is a different write path. |
| **`DaemonMode.HotCold`** | Leader election across nodes means several processes sharing one file. |
| **Newtonsoft.Json** | System.Text.Json only. |
| **`CreatedSince` / `CreatedBefore`** | There is no `created_at` column unless you enable one; answering from `last_modified` would be a different question. |
| **A binary event serializer** | The [seam exists](/events/storage#binary-event-bodies); choosing an encoding is your decision. |

## Behaviour that differs

These are the ones that compile and then behave differently, so they are worth checking in ported code.

### The exclusive append methods fail rather than wait

::: danger
`AppendExclusive`, `FetchForExclusiveWriting` and `WriteExclusivelyToAggregate` are the **optimistic**
methods on Fisher. A competing session gets `EventStreamUnexpectedMaxEventIdException` instead of
waiting its turn.

The safety property is unchanged — the version guard still runs inside the write transaction. Code
that relied on *waiting* needs a retry.
:::

See [Appending Events](/events/appending#the-exclusive-methods-fail-where-the-siblings-wait).

### `Include()` is an extension method, not a builder

Fisher [includes related documents](/documents/querying/linq/includes), and covers the same three
plan kinds Marten does — a callback or `IList`, a dictionary keyed by the related identity, and a
dictionary of lists grouped by a mapping member — in both join directions, with an optional filter on
each. What differs is the call shape, so **a ported line needs editing even though the feature is
there**:

```cs
// Marten
query.Include<Boat>(boats).On(x => x.BoatId);
query.Include<Catch>(x => x.Id, catches).On(x => x.Id, c => c.AnglerId);

// Fisher
query.Include(x => x.BoatId, boats);
query.Include(x => x.Id, (Catch c) => c.AnglerId, catches);
```

There is no `IMartenQueryable`-equivalent interface to hang members off, so there is no fluent
`.On(…)` builder; the id source, the optional id mapping and the destination are all arguments of one
extension method. The filter is a trailing optional argument rather than a separate overload.

Two behavioural differences to plan for. Fisher resolves an include with a **second statement** rather
than Marten's temp-table join, because an embedded store has no round trip to amortise — so the reads
are not atomic with each other unless you wrap them in a transaction. And an `Include` combined with a
`Select`, a `GroupBy`, a join, or a terminal that returns no documents is **refused by name** rather
than silently leaving the destination empty.

### Full-text search is FTS5, and relevance is `OrderByRelevance()`

Fisher has [full-text search](/documents/querying/linq/full-text) over SQLite's FTS5, with all six of
Marten's operators — `Search`, `PlainTextSearch`, `PhraseSearch`, `WebStyleSearch`, `PrefixSearch`
and `NgramSearch` — plus `[FullTextIndex]` and `Schema.For<T>().FullTextIndex(...)`. Four things
differ from Marten, and all but the third are what a ported line needs edited for:

- **No `regConfig` argument.** Marten's overloads take a PostgreSQL text-search configuration name;
  FTS5 has no equivalent, and its nearest relative — the tokenizer — is fixed on the *index* rather
  than chosen per query. So the second argument is gone and
  `FullTextIndex(FullTextTokenizer.Porter, …)` is where the choice is made.
- **One index per document type.** Marten permits several and carries
  `AmbiguousFullTextIndexException` for a search that cannot tell which one it meant; here a search
  operator names no index, so with one there is nothing to disambiguate. A second declaration is
  refused — put every searchable member in the one.
- **`NgramSearch` needs a `Trigram` index and the word operators refuse one.** Marten reaches ngram
  search through a separate index type and has the same requirement; what is different is that Fisher
  refuses the mismatch by name in both directions rather than returning nothing.
- **Relevance is `OrderByRelevance()`, not `OrderByTextRank(term, function)`.** FTS5's `bm25()`
  reads its match from the query's own `MATCH`, so Fisher's ordering repeats neither the term nor the
  search function. Column weights are a query argument (`OrderByRelevance(10.0, 1.0)`, in index
  declaration order) rather than Marten's `WeightedFullTextIndex` at declaration time, and
  `OrderByNgramRank` has no counterpart because `bm25()` ranks a `Trigram` index like any other.
  `Snippet()` and `Highlight(column)` project FTS5's `snippet()` and `highlight()` inside a `Select`.
  See [Full-Text Search](/documents/querying/linq/full-text).

The index itself is an external-content FTS5 table kept in step by database triggers, so it survives
writes that never went through Fisher — and it is created and populated by the ordinary schema
migration, including on a store that already holds documents.

### Vector search shares the contracts, not the index

Fisher has [vector search](/documents/querying/vector-search),
[vector projections](/events/projections/vector) and [hybrid search](/documents/querying/hybrid-search)
on [Marten.PgVector](https://martendb.io/documents/pgvector)'s API shape, and the types a ported line
names — `IEmbeddingProvider`, `DistanceFunction`, `VectorMatch<T>` — are the store-neutral ones in
`JasperFx.Events.Vectors` that all three stores share. What a port has to edit:

- **No `UsePgVector()`, and no HNSW index.** Marten.PgVector's
  `opts.VectorIndex<T>(x => x.Embedding, dimensions, distance, m, efConstruction)` declares an HNSW
  index that schema migrations track, and one index serves one metric. Fisher deliberately has none:
  `Schema.For<T>().VectorIndex(x => x.Embedding, dimensions)` or `[VectorIndex(dimensions)]` declares
  the member, and the search is brute force — every row's distance computed in full — so `m` and
  `efConstruction` have nowhere to go, and one declaration serves whichever metric a call names. See
  [why there is no side table](/documents/querying/vector-search#how-it-works-and-why-there-is-no-side-table).
- **The embedding provider is already shared, from Marten 9.36.** Marten.PgVector's projection and
  search run on `JasperFx.Events.Vectors.IEmbeddingProvider`, which returns `ReadOnlyMemory<float>[]`,
  so a provider written against it ports unchanged. Older Marten.PgVector had its own
  `IEmbeddingProvider` returning `Pgvector.Vector[]`; it survives only as an `[Obsolete]` compatibility
  type, and a provider still implementing it needs its return type changed, because Fisher takes only
  the shared contract.
- **`VectorProjection<TDoc, TId>` writes a document, not a table.** The document implements
  `IVectorized<TId>` and declares its own vector index, and it is searched with `VectorSearchAsync`
  like any other — there is no `VectorProjectionSearchAsync`. `TId` is any identity Fisher stores,
  where Marten's is `Guid` only.
- **A delete always takes an id selector.** Marten.PgVector's `Delete<TEvent>()` falls back to the
  stream id when no selector is given; `map.Delete<TEvent>(e => …)` here has no such overload, so a
  line relying on that default gains `e => e.StreamId`.
- **Selectors take the event envelope.** Marten.PgVector's `Map` and `Delete` selectors receive the
  event's data (`e => e.Text`); Fisher's receive `IEvent<TEvent>` (`e => e.Data.Text`), so the
  stream id and metadata are in reach without a second overload.
- **Register it `Async`.** Nothing refuses `Inline`, but an inline projection would hold SQLite's one
  write lock across the model call. The embedding commits in the daemon's batch transaction, not on a
  connection of its own.

[Hybrid search](/documents/querying/hybrid-search) needs both a full-text and a vector index on the
type, and refuses a type with only one rather than degrading to the leg it has.

### String searching is ordinal and case-sensitive

Fisher uses `instr`/`substr` rather than `LIKE`, because SQLite's `LIKE` is case-**insensitive** for
ASCII while `=` is case-**sensitive** — a LIKE-based `Contains` would contradict `==` in the same
`Where` clause. See [Searching on String Fields](/documents/querying/linq/strings).

### Timestamp equality is normalised

A document's `DateTimeOffset` member is compared through SQLite's date parser, folding the offset into
UTC at millisecond precision. Two spellings of one instant compare equal — which costs sub-millisecond
discrimination on `==`, as it does on the siblings.

### Numeric revisions follow Marten, not Polecat

::: warning
The guard requires the supplied revision to be **strictly greater** than the stored one. Polecat
diverged to an equality rule; ported Polecat code that re-stores an instance carrying its current
revision gets a `ConcurrencyException` here.
:::

See [Optimistic Concurrency](/documents/concurrency#numeric-revisions).

### Batched queries are not a performance feature

They exist for API parity. SQLite is embedded, so there are no round trips to collapse. The ordering
property still holds. See [Batched Queries](/documents/querying/batched-queries).

### `QueryForNonStaleData` waits for the whole store

Where Polecat waits for the projections feeding the queried type. Stricter, not weaker.

### An event-raising projection actually raises events

::: warning
Polecat **no-ops** the three members involved, so an event-raising projection there drops its events
with no signal. Fisher appends them, inside the batch's transaction, with the optimistic guard.
:::

### `MaybeArchived()` lives in `Fisher.Events.Archiving`

[Archived events](/events/archiving#reading-archived-events) are left out of event reads unless a
predicate opts in, as on Marten, and `MaybeArchived()` and `x.IsArchived` work the same way.
`LoadAsync(eventId)` returns an archived event here and on Marten, where Polecat's leaves it out. One read
differs from Marten: `FetchForWriting` always folds the stream here and on Polecat, so an archived stream's
aggregate is null even for an Inline aggregate, where Marten can hand back the stored document.

### Inner-side join predicates are applied

::: warning
Polecat silently drops the inner query's own predicates, so
`GroupJoin(session.Query<Catch>().Where(...))` there returns rows the caller excluded. Fisher applies
them — which means a ported query may return **fewer** rows here, correctly.
:::

## Things that are cheaper here

Worth revisiting when you port, because the workaround you carried may no longer be needed:

- **[Duplicated fields](/documents/indexing/duplicated-fields)** are generated columns — no backfill
  when added to a populated table, and a [patch](/documents/partial-updates-patching) has nothing to
  refresh.
- **[Indexes](/documents/indexing/indexes)** are expression indexes — no computed column, no
  `JSON_VALUE` index.
- **[Patching](/documents/partial-updates-patching)** needs no server-side function installed.
- **[JSON reads](/documents/querying/query-json)** are byte-exact and save the whole round trip rather
  than a fraction of it.
- **[Joins](/documents/querying/linq/joins)** are plain SQL and there is no round trip they are
  competing against.
- **[Database-per-tenant](/configuration/multitenancy#database-per-tenant)** is a file per tenant, and
  it buys **concurrency** as well as isolation.

## Things to plan for

- **One writer per file.** If several processes or many concurrent writers need the same data, either
  split across files or use Marten or Polecat.
- **[Transaction participants](/documents/transaction-participants)** and
  [`QueueSqlCommand`](/documents/querying/raw-sql) stop being conveniences and become the way you write
  your own tables alongside Fisher's without contending with yourself.
- **[WAL](/configuration/sqlite#wal)** must stay on if you run the async daemon.
- **[Guid casing](/documents/identity#guids-are-lowercase-canonical-text)** matters in any SQL you
  write by hand.

## Testing against Fisher, deploying elsewhere

An integration suite with no server at all is an appealing idea, and it is a **narrow** one. Read the
scope before adopting it.

::: danger
**Fisher is not a test double for Marten or Polecat.** The section above is a list of behaviours that
compile and then differ — which is the exact failure mode of a stand-in: the suite compiles, goes
green, and production behaves another way.

Several of those divergences fall squarely in the territory integration tests exist to cover.
Concurrency under contention differs (the exclusive methods fail where the siblings wait), the
numeric-revision guard differs from Polecat's, `QueryForNonStaleData` is stricter here — so a real
staleness race in the deployed store can stay hidden — and both ordinal string comparison and
inner-side join predicates change which rows come back.
:::

What a suite on Fisher **can** honestly cover for an application deployed elsewhere: wiring,
registration, projection shape, and that your handlers and endpoints hold together. What it **cannot**
cover: concurrency, ordering, collation, and staleness semantics. Those have to be tested against the
store you deploy on, and the hard part is that knowing in advance which of your tests are sensitive to
them is not obvious.

Fisher's own positioning is **SQLite in production** — edge, embedded, desktop, single-node. That is
where it is a first-class answer rather than a compromise.

See [Integration Testing](/testing/integration).

## Upgrading from Fisher 1 to Fisher 2

Fisher 2.0 is the Fisher half of the Critter Stack's .NET 11 release train: JasperFx 3, Weasel 10.0,
Marten 10.0, Polecat 6.0, Fisher 2.0 and Wolverine 7.0 move together. Each entry below says what changes,
what it does to an existing database file, and how to opt out.
[The last entry](#what-a-fisher-1-file-gains-on-its-first-migration) lists every table and column a Fisher 1 file
gains.

### Target frameworks: `net10.0` and `net11.0`

Fisher, `Fisher.AspNetCore`, `Fisher.EntityFrameworkCore` and the new `Fisher.Aspire.Hosting` now target `net10.0;net11.0`. **`net9.0` is
dropped**, so an application still on .NET 9 stays on Fisher 1.x. Nothing about the on-disk format depends
on the target framework.

JasperFx 3's `net11.0` build depends on the .NET 11 `Microsoft.Extensions.*` packages, so an application
targeting `net11.0` has to reference the matching 11.x hosting and dependency-injection packages, or
restore fails with `NU1605`.

Fisher's `net11.0` build uses the .NET 11 GA packages (11.0.0): `Microsoft.Data.Sqlite`, the
`Microsoft.Extensions.*` hosting and dependency-injection packages, and for `Fisher.EntityFrameworkCore`,
`Microsoft.EntityFrameworkCore.Relational`. The `net10.0` build keeps the 10.x versions
(`Microsoft.Data.Sqlite` 10.0.15, EF Core 10.0.0). No .NET 11 preview or release candidate is left in
Fisher's dependencies. An application on `net11.0` that references EF Core itself moves to EF Core 11 with
`Fisher.EntityFrameworkCore`.

Test projects move with the stack: xunit.v3 4.x on Microsoft Testing Platform 2 (Fisher's own suite did,
which is why the old "stay on MTP 1.x" note is gone).

### Runtime-async on `net11.0`

Landed in 2.0.0-alpha.2. Fisher's `net11.0` assemblies are compiled with the C# compiler's **runtime-async** feature (fisher#550), as
JasperFx 3.0's and Wolverine 7.0's are. Async methods are run by the runtime rather than by a compiler-built
state machine, which allocates much less. Wolverine measured 38% to 84% fewer allocations depending on how often a
call suspends. The `net10.0` build is unchanged, because that runtime cannot load runtime-async code.

There is nothing to migrate and no table or column is involved. What can look different is async code at runtime:
stack traces, debugger stepping, and APM agents or profilers that hook async state machines. To build Fisher from
source without it, pass `-p:FisherRuntimeAsync=false` and clean `bin/` and `obj/` first, since changing the switch
does not trigger a recompile. The setting governs only the assemblies it compiles; your own projects decide for
themselves.

### JasperFx 3 and Weasel 10

Fisher 2 requires JasperFx 3.12.1 and Weasel.Sqlite / Weasel.Storage 10.0 (10.0.0-rc.1 at the moment). An
application referencing either directly has to move them together; mixing a 2.x JasperFx with Fisher 2 does not
restore. Weasel.Sqlite 10 depends on `Microsoft.Data.Sqlite` 10.0.15 on `net10.0` and 11.0.0 on `net11.0`, the same
versions Fisher uses, so the two never pull different SQLite providers into one application.

Two things in JasperFx 3.12 can show up in an application:

- **A natural key has to be an `int`, `long` or `string`**, or a strong-typed identifier wrapping one. Anything
  else, a `Guid` included, is now refused when the projection is registered, with an
  `InvalidProjectionException` naming the aggregate and the member. Fisher 1 accepted a `Guid` key and stored it
  as text. Use the key's string form, or wrap it in a strong-typed identifier over a `string`.
- **A failed rebuild of a composite projection throws.** It used to return normally with the composite's shard
  paused or stopped.

### JasperFx 3.9.0: culture-independent names, and bare `case` labels in `Evolve()`

JasperFx 3.9.0 stops lowercasing names with the machine's culture ([details](https://shared-libs.jasperfx.net/migration-guide#key-changes-in-3-9-0)).
Nothing changes for Fisher: its event type names, tag table names and duplicated-field columns come from Fisher's
own snake casing, which never depended on the culture. Separately, `Evolve()` methods may now use bare `case Foo:`
labels, and the source generator bundled in the Fisher package picks them up.

### The native SQLite build

**No change of provider.** Fisher stays on the `SQLitePCLRaw.bundle_e_sqlite3` 3.0.x bundle that
`Microsoft.Data.Sqlite` brings (3.0.5, the version Microsoft.Data.Sqlite 11 also depends on). It does not
switch to SQLite3MC and does not cap `Microsoft.Data.Sqlite`. Fisher needs SQLite 3.44 or newer for the
json1 functions, and its tests assert that floor against the bundle in use.

An application that wants encryption at rest references `SQLite3MC.PCLRaw.provider` itself, which is built
on the same SQLitePCLRaw 3.0.x line. See [SQLite configuration](/configuration/sqlite#the-native-sqlite-build).
There is no schema effect.

### Event-store defaults: `GateSideEffectsBehindPriorVersion`

jasperfx#1075 turns `GateSideEffectsBehindPriorVersion` **on by default for every event store** in
JasperFx 3.0, Fisher included. Fisher does not set it itself, so the default arrives with the JasperFx
bump.

**What it does.** It matters only when you raise a projection's `Version` (a blue/green or rolling deploy of
a changed projection). The new version replays the event store to build its own documents, in its own table
(see [a new projection version gets its own table](#a-new-projection-version-gets-its-own-table)). With the gate on, it
does that with **side effects suppressed** up to where the previous version's progression stopped, and
publishes messages and raises events only for events the previous version never processed. In Fisher 1 the
new version re-published everything it replayed, so every message the old version had already sent went out a
second time.

**Effect on an existing database: none.** No table or column changes. The gate reads the previous
version's row in `fi_event_progression`, which the version-qualified shard name (`Quest:V2:All` beside
`Quest:V1:All`) already keeps apart. A projection whose `Version` never changes, or that starts from the
present, behaves exactly as before.

**Opting out** is per projection, on the projection's options:

```cs
public class QuestProjection : SingleStreamProjection<Quest, Guid>
{
    public QuestProjection()
    {
        Version = 2;

        // Fisher 1's behaviour: re-publish side effects over the whole replay.
        GateSideEffectsBehindPriorVersion = false;
    }
}
```

`side_effect_gate_behind_prior_version` in Fisher's own suite runs both cases against a real file and the
real daemon. `RestoreV1Defaults()` (below) turns the gate off for every registered projection at once.

### DCB boundaries see tombstones, archiving and `AssignTagWhere`

Landed in fisher#635, the port of marten#6035 and marten#6078. A boundary from `FetchForWritingByTags` now
fails its commit with `DcbConcurrencyException` when, after it read, a tombstone, an archive or un-archive,
an `AssignTagWhere`, or a compaction changed the events it read. In Fisher 1 those went through, because the
check only looked for events appended after the boundary's last sequence. See
[Dynamic Consistency Boundary](/events/dcb#writing-against-a-boundary).

**Effect on an existing file.** A store with a registered tag type gets two new tables,
`fi_dcb_tag_version` and `fi_dcb_tag_version_sequence`. Under `AutoCreate.All` or `CreateOrUpdate` they are
created the first time the store touches the event tables. Under `AutoCreate.None`, apply the migration
first, or the first `FetchForWritingByTags` fails with `no such table`. Nothing is back-filled: a tag with
no row reads as version 0, and Fisher checks that the events a boundary read are still there whenever one
of its tags has no row.

### A new projection version gets its own table

Landed in fisher#638, matching Marten. An aggregation projection (single or multi stream) with `Version`
above 1 now writes its documents to a table with the version on the end: `fi_doc_quest_2` for version 2 of a
projection of `Quest`. Version 1 keeps `fi_doc_quest`. The members of a composite projection take the
composite's `Version`, so bumping the composite gives every member new progression rows and new tables.

In Fisher 1 the new version wrote into the old version's table. An aggregation loaded the document the old
version had built and applied every event to it again, so a projection that counted events ended up with
double the count. A composite bump was worse: the members kept their own version, reused the old progression
rows, and the composite stopped with `ProgressionProgressOutOfOrderException`.

**Effect on an existing file.** A projection that has `Version` above 1 reads and writes a new, empty table
after the upgrade. Its progression row (`Quest:V2:All`) is already where Fisher 1 left it, so the daemon
will not fill the new table on its own: rebuild the projection (`RebuildProjectionAsync`) once after the
upgrade. The old table stays where it is with its data; drop it yourself once nothing reads it. Under `AutoCreate.None`, apply the migration first to create the new table. Code that reads the table by
name in raw SQL needs the new name. Projections at version 1, and documents that no projection writes, keep
their tables.

### Store defaults that follow Marten 10, and `RestoreV1Defaults()`

Fisher 2.0 takes the default changes Marten 10 made in marten#5812 wherever Fisher has the same option
(fisher#621). `StoreOptions.RestoreV1Defaults()` puts every one of them back, so call it first if you
want Fisher 1's behaviour and add your own settings after it:

```cs
var store = DocumentStore.For(opts =>
{
    opts.ConnectionString = connectionString;
    opts.RestoreV1Defaults();

    // then anything you want on again, for example
    opts.Events.EnableCorrelationId = true;
});
```

::: danger Inline projections now run their side effects
**`Events.EnableSideEffectsOnInlineProjections` is on by default. An application whose inline projections
publish messages or raise events starts doing so on upgrade, inside `SaveChangesAsync`.** In Fisher 1
those side effects were dropped for inline projections. Messages go to `Events.MessageOutbox`, which drops
them unless you configured one. Before you upgrade, check every inline projection that publishes a
message or raises an event, and set `opts.Events.EnableSideEffectsOnInlineProjections = false` (or call
`RestoreV1Defaults()`) to keep Fisher 1's behaviour.
:::

| Setting | Fisher 1 | Fisher 2 | Effect on an existing file | Restored by `RestoreV1Defaults()` |
| :--- | :--- | :--- | :--- | :--- |
| `Events.EnableCorrelationId`, `EnableCausationId`, `EnableHeaders`, `EnableUserName` | off | **on** | Adds `correlation_id`, `causation_id`, `headers` and `user_name` to `fi_events` (they are now created whatever the flags say, see below). Existing events read `null`. | yes, the flags; the columns stay |
| `Events.EnableExtendedProgressionTracking` (fisher#664, marten#5763) | off | **on** | Adds the extended columns to `fi_event_progression` (also created whatever the flag says, see below). The daemon writes `heartbeat`, `agent_status`, `pause_reason`, `running_on_node`, the `failure_*` columns and `parked_count`. | yes, the flag; the columns stay |
| Document `correlation_id`, `causation_id`, `last_modified_by`, `headers` columns | off unless a type enabled them | **on for every document type** | Adds the four columns to every document table. Existing rows read `null`. Turn one off for one type with `Metadata(m => m.Headers.Enabled = false)`. `created_at` stays opt-in. | yes |
| `Events.EnableSideEffectsOnInlineProjections` | off | **on** | None on the file. See the warning above. | yes |
| The `fisher.events.appended` counter | after `OpenTelemetry.TrackEventCounters()` | **on** | None. With nothing listening it costs one check per commit. | yes |
| System.Text.Json `AllowOutOfOrderMetadataProperties` on Fisher's `Serializer` | `false` | **`true`** | None. Reads accept `$type` anywhere in the object. | yes, applied when the store is built |
| `GateSideEffectsBehindPriorVersion` (JasperFx 3.0, above) | off | **on** | None. | yes, on every registered projection, applied when the store is built |
| `Projections.Errors.ApplyErrors` and `SerializationErrors` ([parking](/events/projections/async-daemon#parking-a-failing-identity), [JasperFx docs](https://shared-libs.jasperfx.net/events/parked-identities), fisher#652) | skip the event and dead-letter it | **park the failing identity, or pause the shard where the projection can't park** | Adds `fi_parked_identities`, and `parked_count` to the progression table. An event projection, a flat table, a composite or a multi-stream projection grouped by a delegate now **pauses** on a bad event instead of skipping it. | yes |
| A projection batch that fails transiently, such as `SQLITE_BUSY` ([JasperFx 3.11.0](https://shared-libs.jasperfx.net/migration-guide#key-changes-in-3-11-0), jasperfx#1256) | the shard pauses, or under skip goes round again without limit when several identities fail at once | **the daemon builds the batch again through the store's Polly pipeline (five retries by default), under every error policy; when the retries run out, the shard pauses with `ShardFailure.IsTransient` true** | None. A transient failure never parks or skips anything. | no. `ConfigurePolly` changes the retries, for every command Fisher runs |

**The new columns are an ordinary migration.** Under `AutoCreate.All` or `CreateOrUpdate`, Fisher adds them
to existing tables the first time the store touches them. Under `AutoCreate.None`, apply the migration
first (`ApplyAllConfiguredChangesToDatabaseAsync`, `db-apply` or the generated DDL). Otherwise the first
write fails with `table … has no column named correlation_id`. Adding a column does not rewrite the table.

**The cheap event-store columns are created whatever the flags say** (fisher#664, following marten#5765).
`fi_events` always gets `correlation_id`, `causation_id`, `headers` and `user_name`, and
`fi_event_progression` always gets the extended tracking columns. The flags only decide whether Fisher writes
and reads them. When the flags decided the schema, two stores over one file with different settings dropped
each other's columns every time either one migrated. `RestoreV1Defaults()` puts the flags back, not the old
table shape, so a Fisher 1 file gains these columns on its first migration either way. Every column is
nullable, so it's a plain `ALTER TABLE ADD COLUMN`. Under `AutoCreate.None`, apply the migration before you
deploy, as for the other new columns.

What marten#5812 changed that has **no Fisher equivalent**, so nothing changes here:

- Event skipping in projections and subscriptions. Fisher has no `is_skipped` column or skipping filter, and no
  high-water skip tracking: SQLite has one writer, so the high-water mark never has a gap to skip.
- `FetchForWriting` metrics. Fisher has no such instruments.
- Indexes that start with the tenant id for conjoined documents. A conjoined Fisher table's primary key
  already starts with `tenant_id`, and Fisher has no option to lead secondary indexes with it.
- Archived-stream partitioning and strict stream identity enforcement. SQLite has no partitioning.
- The sticky connection lifetime option and the store-wide "all documents multi-tenanted with
  partitioning" policy. Fisher never had either.

### Types that move to shared packages

Landed in 2.0.0-alpha.1 (fisher#465 via #538, fisher#530 via #544), and `StreamLockedException` in 2.0.0-alpha.2
(fisher#530 via #556). The Fisher 1 names are gone.

| Fisher 1 | Fisher 2 |
| :--- | :--- |
| `Fisher.BulkInsertMode` | the shared `Weasel.Core.BulkInsertMode`. Its new `OverwriteIfVersionMatches` value is refused with `NotSupportedException` |
| `Fisher.Patching.IPatchExpression<T>` | `JasperFx.Events.IPatchExpression<T>` (three defaults change, see [patching](#patching-follows-the-shared-contract)) |
| `Fisher.Pagination.IPagedList<T>`, what `ToPagedListAsync` returns | `JasperFx.Pagination.IPagedList<T>` |
| `Fisher.Linq.BadLinqExpressionException` | `JasperFx.BadLinqExpressionException`, thrown directly |
| `Fisher.Exceptions.*` types that derived from a `JasperFx.Events` exception: `ExistingStreamIdCollisionException`, `NonExistentStreamException`, `UnknownEventTypeException`, `EventDeserializationFailureException`, `ArchivedStreamException`, `DuplicateNaturalKeyException`, `StreamLockedException` | the `JasperFx.Events` type itself, thrown directly |

That includes `StreamLockedException`: `Fisher.Exceptions.StreamLockedException` is gone and Fisher throws
`JasperFx.Events.StreamLockedException`, whose `StreamIds` lists every stream in the unit of work (jasperfx#1172).
The mechanical change is a `using` and, for the exceptions, a `catch` of the JasperFx type; members are unchanged.
No table or column is involved.

The messages are JasperFx's now, so a test or a log query that matches Fisher 1's wording needs updating. For
example, the archived-stream message ends "or start a new stream", and the unknown event type message starts
"Unknown event type name alias".

`DisabledTenantException` and `InvalidValueTypeException` stay Fisher's. Two tag-type registration errors change
with the shared tag registry (fisher#574): a tag type Fisher cannot infer throws JasperFx's
`InvalidOperationException` instead of `InvalidValueTypeException`, and registering a tag type a second time with a
different inner type is refused, where Fisher 1 kept the first registration.

### Patching follows the shared contract

Landed in 2.0.0-alpha.1 (fisher#465 via #538). `Patch<T>()` returns the shared `IPatchExpression<T>`, which adds
dictionary operations, element-predicate `Remove`/`AppendIfNotExists`/`InsertIfNotExists`, `Set` and `Delete`
relative to a parent member, and `float` `Increment`. Three calls that compile either way do something different:

- `Insert` with no index **appends**. Fisher 1 inserted at the front. Pass `index: 0` for the old behaviour.
- `Remove` takes the **first** match. Fisher 1 removed every match. Pass `RemoveAction.RemoveAll`.
- `Duplicate` copies the source member's stored value and takes no value of its own. Fisher 1's
  `Duplicate(source, value, destinations)` is `Set(source, value).Duplicate(source, destinations)`.

The nullable `Increment` overloads are extension methods in `Fisher.Patching`. See
[the operations](/documents/partial-updates-patching#the-operations). No table or column is involved.

### Appending: the expected version is the version after the append

Landed in 2.0.0-alpha.1 (fisher#464 via #537), with the shared `EventOperationsBase`.

::: warning
`Append(streamId, expectedVersion, events)` now reads `expectedVersion` as the version of the stream **after** the
append, as Marten and Polecat do. Fisher 1 read it as the version **before**. Add the number of events you append
to the value you were passing. A value smaller than the number of events throws `ArgumentOutOfRangeException` at
the call, so `Append(id, 0, e)` on a new stream is now `Append(id, 1, e)`. `FetchForWriting` and
`AppendOptimistic` are unaffected.
:::

The same change makes every append and start-stream overload check its arguments the same way:

- `Guid.Empty` and an empty string key are refused as a stream identity.
- `StartStream` with no events is refused, and so is `StartStream` with a non-zero expected version.
- Under conjoined tenancy, appending to a stream that holds another tenant's events is refused.
- A session header no longer overwrites an event's own header of the same name.

See [optimistic concurrency](/events/appending#optimistic-concurrency). No table or column is involved.

### Union-typed properties and events (`net11.0` only)

Landed in 2.0.0-alpha.1 (fisher#466 via #536): LINQ over C# union-typed members through `json_extract` / `json_type`, with a type test and case access, and clear refusals for the shapes SQLite cannot answer. Unions are a C# 15 /
.NET 11 language feature, so this is available on the `net11.0` build only. It adds a capability and
changes nothing for an application that does not use unions.

### Hosting: `AddFisher(name)` and .NET Aspire

Landed in 2.0.0-alpha.1 (fisher#468 via #541) as the new `Fisher.Aspire.Hosting` package: an AppHost-side
`AddFisher(name, path)` that publishes the file path as a connection-string resource, `WithVolume` for the
file's volume, and [Aspire](/configuration/aspire) documents the `CommunityToolkit` `AddSqliteConnection`
path, OpenTelemetry registration and the high-water health check. The existing
`AddFisher(...)` registration in this guide is unchanged.

### Stream compaction waits for async projections and subscriptions

Landed in fisher#634, the port of marten#6050. `CompactStreamAsync()` now throws an `InvalidOperationException`
while an asynchronous projection or subscription has not processed the events it would delete, including one that
has never run. Nothing is compacted; compact again once they caught up. A single stream projection of the compacted
type is not checked, since it reads the `Compacted<T>` snapshot. In Fisher 1 those events were deleted anyway, and a
multi-stream projection or a subscription that was behind never saw them ([compacting](/events/rewriting#stream-compacting)).

### `RebuildSingleStreamAsync` goes through the daemon, or refuses

Landed in fisher#629, on JasperFx 3.6.0's per-identity rebuild (jasperfx#1201). This only matters for a document
written by an **asynchronous** single-stream projection. Inline and live projections rebuild one stream the way
they always did.

In Fisher 1, `Advanced.RebuildSingleStreamAsync<T>(id)` always aggregated the stream and stored the result on its
own connection, whether or not a daemon was running that projection. If the daemon was running, the two could
race. Now Fisher picks one of three paths:

1. A daemon this process hosts (`AddAsyncDaemon()`) is running the projection: the rebuild goes through it,
   `IProjectionDaemon.RebuildIdentityAsync`, and commits with the shard's next batch. Nothing races.
2. The projection's progression row was written by a running agent in the last 30 seconds, and Fisher cannot
   reach that agent: the call throws `IdentityRebuildRefusedException` with
   `Reason == IdentityRebuildRefusal.ShardLiveOnAnotherNode`. That is a daemon you built yourself with
   `BuildProjectionDaemonAsync()`, one running as Wolverine agents, or a second process on the same file.
   Rebuild through that daemon instead:

   ```cs
   await daemon.RebuildIdentityAsync("Trip", tenantId: null, identity: tripId, token);
   ```

3. Otherwise the stream is rebuilt offline, as before. That is only safe when no daemon is running the
   projection.

The refusal is the behaviour change. Code that called `RebuildSingleStreamAsync` next to a hand-built daemon
used to succeed and now throws. Without extended progression tracking, an idle shard stops rewriting its row, so
it reads as stopped after 30 seconds and the offline path runs. With it on, a stopped agent writes `Stopped` and
the offline path runs straight away.

A projection whose documents live in storage registered through `Projections.StorageProviders` (EF Core) cannot
be rebuilt one identity at a time. The daemon refuses with `IdentityRebuildRefusal.NotSupportedByStore`. Use
`RebuildProjectionAsync`.

### LINQ: computed ordering keys, indexes, `LeftJoin` and `RightJoin`

Landed in fisher#666, the port of marten#5957, #5968, #5949 and #5910. Fisher 1 refused all of these by name;
Fisher 2 translates them. See [ordering by a computed key](/documents/querying/linq/operators#ordering-by-a-computed-key),
[indexing](/documents/querying/linq/operators#indexing-into-arrays-and-lists) and [joins](/documents/querying/linq/joins).

Two shapes that Fisher 1 answered without complaint are refused now, because the answer was wrong:

- A cast around an ordering key that changes the value, such as `OrderBy(x => (byte)x.Number)` or
  `ThenBy(x => (int)x.Price)`. Fisher 1 ordered by the member underneath the cast.
- A key comparer on `Join` or `GroupJoin`. Fisher 1 ignored it and compared the stored keys.

### LINQ answers that were wrong in Fisher 1

Landed in 2.0.0-alpha.3 (fisher#588 to #615, from a sweep of Marten's LINQ fixes for anything that also bites
Fisher). Each of these compiled on Fisher 1 and returned a different answer from LINQ to Objects, with nothing to
signal it. Fisher 2 matches LINQ to Objects or refuses the shape by name with a `BadLinqExpressionException`. None of
them changes a table, but a query in this list can return different rows after the upgrade:

- **A second `OrderBy` re-sorts.** `OrderBy(a).OrderBy(b)` orders by `b`, then `a`. Fisher 1 rendered `order by a, b`.
- **An operator after `Take` or `Skip` applies to the page**, for documents and for `QueryRawEvents()`,
  `QueryStreamStates()` and `QueryEventData<T>()`. Fisher 1 ran a later `Where` or `OrderBy` before the `LIMIT`.
- **`Distinct()` and an identity `OrderBy` after a computed `Select`** act on the computed value.
  `Select(x => x.Number % 3).Distinct()` returns three rows, not one per document.
- **A static member or a captured local is a value.** Fisher 1 read it as a JSON path when its name matched a
  document member. On a join, an ordering key that is a constant is dropped rather than refused.
- **Comparing two document members** (`x.A < x.B`) translates. Fisher 1 failed with an internal
  `InvalidOperationException`.
- **A null member compares the way C# compares it.** `x.N != 3` and `!(x.N > 3)` are true when `x.N` is null. A
  string method on a null member still follows SQL null semantics, see
  [child collections](#child-collections-what-ships-and-what-does-not).
- **Decimals are exact.** Comparisons, ordering, `Distinct`, `GroupBy`, join keys and the aggregates read a
  `decimal` as the text System.Text.Json wrote and compare or compute it as a `decimal`, not as SQLite's REAL.
  Two decimals that differ past the fifteenth digit are two values. An `OrderBy` over a `decimal` sorts by an exact
  key now, so an index on the member no longer serves it. `Where` comparisons still use the index.
- **Arithmetic on a `decimal` or `float` member** in a predicate: a member plus or minus constants is folded in
  .NET in `decimal`, and every other shape is refused. Fisher 1 computed it in SQLite's `double`.
- **A `float` comparison** binds the `double` its stored text reads back as. `x.Weight == 0.3f` matched nothing on
  Fisher 1 and matches the row that stored `0.3f` now.
- **A `[DuplicateField]` collection member** works with `Contains()`, `Any()` and `Count()` again.

See [Decimals](/documents/querying/linq/operators#decimals) and
[`decimal` is a JSON number](/configuration/sqlite#decimal-is-a-json-number-and-it-compares-as-one).

### Parking, health checks and the `projections` CLI

Parking a failing identity is the 2.0 default (the [defaults table](#store-defaults-that-follow-marten-10-and-restorev1defaults)
has the row). Around it (fisher#652, #655, #658, #660, #661, #667, #671):

- **`AddFisherParkedIdentitiesHealthCheck()`** in `Fisher.AspNetCore` is new. It is Degraded while anything is
  parked and lists the parked identities, ten per projection, with the natural key when the aggregate has one. It
  is Unhealthy while a shard is paused at the parking cap, which it can only see with extended progression
  tracking on, the 2.0 default. See [health checks](/diagnostics#health-checks).
- **`ShardState` carries more under extended tracking.** `AllProjectionProgress` fills `ParkedCount`,
  `LastHeartbeat`, `AgentStatus`, `PauseReason`, `RunningOnNode` and `Failure`. Fisher 1 filled only the name,
  sequence and last update.
- **A shard that fails before its first commit gets a progression row.** A pause, or the parking cap on the first
  batch, writes a row at the shard's start position with the failure. Fisher 1 wrote nothing, so code that reads
  `fi_event_progression` directly can now see a row for a shard that has never committed.
- **A daemon built without a logger logs through the host's `ILoggerFactory`.** That covers
  `BuildProjectionDaemonAsync()` and Wolverine-managed distribution. Fisher 1 used `NullLogger` there, so parking
  and the parking cap were not logged. A store built with `DocumentStore.For(...)` still has no logger factory.
- **`EventStoreUsage.SubjectUri` is the store's uri**, as on Marten and Polecat. Fisher 1 put the database uri
  there, so the JasperFx `projections parked` and floor commands found no Fisher store. `FetchParkedIdentitiesAsync`
  fills `ParkedIdentity.NaturalKey`, so `projections parked` shows it.

### Smaller behaviour changes

- **The order of the commit hooks.** `SaveChangesAsync` runs the message outbox's `BeforeCommitAsync` after every
  transaction participant, Fisher's own and the shared ones, as the projection batch already did. Fisher 1 ran it
  first (fisher#581). The session also implements the shared contract's `StoreObjects`,
  `AddTransactionParticipant(IDbTransactionParticipant)` and `AddSaveListener`, which threw
  `NotSupportedException` in Fisher 1 when Wolverine called them.
- **`AdvancedSql` with an `object` result** returns the provider's own value: `long`, `double`, `string`, `byte[]`
  or null. Fisher 1 deserialized it as JSON, which gave a `JsonElement` for a number and threw for plain text
  (fisher#613).
- **Waiting for the write lock** goes through a busy handler that retries every 1 ms until
  `PragmaSettings.BusyTimeout`, in place of SQLite's default back-off, when the provider is the bundled `e_sqlite3`.
  The deadline and the `SQLITE_BUSY` at the end of it are unchanged (fisher#547). See [the busy handler](/configuration/sqlite#the-busy-handler).
- **Startup.** One store activator replaces the three startup hosted services. `SeedInitialDataOnStartup()` no
  longer has to come after `ApplyAllDatabaseChangesOnStartup()`: seeding always runs after the schema step.
- **One `ActivitySource` and one `Meter` per store.** Fisher 1 built a second of each for the event store, so a
  listener on `Fisher` heard both.
- **Generated Guid ids** come from JasperFx's `SequentialGuid` in PostgreSQL `uuid` order, not from
  `Guid.CreateVersion7()`. Both are time-ordered, and existing ids are untouched.
- **Batched queries.** `EventsExist` and `FetchForWritingByTags` in a batch fault their own item when the read
  throws, and a cancelled `Execute` throws one `OperationCanceledException` instead of an aggregate (fisher#633).
- **`AssignTagWhere` under conjoined tenancy** only tags the session's tenant's events (fisher#632).
- **`FetchLatest` of an Inline immutable aggregate after a commit** on an identity or dirty-tracked session returns
  the new snapshot. Fisher 1 returned the one from before the commit (fisher#645).

### What a Fisher 1 file gains on its first migration

All of it is additive: new tables, and nullable columns added with `ALTER TABLE ADD COLUMN`. Under `AutoCreate.All`
or `CreateOrUpdate` Fisher applies it the first time the store touches the tables. Under `AutoCreate.None`, apply
the migration before you deploy.

| Table or column | When |
| :--- | :--- |
| `correlation_id`, `causation_id`, `headers`, `user_name` on `fi_events` | always |
| The extended tracking columns and `parked_count` on `fi_event_progression` | always |
| `correlation_id`, `causation_id`, `last_modified_by`, `headers` on every document table | unless `RestoreV1Defaults()` or the type turns them off |
| `fi_parked_identities` | always, with the event tables |
| `fi_event_range_marks` | always, with the event tables |
| `fi_dcb_tag_version`, `fi_dcb_tag_version_sequence` | when a tag type is registered |
| `fi_slice_index_<projection>` | for a projection with `UseSliceIndex()` |
| `fi_doc_<type>_<version>` | for an aggregation projection with `Version` above 1 (rebuild it once, see [above](#a-new-projection-version-gets-its-own-table)) |
