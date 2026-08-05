# Handoff

State of Fisher after the JasperFx 2.39.4 upgrade, `DocumentStore : IEventStore`, the rebuild
concurrency cap, the LINQ layer, DCB tags, and **the async daemon, now landed**. Written for whoever
picks this up next.

**Nothing is half-built.** Every milestone on disk builds, is tested, and was committed complete.
[ROADMAP.md](ROADMAP.md) says what comes next and why in that order.

[CLAUDE.md](CLAUDE.md) has the architecture and the SQLite traps. This document is the compliance
scoreboard and the things that are true right now but not obvious from either.

**396 tests green on net9.0 and net10.0**, four consecutive full runs. 124 of them are shared
cross-store compliance tests.

## Where we are against the compliance suites

`JasperFx.Events.ComplianceTests` 2.39.4 ships **17 suites, 124 tests**. Fisher passes **all 124,
all 17 suites**. Every suite compiles; every one is now also subclassed and running.

`AsyncDaemonCompliance` was the last one in. 2.39.4 had added three suites: `FetchLatestCompliance`
and `StreamArchivingCompliance` went green on the version bump alone, with no production change,
because `FetchLatest`/`ProjectLatest` and `ArchiveStream` were already built to the shape the shared
suite expects. The third, `EventStoreExplorerCompliance`, is what motivated
`DocumentStore : IEventStore`.

### Green — 17 suites, 124 tests

| Suite | Tests |
|---|---|
| `DcbTagQueryAndConsistencyCompliance` | 26 |
| `FetchForWritingCompliance` | 13 |
| `StreamReadCompliance` | 11 |
| `EventMetadataCompliance` | 9 |
| `SelfAggregatingEvolveCompliance` | 8 |
| `FetchLatestCompliance` | 7 |
| `LiveAggregationCompliance` | 7 |
| `StringIdentitySingleStreamCompliance` | 6 |
| `StreamArchivingCompliance` | 6 |
| `EventStoreExplorerCompliance` | 6 |
| `AssignTagWhereCompliance` | 6 |
| `RebuildConcurrencyCapCompliance` | 5 |
| `ActivityCorrelationCompliance` | 4 |
| `EventProjectionRegistrationCompliance` | 3 |
| `EventProjectionEnrichmentCompliance` | 3 |
| `AsyncDaemonCompliance` | 2 |
| `AutoDiscoveredAggregateCompliance` | 2 |

**Green on all seventeen is not the same as feature-complete.** The suites cover what is portable
across stores; "Deliberate gaps" below is still the honest list of what Fisher does not do.

### What the fixture still throws

`FisherComplianceFixture` implements every member and nothing in it throws any more except
`LoadDocumentAsync` for a strongly typed id, which Fisher does not support anywhere. The discipline
stands for the next suite that arrives: a member Fisher cannot honour throws naming its milestone, so
enrolling prematurely fails loudly rather than passing on a stub.

`CreateBatch` went live with DCB tags, adapting Fisher's own `IBatchedQuery`. `EventStore` hands back
the `DocumentStore`. `StartDaemonAsync` builds one daemon per fixture and keeps it, so disposal can
stop it — a second daemon over the same file would mean two writers contending for one lock.

`EventOperations.Unsupported.cs` is now down to **two members**, both event-rewrite
(`OverwriteEvent`, `CompletelyReplaceEvent`). That file shrinking is the progress measure.

### The rebuild concurrency cap

`StoreOptions.MaxPoolSize` is a Fisher-specific knob and the one place this milestone diverges from
the siblings. Marten and Polecat derive the cap from a real connection-pool ceiling — Npgsql's and
SqlClient's `Max Pool Size` keyword. `Microsoft.Data.Sqlite` has no such keyword; its
`SqliteConnectionStringBuilder` exposes only a boolean `Pooling`. So the ceiling is a store option and
nothing folds it into the connection string.

Its default of 8 is chosen for the cap it produces rather than as a pooling recommendation:
`max(1, 8 / 8)` is 1, and one is the honest answer for SQLite, where writers serialize at the file
level and concurrent rebuild cells contend for the same write lock instead of parallelising. The
shared suite only ever sets the ceiling explicitly, so `Events/rebuild_concurrency_cap.cs` is what
pins the default.

