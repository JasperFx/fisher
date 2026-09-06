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

## Metrics

Fisher publishes a `Meter` named **`Fisher`**, matching the `ActivitySource`, so one name subscribes
to both:

```cs
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter("Fisher"));
```

**Everything is opt-in and nothing is created until it is asked for.** A store that opts into nothing
publishes no instruments and pays a null check on the commit path.

```cs
builder.Services.AddFisher(opts =>
{
    opts.ConnectionString = connectionString;

    opts.OpenTelemetry.TrackWriteLockContention();
    opts.OpenTelemetry.TrackEventCounters();
    opts.OpenTelemetry.TrackDocumentCounters();
});
```

| Instrument | Kind | Tags | What it answers |
| :--- | :--- | :--- | :--- |
| `fisher.write_lock.wait` | histogram (ms) | `fisher.store`, `fisher.write_lock.holder` | How long a writer queued for SQLite's one write lock |
| `fisher.write_lock.retries` | counter | `fisher.store`, `exception.type` | How often a `SQLITE_BUSY` was retried rather than waited out |
| `fisher.events.appended` | counter | `fisher.store`, `fisher.event.type`, `fisher.tenant` | Append volume, by event type |
| `fisher.documents.written` | counter | `fisher.store`, `fisher.document.type`, `fisher.document.operation` | Commit shape — inserts, updates, deletions |

`fisher.write_lock.holder` is `session`, `daemon` or `rebuild`. That distinction is what separates
"the application is contended" from "the daemon is starving the application", which look identical from
a session's side alone.

### Why the wait, and not just the retries

::: warning
**A `SQLITE_BUSY` retry counter on its own is the wrong instrument here, and that is a measurement
rather than an opinion.** Fisher's Polly pipeline already emits a `fisher.retry` activity event, so
counting retries is the obvious move. Under the benchmark harness's concurrent-writers scenario that
counter reads **zero** while throughput visibly collapses — a contended writer sits inside
`BEGIN IMMEDIATE` under the connection string's busy timeout and eventually succeeds, never reaching
the retry.
:::

So a dashboard built on retries alone shows a flat line through the exact incident it exists to
diagnose, which is worse than no instrument at all: a flat line reads as *not the database*.
`TrackWriteLockContention()` therefore creates **both** — they are opted into together so neither can
be charted without the other:

- a **rising histogram with no retries** is ordinary contention absorbed by the busy timeout;
- **retries** mean the timeout was exceeded, or the failure was `SQLITE_BUSY_SNAPSHOT`, which the busy
  timeout does not cover at all.

### The counters are not Marten's

Marten's interesting number is connection usage against a pooled remote server. Fisher's is contention
for the one write lock on a file, so the instruments differ:

| Marten | Fisher |
| :--- | :--- |
| `TrackConnections` | **refused** — a Fisher connection is a file handle, not a lease on a scarce server resource. Weasel's `SqliteDataSource` builds a fresh connection per open, and the pooling beneath it is Microsoft.Data.Sqlite's, keyed process-wide by connection string and not attributable to a store. Setting it throws, naming `TrackWriteLockContention()` instead. |
| — | `fisher.write_lock.wait` — no sibling has one, because no sibling serialises every writer on a file |
| `marten.event.append` (`TrackEventCounters`) | `fisher.events.appended`, same shape. It earns its place here for an extra reason: on one file every appending writer is queued behind every other, so append volume charted against the wait separates *more work arrived* from *the same work is now waiting* |
| — | `fisher.documents.written` — the other cause of the wait |
| `ExportCounterOnChangeSets<T>` | same, for a counter specific to your own model |

::: tip
`TrackConnections` is **refused rather than ignored**, following `SessionOptions.IsolationLevel`, which
is carried for parity and refuses exactly one value by name. A knob that silently does nothing is worse
than an error, because the absence of data is indistinguishable from having none to report.
:::

The event and document counters describe a **user session's** unit of work. An async projection commits
through the daemon's batch, which deliberately does not fire session listeners — counting those here
would put the daemon's own work on the same series as the application's.

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

`ToSql` answers about one statement you already have in hand. For *what a session actually ran*, use
the logger below.

## Logging

Fisher logs through `ILogger` where a host supplies one — the WAL warning at daemon startup, and
every statement a session executes.

### The session logger

`AddFisher` attaches a `DefaultFisherLogger` over the container's `ILogger<IDocumentStore>`, so
turning Fisher's SQL on is a log-level change and nothing else:

```json
{ "Logging": { "LogLevel": { "Fisher": "Debug" } } }
```

