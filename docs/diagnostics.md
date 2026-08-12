# Diagnostics and Instrumentation

## Tracing

Fisher publishes an `ActivitySource` named **`Fisher`**, with spans around `SaveChangesAsync`, a LINQ
execution and a document load.

```cs
builder.Services.AddOpenTelemetry().WithTracing(tracing =>
{
    tracing.AddSource("Fisher");
});
```

### The instinct that tracing is for network calls is backwards here

An embedded store has no network calls, which sounds like it has nothing to trace. But SQLite
**serialises writers per file**, so the interesting question about a slow Fisher call is almost always
*how long it waited for the write lock* — and a request that spent its time queued behind another
writer is otherwise indistinguishable from one that was simply slow.

**The retry event is the point.** It is recorded from the resilience pipeline against the enclosing
span rather than as a span of its own, because a retry is the same operation happening again.

### What is and is not covered

| | |
| :--- | :--- |
| `SaveChangesAsync` | The whole commit, including a failed one — marked `Error` |
| A LINQ execution | Building and executing the statement |
| A document load | The read |
| A retry | An **event** on the enclosing span |

::: tip
A query's span covers building and executing the statement, **not materializing the rows**. Every
terminal reads rows after the reader is returned, so covering materialization would mean a span per
terminal — and the question the span exists to answer is entirely inside the boundary drawn.
:::

The commit span's counts are tagged **after** everything that can add to the unit of work has run —
listeners, inline projections — so they describe what was written rather than what had been asked for
when the span opened.

::: tip
Fisher instruments inside the session rather than through a decorator. Polecat's tracing decorator
means re-implementing every member of `IDocumentSession` as a pass-through, a cost that grows with
every feature added to the interface.
:::

### Two things learned writing the tests

::: warning
**A contended write does not reach the retry the way you would expect.** The wait at `BEGIN IMMEDIATE`
comes from the connection string's `Default Timeout` and nowhere else — not `SessionOptions.Timeout`,
which bounds a command, and not `PRAGMA busy_timeout`, which does not cover it. And `Default Timeout=0`
means *no limit*, not "do not wait".

So a contended save either sits for the full wait and succeeds with no retry, or fails while the
connection is still being opened — since opening one applies the PRAGMAs, and `journal_mode` wants the
write lock.
:::

::: warning
**An `ActivityListener` is process-wide**, and test collections run in parallel. A test asserting
`Single(...)` over recorded spans is green alone and red in a full suite. Filter by a tag the test's
own store sets.
:::

## The event store tooling surface

`DocumentStore` implements `IEventStore` **explicitly**, so none of a tooling-only surface lands on the
store's own public API. Cast to reach it:

```cs
var eventStore = (IEventStore)store;

var streams = await eventStore.GetRecentStreamsAsync(…);
var metadata = await eventStore.GetStreamMetadataAsync(streamId);
```

::: warning
`IEventStore`, `IEventStore<,>` and `ISubscriptionRunner<>` are deliberately **not** on `IDocumentStore`.
Re-exposing one through it would undo the point of implementing them explicitly.
:::

::: tip
A [secondary store's](/configuration/multiple-stores) marker proxy is *not* an `IEventStore` —
`DispatchProxy` implements only the interfaces it was asked for. The `IEventStore` registration
therefore reaches through the proxy to the real store, so a secondary store is still visible to a
monitoring console.
:::

## The document tooling surface

`IDocumentStoreUsageSource`, `IDocumentStoreDiagnostics` and projection step-through, also implemented
explicitly.

```cs
var diagnostics = (IDocumentStoreDiagnostics)store;
var page = await diagnostics.QueryDocumentsAsync("Order", …);
```

Several things in that surface are worth knowing:

- **The usage sweep forces the mappings into existence.** A mapping is created lazily on first use, so
  a store that has opened no session has none — exactly the state a console sees on a fresh boot.
- **`PartitioningStrategy` is reported as null rather than omitted.** SQLite has no table partitioning,
  so the field has a value — *none* — rather than being unknown.
- **A DDL failure is reported as a SQL comment, not thrown.** One bad mapping should not take the whole
  store's description with it.
- **`QueryDocumentsAsync` is hand-built SQL, and a fourth caller of the three implicit filters.** It
  cannot go through `Query<T>()`: a console names its type as a *string* and filters on *columns* that
  are not document members. Each filter is composed from the one place that owns it rather than
  re-spelled.
- **A table that does not exist reports an empty page**, because SQLite resolves a table name at
  prepare time and a count against a never-created table fails before any guard could run.
- **A sub-class name resolves to its base's mapping plus a `doc_type` filter**, since a registered
  sub-class has no mapping of its own.
- **A console's id is converted through the mapping's identity type before it is bound**, because
  `fi_doc_*.id` holds the lowercase canonical Guid form under a case-sensitive collation.

## Projection step-through

Replay a stream one event at a time and capture the aggregate at each step:

```cs
var timeline = await ((IDocumentStoreDiagnostics)store).ReplayProjectionAsync<Order>(streamId, token);
```

::: warning
**Each step copies the aggregate**, and this is the one thing Polecat's equivalent does not do.
JasperFx's aggregation mutates the aggregate in place, so a timeline built from live references shows
the *final* state at every step — the single thing a step-through exists not to do.

The copy goes through the store's own serializer, which also makes each captured state exactly what
would have been persisted.
:::

A step's exception is **recorded on the step rather than thrown**, or the first bad event would hide
every step after it. An unknown event type is skipped, following the stream reads' policy rather than
the daemon's — a console may be pointed at a store holding types this deployment does not know.

## Event store statistics

```cs
var stats = await store.Advanced.FetchEventStoreStatisticsAsync();
```

::: tip
`EventSequenceNumber` can exceed `EventCount`, because archiving, compacting or deleting events leaves
the sequence where it was. The gap between the two numbers is the count of events that once existed and
no longer do. See [Event Storage](/events/storage#statistics).
:::

## Health checks

```cs
builder.Services.AddHealthChecks().AddFisherHighWaterHealthCheck();
```

See [ASP.NET Core Integration](/documents/aspnetcore#health-check) — including why it exists, and why
the extended progression `heartbeat` column cannot answer the question it answers.

## Seeing the SQL

```cs
var sql = session.ToSql(session.Query<User>().Where(x => x.Internal));
```

Parameter *names*, not values, so the text is readable rather than executable. It is the cheapest way
to check that an implicit filter is actually present.

## Logging

Fisher logs through `ILogger` where a host supplies one — most notably the WAL warning at daemon
startup, which is the only place an operator would otherwise see it.