### The `IEventStore` surface

`DocumentStore` implements JasperFx's `IEventStore` explicitly, in `DocumentStore.EventStore.cs`, so
none of it lands on the store's own public API. Most of the interface is default-implemented by
JasperFx and left alone. Fisher overrides the two explorer reads it can answer out of `fi_streams`
(`GetRecentStreamsAsync`, `GetStreamMetadataAsync`) and supplies `TryCreateUsage`; the required
members it cannot honour — `BuildProjectionDaemonAsync`, `OpenReadOnlyEventStore`,
`CompactStreamAsync` — throw naming their milestone, the same discipline as
`EventOperations.Unsupported.cs`.

Two SQLite-specific things the shared suite does **not** cover, pinned by
`src/Fisher.Tests/Events/event_store_explorer.cs` instead:

- **`GetStreamMetadataAsync` normalises Guid casing.** `fi_streams.id` holds the lowercase canonical
  form and SQLite's default collation is case-sensitive, so an uppercase Guid string matches nothing.
  The compliance suite only ever passes `Guid.ToString()`, which is already lowercase, so it would
  pass either way. `stream_metadata_is_found_regardless_of_guid_casing` fails without the parse.
- **Recent-stream ordering is a string sort** over ISO-8601 TEXT, correct only while
  `SqliteTimestamp.Format` stays fixed-width, UTC and millisecond-precision.

## The async daemon

Landed across five increments, each built, tested and committed before the next began:

| Increment | What | Commit |
|---|---|---|
| 1 | `IEventDatabase` on `FisherDatabase` — progress reads/writes, highest sequence, timestamp floor, non-stale wait | `1a0e15e` |
| 2 | `FisherHighWaterDetector` | `f5d5cb8` |
| 3 | `FisherEventLoader` + `FisherProjectionBatch` | `dc42709` |
| 4 | `IEventStore<IDocumentSession, IQuerySession>` on `DocumentStore` + `FisherProjectionDaemon` | `Daemon steps 4-5` |
| 5 | `SnapshotLifecycle.Async`, the fixture's daemon pair, enrollment | `Daemon steps 4-5` |

### The scale is not what the test count suggests

Two compliance tests, but they demand the whole daemon: start it, catch up, persist a snapshot, and
rebuild. The machinery is JasperFx's — coordinator, subscription agents, shard tracker, throttled and
resilient loaders, roughly 10,500 lines. **What a store supplies is the storage seam**, which is the
five types above; `FisherProjectionDaemon` itself is a dozen lines closing
`JasperFxAsyncDaemon<,,>` over Fisher's session pair, exactly as Polecat's is.

### `DocumentStore.Daemon.cs` ignores the `IEventDatabase` it is handed

Every member of the generic interface takes an `IEventDatabase` and every one of them works against
`Database` instead. That is not laziness: Marten and Polecat resolve a connection string off that
parameter because they can be database-per-tenant, and a SQLite store is one file. If Fisher ever
grows separate-database tenancy, these are the methods that have to start reading it.

### Teardown's missing-table check has to be in C#

A rebuild clears the projection's documents before replaying, and the table may not exist —
Fisher creates document tables on demand. The trap is that SQLite resolves a table name when it
**prepares** the statement, so `delete from t where exists (select 1 from sqlite_master ...)` fails
before the guard ever runs. The names come back from `sqlite_master` first and the delete is skipped
in C#. `rebuilding_when_the_projection_table_does_not_exist` pins it, and was verified by removing
the check — it fails with `no such table`.

Teardown runs both halves in one transaction. Deleting the progression and then failing before
clearing the documents would leave a projection replaying from zero on top of rows it already wrote,
which is the exact double-application a rebuild exists to avoid.

### The daemon warns about WAL rather than refusing to start

WAL is what lets the daemon read while a session writes; without it SQLite blocks readers for the
duration of a write, so the daemon and every application session serialize against each other. It is
on by default through `SqlitePragmaSettings.Default`, but a consumer replacing
`StoreOptions.PragmaSettings` can turn it off, and the result presents as a slow projection rather
than a misconfiguration. `BuildProjectionDaemonAsync` logs a warning; refusing to start would be the
stronger position and is not what is there.