Every statement then arrives with its duration, and each `SaveChangesAsync` with what it committed:

```
Fisher executed in 0.42 ms, SQL: insert into fi_doc_user (id, data, ...) ...
  @p0: (String)
  @p1: (String)
Fisher committed 3 operations in 1.86 ms — 2 updates, 1 inserts, 0 deletions, 4 events across 1 streams
```

Covered: the write batch (one line per storage operation), a LINQ execution, and a document load —
the same three boundaries the spans are drawn at. A failed statement is logged with its command, and
a failed commit is logged again as a message, because "this statement was refused" and "the whole
unit of work is gone" are different news.

### Parameter values are not logged by default

::: warning
**This is a deliberate divergence from Marten**, which logs `p.Value` for every parameter at `Debug`.
Fisher logs the parameter's **name and the CLR type of the bound value** instead — `@p0: (String)`.
:::

Three things stack up behind it:

- **Fisher already answered this question once, the same way.** `ToSql` renders parameter names and
  not values, so that the text is readable rather than executable. One store should not hold two
  opposite answers to "may Fisher write bound values somewhere a human will read them".
- **What is bound here is the whole document.** A Fisher upsert binds the serialized document body as
  a single parameter, and an event append binds the event body the same way. "Log the parameter
  values" means every field of every document and every event, verbatim, at `Debug`.
- **Fisher is embedded, so the blast radius is different.** Marten's logs are a server-side
  application's. Fisher runs in-process next to its database file, very often on a desktop, an edge
  box or a device, where the log is a file on the same disk and is exactly the artifact attached to a
  support ticket. Turning on `Debug` to find out why a query is slow should not be the same gesture as
  exporting the database.

::: tip
The type is not a placeholder for the value — it is the diagnostic for Fisher's sharpest binding trap.
A `Guid` bound without conversion is written as a 16-byte BLOB that can never match the TEXT the
schema holds, and every read then silently returns nothing. A line reading `(Guid)` where `(String)`
belongs says that at once; the value would not.
:::

Opt in when you need them:

```cs
builder.Services.AddFisher(opts =>
{
    opts.ConnectionString = connectionString;
    opts.LogSqlParameterValues = true;
});
```

This governs the shipped logger only. `IFisherSessionLogger` hands a custom logger the live
`DbCommand`, values and all — what it does with them is its own decision.

### Per store, per session

```cs
// The whole store
options.Logger(new ConsoleFisherLogger());

// Just this one session
session.Logger = new ConsoleFisherLogger();
```

`IFisherLogger` is the store-level factory and `IFisherSessionLogger` the per-session recorder,
mirroring Marten's `IMartenLogger` / `IMartenSessionLogger` so that a logger ports across with a
rename. Two of Marten's members are deliberately absent:

| Marten member | Why Fisher does not carry it |
| :--- | :--- |
| `LogSuccess(NpgsqlBatch)` and its two siblings | There is no batch to log. `SqliteBatch` exists, but Fisher executes one command per storage operation on purpose — 1000 upserts take 4–6 ms as separate commands and 82–192 ms concatenated. The overload could never fire. |
| `IMartenLogger.SchemaChange(string sql)` | Fisher already has that seam one layer down. All of Fisher's DDL goes through Weasel, and `FisherDatabase` implements `IDatabaseWithMigrationLogger` — so migration output can already be routed anywhere without this interface. Honouring it here would mean displacing the `DefaultMigrationLogger` every Weasel provider type-checks to decide whether a failed DDL statement rethrows with its original stack trace. |

### The unlogged path costs nothing

A store built by hand — `DocumentStore.For(...)`, which is what every test and every non-DI embedded
use does — holds `NulloFisherLogger`, whose `Enabled` is a constant `false`. Every call site checks
that **before** constructing any argument, so nothing is built for a logger that will discard it.

::: tip
That guard is why `IFisherSessionLogger.Enabled` exists at all, and it is fisher#165's lesson held in
advance: a `DaemonTrace.Record` call site once built its interpolated-string argument ahead of the
gate that would have rejected it, so a facility documented as free cost an allocation per call.
`RecordSavedChanges` is the same shape here — it wants an `IChangeSet` that `SaveChangesAsync` would
not otherwise build. Measured: 0 bytes with the guard, 72 bytes per command without it.
:::

A store that *has* a logger still records nothing until the level is on — `DefaultFisherLogger.Enabled`
is `ILogger.IsEnabled(LogLevel.Debug)`, asked every time rather than cached, so a host can change its
levels while running.
