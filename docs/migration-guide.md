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

## What is not here, and will not be

| | Why |
| :--- | :--- |
| **A message bus** | The [side-effect seam](/events/projections/side-effects) exists; delivery is a bus integration's job here as on both siblings. |
| **Table partitioning** | SQLite has no partition functions or schemes. |
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

A common and reasonable setup — an integration suite with no server at all, against production code
that runs on PostgreSQL or SQL Server. Just test the SQLite-specific behaviours above against the store
you actually deploy on. See [Integration Testing](/testing/integration).