### Async is genuinely async, and there is a test that says so

`Snapshot<T>(SnapshotLifecycle.Async)` used to throw. If lifting that had registered the projection
as Inline instead, `AsyncDaemonCompliance` would still pass — the document would already be there
when the daemon was asked for it. `an_async_snapshot_is_not_written_by_the_commit_that_appended_the_events`
asserts its *absence* before the daemon runs, which is what tells the two apart.

### The finding that shaped increment 2

**Committed `seq_id`s are contiguous on SQLite, so there is no high-water gap problem at all.** Both
halves verified against 3.51 rather than assumed:

- **Writers are serialized.** One writer per file plus `BEGIN IMMEDIATE` means a transaction's
  sequences fully commit before the next writer allocates any — no interleaving, so no hole.
- **A rollback does not consume the sequence.** SQLite keeps the `AUTOINCREMENT` counter in
  `sqlite_sequence`, an ordinary table that rolls back with the transaction. After a rolled-back
  two-row insert, the next insert reuses the number the failed one had.

Marten and Polecat must distinguish the highest sequence *issued* from the highest safe to *read*,
because a PostgreSQL sequence or SQL Server IDENTITY hands out numbers outside the transaction — a
writer can hold 7 uncommitted while 8 commits ahead of it, so reading to 8 would skip 7 forever.
Their safe-zone polling, stale-gap skipping and `SafeStartMark` machinery exists for that.

So the mark simply **is** `max(seq_id)`, and `DetectInSafeZone` has no separate answer to give.
**Do not reintroduce gap-skipping on the assumption Fisher must need it too** — it would guard a
state that cannot occur. The class comment says so as well.

`a_rolled_back_append_leaves_no_gap_in_the_sequence` is written at raw SQL deliberately: failing a
Fisher append instead proves nothing, because if the failure came before any row was inserted no
sequence was consumed and the assertion holds either way. It inserts a row, **asserts that row took
sequence 4**, rolls back, then requires the next real append to reuse 4.

### The batch must stay atomic

`FisherProjectionBatch` commits the projection's document writes **and** the progression row in one
transaction. Splitting them lets a crash between them either replay events already applied or skip
events never applied — the projection ends up permanently wrong in one direction or the other, with
nothing to signal it.

Sessions are collected rather than merged, and each flushes its *own* operations into the shared
transaction (`FisherSession.FlushOperationsAsync`), because an operation is configured against a
session as its storage context and that is what carries tenancy. Running one session's operations
through another would quietly mis-scope them.

### The loader diverges from the stream reads on purpose

Fisher's stream reads skip an unresolvable `dotnet_type` unconditionally, so a deployment can still
read events it does not know about. **The daemon must not** — silently skipping would leave the
projection permanently wrong with no signal. It honours `SkipUnknownEvents`, and otherwise throws
Fisher's own `UnknownEventTypeException`, which implements JasperFx's `IEventFailureContext` so the
daemon can classify the shard failure and name the offending sequence without knowing Fisher's
exception types. JasperFx owns only `ApplyEventException`; read-side failures belong to the store, as
in both siblings.

### The hazard that stays live

**`AUTOINCREMENT` on `fi_events.seq_id` is load-bearing, not decorative.** A bare
`INTEGER PRIMARY KEY` aliases the rowid, which SQLite **reuses** after a delete. A reused sequence
would sit below the high-water mark and be invisible to every async projection. It is also half of
why the contiguity argument above holds.

### Known gaps in what is built

Deliberate, and each throws by name rather than failing quietly. Each is tracked as an issue rather
than only as a note here:

