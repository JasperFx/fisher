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
| Full-text search — `Search`, `PlainTextSearch`, `PhraseSearch`, `WebStyleSearch`, `NgramSearch`, and full-text indexes | **Absent.** Nothing is built over SQLite's FTS5. | `Contains` / `StartsWith` / `EndsWith`, which are ordinal and case-sensitive (see below) and can be served by a [declared index](/documents/indexing/indexes). Not a substitute for ranked search. |
| Child-collection LINQ | **Partly landed** ([fisher#166](https://github.com/JasperFx/fisher/issues/166)). | See the [operators page](/documents/querying/linq/operators) — the shipped and missing halves are below. |

### Child collections: what ships and what does not

Querying *into* a document's own collection members works over correlated `json_each` sub-queries:

- `Contains(value)` over **scalar** element collections — strings, numbers, Guids, enums, bools.
- `Any()`, and `Any(c => …)` with a predicate over a complex element's own members.
- `All(c => …)`, vacuously true over an absent, empty or JSON-null collection.
- `Count()` compared to a value in either operand order, the `.Count` property, an array's `.Length`,
  and `Count(c => …)`.
- Nesting — `x.Stops.Any(s => s.Cargo.Contains("fuel"))` — one alias deeper per level.

Still missing against Marten, and refused by name rather than answered wrongly:

- **Dictionary members.** `IDictionary<,>` and `IReadOnlyDictionary<,>` are excluded outright, so
  querying a dictionary member is not expressible at all.
- **`SelectMany` over a child collection**, and therefore anything that flattens elements into the
  result — including ordering or projecting by a child element's member.
- **`Select` projecting a child collection.**
- A predicate inside `Any` / `All` / `Count` follows **SQL null semantics**, not C#'s: a predicate that
  evaluates to NULL is not satisfied.

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