- **Event-emitting async projections** — [fisher#3](https://github.com/JasperFx/fisher/issues/3).
  `QuickAppendEvents` and friends on the batch throw; they need the append planner's version
  assignment and sequence read-back inside the batch's transaction.
- **Projection side effects** — [fisher#4](https://github.com/JasperFx/fisher/issues/4).
  `PublishMessageAsync` throws; there is no message sink.

[fisher#5](https://github.com/JasperFx/fisher/issues/5) (dead letters) is **closed** — see below.

### Dead letters

`fi_dead_letters` carries `DeadLetterEvent`'s columns one for one, so CritterWatch reads Fisher's the
same way it reads Marten's. `SkipApplyErrors` now works: a poison event is quarantined and its shard
carries on, which `a_skipped_poison_event_is_quarantined_and_the_shard_carries_on` covers end to end
rather than only at the storage layer.

Three decisions worth not undoing:

- **No foreign key to `fi_events`** — deliberately the opposite of the tag tables. A dead letter has
  to survive the event being archived, compacted or cleaned away, or a cascade erases the evidence
  somebody came looking for.
- **The write is on its own connection**, outside the failing batch's transaction, which is about to
  roll back. Writing it inside would roll the record back with the failure it records.
- **It is an upsert.** The daemon retries the write in the background against a pre-assigned id.

That "no foreign key" choice is also why the cleaner has to delete them: nothing else ever would.

### Two bugs found while building the above

**[fisher#6](https://github.com/JasperFx/fisher/issues/6)** — `DeleteAllEventDataAsync` failed with
`FOREIGN KEY constraint failed` whenever DCB tag rows existed: tag tables have a real FK to
`fi_events(seq_id)` and were not being cleared. The suite never caught it because every fixture gets
a fresh database, so the clean always ran before any tag row existed. Fixed by deleting in a fixed
order, tags first.

**[fisher#7](https://github.com/JasperFx/fisher/issues/7)** — `WaitForNonStaleProjectionDataAsync`
translated only its `Task.Delay` cancellation into a `TimeoutException`. Its two reads take the same
token, so a timeout elapsing mid-query escaped as `OperationCanceledException` — the same condition
reported as two different exception types depending on timing alone. It surfaced as a roughly
1-in-8 flake on net9.0 under the full suite's load and would not reproduce in isolation, which is
exactly what the "run the suite more than once" convention exists to catch. The regression test uses
an already-elapsed timeout so the cancellation *must* come out of a read.

Both fixes were verified by reverting them: each new test fails without its fix.

## The LINQ layer

Ported from Polecat, which owns `Polecat.Linq.SqlGeneration` itself rather than taking it from
`Weasel.SqlServer` — so Fisher carrying its own is the mirror, and no upstream Weasel change is
needed. `Weasel.Sqlite` has no `SqlGeneration` namespace at all.

Committed:

1. **`Fisher.Linq.SqlGeneration`** — the where-fragment set. `Statement` is the one genuinely
   dialect-specific file; see below.
2. **`Fisher.Linq.Members`** — member locators over `json_extract`.
3. **`Fisher.Linq.Parsing`** — `WhereClauseParser`, `LinqQueryParser` and the method-call parsers.
4. **The provider** — `FisherQueryable<T>`, `FisherQueryProvider`, the async terminal operators, and
   `session.Query<T>()`.

**`session.Query<T>()` works.** Supported: `Where`, `OrderBy`/`OrderByDescending`/`ThenBy`/
`ThenByDescending`, `Take`, `Skip`, and the async terminals `ToListAsync`, `FirstAsync`/
`FirstOrDefaultAsync`, `SingleAsync`/`SingleOrDefaultAsync`, `CountAsync`/`LongCountAsync`,
`AnyAsync`. Anything else throws `BadLinqExpressionException` naming the operator.

`Joins`, `CursorPaging`, `SoftDeletes`, `GroupBySelectBuilder` and `SelectProjectionAnalyzer` remain
out of scope — they serve features Fisher does not have. So do `Select` projections and `GroupBy`;
`Statement` has no DISTINCT / GROUP BY / HAVING because nothing exercises them yet.

### The provider reuses the closed-shape storage rather than hand-writing SQL

`FisherQueryProvider.Build<T>` takes the column list from `ISelectClause.SelectFields()` and the
materializer from `ISelectClause.BuildSelector()`, both off the **query-only** storage flavour — the
seam CLAUDE.md notes was left in place for exactly this. That is what stops the query path's read
layout drifting away from `LoadAsync`'s. Predicates also go through `storage.FilterDocuments`, which
is a no-op today but is where a conjoined table's tenant filter lands; going around it is how a query
path silently stops honouring tenancy the moment tenancy arrives.

Two things worth knowing:

- **The provider is cached per session and uses the session's own connection**, which is why a query
  inside a unit of work sees writes that session already committed.
- **Counting a paged query wraps it as a subquery.** `Take(2).CountAsync()` must count the page, not
  the table. The subquery is modelled as a nested `Statement` rather than pre-rendered SQL so its
  parameters reach the same command builder. `counting_a_paged_query_counts_the_page` fails without
  it — verified by removing the wrap.
- **Synchronous enumeration throws.** There is no non-blocking way to serve `IEnumerator<T>` over an
  async read, and blocking inside `GetEnumerator` is how a library deadlocks a caller. Marten and
  Polecat refuse here too.

### Why the port is smaller than its source

`json_extract` does not behave like SQL Server's `JSON_VALUE`. Verified against SQLite 3.51 before
anything was built on it: it returns a JSON number as INTEGER, a float as REAL, a string as TEXT and
`true`/`false` as INTEGER 1/0. So `json_extract(data,'$.age') > 30` compares numerically with no
cast, and Polecat's whole `CAST`/`RETURNING` apparatus — `SqlTypeMap`, `BuildTypedLocator`,
`SupportsReturning`, the native-json-type switch — has no analogue. `TypedLocator` and `RawLocator`
are the same string.

### Paging is where the dialects actually diverge

`TOP(n)` and `OFFSET n ROWS FETCH NEXT m ROWS ONLY` collapse to `limit m offset n`. T-SQL needs an
ORDER BY before OFFSET and emits `ORDER BY (SELECT NULL)` as filler; SQLite does not, and inventing
one would impose a sort the caller never asked for. An offset with no limit must say `limit -1`
first — a bare `offset 2` is a parse error, verified.

### Dates are a real capability gap, not a port detail

`DateMember` has no Polecat counterpart. Polecat casts to `datetimeoffset` and lets SQL Server
compare instants. Fisher can only compare the text System.Text.Json wrote, and that text is **not
order-preserving**: STJ trims trailing fractional zeros and preserves the original offset, so
`12:34:56-05:00` sorts before `12:34:56.789+00:00` while being five hours later.

Equality works — the literal is rendered through the very serializer that wrote the document, because
no format string reproduces STJ's trimming. Ordering and range comparison set
`AllowsRangeComparison` false so the parser refuses rather than returning plausible-but-wrong rows,
in both `Where` and `OrderBy`.

**Correction, and it matters for planning.** Earlier notes here said lifting this requires a
normalised sortable duplicated column. That is overstated: SQLite's
`strftime('%Y-%m-%dT%H:%M:%f', json_extract(...))` normalises the offset *and* keeps milliseconds
inline, verified against 3.51 — `order by datetime(...)` puts `12:34:56-05:00` last where raw text
order puts it in the middle. So **fisher#1 (correctness) can ship without duplicated fields**;
**fisher#2 (duplicated fields) is the performance follow-on**, since a function-wrapped locator
cannot be served by an index.

This concerns documents only. The `fi_events` / `fi_streams` timestamp columns are
`SqliteTimestamp`'s fixed-width UTC format precisely so they *do* sort as text.

### String predicates use `instr`, not `LIKE`

The central decision in `Parsing/Methods`. Polecat translates `Contains`/`StartsWith`/`EndsWith` to
`LIKE` patterns; on SQLite that would be wrong twice, both verified against 3.51:

- **`LIKE` is case-insensitive for ASCII by default, while `=` is case-sensitive.** A LIKE-based
  `Contains("frodo")` matches `"Frodo"` on the very same data where `== "frodo"` does not — a query
  surface that contradicts itself, and not what .NET's ordinal `string.Contains` means.
- **`_` and `%` are `LIKE` wildcards**, so a literal needle containing either needs escaping.
  Polecat's `[_]` bracket escaping is T-SQL-only; SQLite needs an `ESCAPE` clause. This is the same
  trap CLAUDE.md records for the document cleaner. `instr` takes its needle literally.

So `Contains` is `instr(loc, ?) > 0`, `StartsWith` is `instr(loc, ?) = 1`, and `EndsWith` is
`substr(loc, -n) = ?` where `n` is the needle's length computed at translation time — the needle is a
constant, so this binds one parameter rather than two. An explicit `StringComparison` of
`OrdinalIgnoreCase` folds both sides with `lower()`. Empty-needle behaviour matches .NET: `instr`
returns 1 for an empty needle so `StartsWith("")` is true, and `EndsWith("")` is special-cased to
`1=1` because `substr(x, 0)` returns the whole string.

`x.Name.Length` uses SQLite's `length`, not SQL Server's `LEN` — `LEN` ignores trailing spaces and
`length` does not, which is what `string.Length` means.

### `Contains` over a collection has two bindings

`array.Contains(x)` binds to `MemoryExtensions.Contains(ReadOnlySpan<T>, T)` on modern .NET, not
`Enumerable.Contains` — so `EnumerableContains` matches on the *shape* of the call (a source plus a
document member) rather than on the declaring type. The span operand also cannot be evaluated by
compiling a lambda, because `ReadOnlySpan<T>` is a ref struct and cannot be returned as `object`;
`StripSpanConversion` unwraps back to the underlying array first. Three tests caught this.

### DCB tags

All 32 tag tests pass. The shape, and the parts that are SQLite-specific:

- **One `fi_event_tag_<suffix>` table per registered tag type**, composite primary key leading with
  `value` because a tag query filters on it. That key is also what makes tagging idempotent: both the
  append path and `AssignTagWhere` write `on conflict do nothing` rather than reading first.
- **Tags are written after the batch and inside its transaction.** A tag row is keyed by the `seq_id`
  SQLite assigns on insert, which Fisher only learns from the append's trailing sequence read-back —
  so there is nothing to write until the appends postprocess. Committing separately would leave an
  event visible but untagged, which a tag query cannot tell apart from never-tagged.
- **Queries use `seq_id in (select …)` subselects, not joins.** Joining several tag tables multiplies
  rows when one event carries two matching tags, and the caller expects each event once.
- **Ordering is by `seq_id`** — a tag query spans streams, so version is not a global order.
- **Guid tag values bind as lowercase canonical text.** The raw Guid writes a BLOB and
  Microsoft.Data.Sqlite's string form is uppercase; under the case-sensitive default collation either
  writes a row that can never be read back.

`AssignTagWhere` is a **client of the LINQ `WhereClauseParser`**, exactly as Marten builds it. The
only new piece was `EventMemberFactory`, an `IMemberResolver` resolving `IEvent` members to
`fi_events` columns instead of `json_extract` paths — which is why `IMemberResolver` is an interface.
All six of its compliance tests passed on the first run with no translator written for them.

One asymmetry worth knowing: **`IEvent.Timestamp` allows range comparison where a document's
`DateTimeOffset` member does not.** Same CLR type, different storage — `fi_events.timestamp` is
`SqliteTimestamp`'s fixed-width UTC format, chosen so a string comparison *is* an instant comparison.

### The DCB consistency check

`FetchForWritingByTags` records the highest sequence it saw; `SaveChangesAsync` re-runs the query
**inside its write transaction, before anything is written**, and throws `DcbConcurrencyException` if
anything matching has landed since. Both halves matter: checking after the write would be checking
against our own appends, and checking outside the transaction would prove nothing because
`BEGIN IMMEDIATE` is what holds the write lock.

A boundary over an *empty* result still enforces consistency — `LastSeenSequence` is 0, and any
matching event appearing later has a sequence above it. That is what makes a boundary usable as a
"this must not exist yet" assertion.

### Batched queries exist for parity, not for speed

`IBatchedQuery` matches the siblings' shape — declared reads, tasks that do not complete until
`Execute`. **It buys Fisher essentially nothing in throughput, and that is understood rather than
unfinished.** In Marten and Polecat a batch collapses several network round trips; SQLite is
embedded, so there are none to collapse. It is carried so DCB code ports between Critter Stack stores
unchanged, and so Fisher can enroll in the shared batched-query tests with a real implementation
rather than a test-only shim.

The alternative was considered and rejected: `DcbTagQueryAndConsistencyCompliance` is a single class
with no supported-flag opt-out, so declining batching would have cost all 26 of its tests, not the 4
that touch a batch.

Implemented without statement coalescing on purpose. The one property that does hold is that the
reads run back to back on one connection with nothing interleaved, so a set of boundaries is
established against a coherent view.

### Conversions that fail silently

Every one of these returns *no rows* rather than an error, which is why they are pinned by tests that
run generated locators against genuinely stored documents:

- A bool binds as INTEGER 1/0, not the `"true"`/`"false"` strings `JSON_VALUE`'s nvarchar needs.
- A Guid binds as lowercase canonical text. The raw `Guid` writes a 16-byte BLOB; an uppercase string
  misses under the case-sensitive default collation.
- The JSON path uses the **serializer's** naming policy — `Name` is `$.name` under Fisher's camelCase
  default. An explicit `[JsonPropertyName]` wins verbatim.
- AsString enums are cased by the naming policy, since the serializer wires
  `JsonStringEnumConverter(PropertyNamingPolicy)`.

## Deliberate gaps, so you don't mistake them for bugs

Each of these is a decision with a reason, not an oversight:

- **Exclusive appends are the optimistic ones.** `AppendExclusive`, `FetchForExclusiveWriting` and
  `WriteExclusivelyToAggregate` do not lock. SQLite has no row lock; the faithful equivalent would
  hold `BEGIN IMMEDIATE` from fetch to commit, blocking every other writer for as long as a caller
  holds a session. Safety is unchanged — the version guard still runs inside the write transaction —
  but a loser fails instead of waiting. **This is the most likely place a future compliance suite
  disagrees with Fisher.**
- **Hi-Lo gaps are expected, not a bug.** A process that stops mid-allocation abandons the rest of
  its `MaxLo` range, and `SetFloor` rounds up to a whole page. Both match Marten and Polecat.
- **No `Select` projections, `GroupBy`, `Include`/joins or `Distinct`.** `session.Query<T>()` covers
  filtering, ordering and paging; anything else throws by name rather than falling back to
  client-side evaluation.
- **No ordering or range comparison on a date member.** See the LINQ section — the stored text does
  not sort by instant.
- **`GetOrStartMessageSink` throws** — projection side effects cannot be published, which is why the
  projection batch's `PublishMessageAsync` throws too
  ([fisher#4](https://github.com/JasperFx/fisher/issues/4)).
- **An async projection cannot append events of its own**
  ([fisher#3](https://github.com/JasperFx/fisher/issues/3)).
- **No dead letters.** A failing event stops its shard rather than being quarantined
  ([fisher#5](https://github.com/JasperFx/fisher/issues/5)).
- **No soft delete, hierarchies, numeric revisions, sub-classing.** All additive against the current
  column shape.
- **No duplicated fields**, so no query can use an index —
  [fisher#2](https://github.com/JasperFx/fisher/issues/2).

## Traps that have already cost real time

The full list is in CLAUDE.md. The two most expensive, both of which produced *silently wrong*
behaviour rather than an error:

1. **A `Guid` bound as a TEXT parameter is written UPPERCASE** by Microsoft.Data.Sqlite, while
   `SqliteStorageDialect<T>.ToDatabaseValue` writes lowercase. SQLite's default collation is
   case-sensitive, so mixing them writes rows that can never be read back — every document load
   returned null, and only for Guid-identified types. `SqliteGuidIdentification` exists solely for
   this. The failure mode is nasty: delete tests and "unknown id returns null" tests pass throughout,
   because they assert absence.
2. **`SqliteConnection.ClearAllPools()` is process-wide** and xUnit runs collections in parallel, so
   one test's cleanup disposes connections another test is using. Presents as a rare flake
   (`ObjectDisposedException: SQLitePCL.sqlite3`). `TemporaryDatabase` clears only its own pool.

## Conventions worth not relearning

- Test files and classes are `snake_case`. Pass `TestContext.Current.CancellationToken`.
- MTP extension packages stay on the **1.x** line — xunit.v3 3.2.2 is built against
  Microsoft.Testing.Platform 1.x and 2.x dies at startup.
- CI runs the test executable directly; `dotnet test` cannot emit TRX under MTP.
- Conventional `Apply`/`Create` dispatch is emitted by JasperFx's **source generator**, keyed on
  `(aggregate, id type)`, with no runtime fallback. An aggregate with no `Id` gets no dispatcher.
- Run the full suite on both TFMs before committing, and more than once — two of the bugs above only
  appeared intermittently or on one TFM.
