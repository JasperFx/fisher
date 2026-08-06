# Handoff

State of Fisher after the **JasperFx 2.41.0 upgrade**, which brought four new compliance suites and
with them multi-stream projections, flat-table projections, and the fix for fisher#1. Written for
whoever picks this up next.

**Nothing is half-built.** Every milestone on disk builds, is tested, and was committed complete.
[ROADMAP.md](ROADMAP.md) says what comes next and why in that order.

[CLAUDE.md](CLAUDE.md) has the architecture and the SQLite traps. This document is the compliance
scoreboard and the things that are true right now but not obvious from either.

**461 tests green on net9.0 and net10.0.** 167 of them are shared cross-store compliance tests, and
those ran three consecutive times clean before this was written.

## Where we are against the compliance suites

`JasperFx.Events.ComplianceTests` 2.41.0 ships **21 suites, 167 tests**. Fisher passes **all 167,
all 21 suites**. Every suite compiles; every one is also subclassed and running.

The four that arrived in 2.40.0 and 2.41.0 divided cleanly into "already true" and "had to be built":

| New suite | What it cost |
|---|---|
| `StringStreamIdentityCompliance` | Nothing. 19 tests green on the bump alone — `StreamIdentity.AsString` was already built to the shape the suite expects. |
| `SnapshotLifecycleCompliance` | Nothing. 6 tests green on the bump alone; inline and async snapshots already agreed. |
| `MultiStreamProjectionCompliance` | One file. `MultiStreamProjection<TDoc, TId>` closes JasperFx's shared base over Fisher's session pair, and all 10 tests passed first run — slicing, `Identities`, `FanOut`, inline and async. The document-storage work that let a projection key on a string paid for this. |
| `FlatTableProjectionCompliance` | A real feature: `Projections/Flattened/`, ~700 lines. See CLAUDE.md. |

**Green on all twenty-one is not the same as feature-complete.** The suites cover what is portable
across stores; "Deliberate gaps" below is still the honest list of what Fisher does not do.

### Green — 21 suites, 167 tests

| Suite | Tests |
|---|---|
| `DcbTagQueryAndConsistencyCompliance` | 26 |
| `StringStreamIdentityCompliance` | 19 |
| `FetchForWritingCompliance` | 13 |
| `StreamReadCompliance` | 11 |
| `MultiStreamProjectionCompliance` | 10 |
| `EventMetadataCompliance` | 9 |
| `SelfAggregatingEvolveCompliance` | 8 |
| `FlatTableProjectionCompliance` | 8 |
| `FetchLatestCompliance` | 7 |
| `LiveAggregationCompliance` | 7 |
| `StringIdentitySingleStreamCompliance` | 6 |
| `StreamArchivingCompliance` | 6 |
| `SnapshotLifecycleCompliance` | 6 |
| `EventStoreExplorerCompliance` | 6 |
| `AssignTagWhereCompliance` | 6 |
| `RebuildConcurrencyCapCompliance` | 5 |
| `ActivityCorrelationCompliance` | 4 |
| `EventProjectionRegistrationCompliance` | 3 |
| `EventProjectionEnrichmentCompliance` | 3 |
| `AsyncDaemonCompliance` | 2 |
| `AutoDiscoveredAggregateCompliance` | 2 |

### What the 2.41.0 bump did *not* cost

`CompactStreamAsync<T>` moved onto `IEventStoreOperations` (jasperfx#635). The upstream note warns that
adoption is not optional, because Marten and Polecat both declared the member themselves and would go
ambiguous on bumping. **Fisher never declared it**, so there was nothing to collapse and the
default-implemented throw is exactly the right behaviour here. `IEventDataMasking` moved into
`JasperFx.Events/Protected/` in the same release; Fisher implements none of it. Both are now tracked —
fisher#10 and fisher#9.

### What the fixture still throws

`FisherComplianceFixture` implements every member and nothing in it throws any more except
`LoadDocumentAsync` for a strongly typed id, which Fisher does not support anywhere. The discipline
stands for the next suite that arrives: a member Fisher cannot honour throws naming its milestone, so
enrolling prematurely fails loudly rather than passing on a stub.

`CreateBatch` went live with DCB tags, adapting Fisher's own `IBatchedQuery`. `EventStore` hands back
the `DocumentStore`. `StartDaemonAsync` builds one daemon per fixture and keeps it, so disposal can
stop it — a second daemon over the same file would mean two writers contending for one lock.

`QueryTableAsync` arrived with 2.41.0 and is the seam's only raw data access — a table name in, every
row out, deliberately predicate-free. Fisher's does the schema fold and converts a lowercase-canonical
Guid string back to a `Guid`, because SQL Server has `uniqueidentifier` and PostgreSQL has `uuid` while
SQLite has neither, so on Fisher *something* has to convert. See CLAUDE.md for why that is the honest
answer rather than a fudge.

`EventOperations.Unsupported.cs` is still **two members**, both event-rewrite
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
members it cannot honour — `OpenReadOnlyEventStore` and `CompactStreamAsync` (fisher#10) — throw
naming their milestone, the same discipline as `EventOperations.Unsupported.cs`.
`BuildProjectionDaemonAsync` used to be on that list and is now real.

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

**Nothing in the daemon throws any more.** What is left is one open question rather than a gap:

- **A message bus** — [fisher#8](https://github.com/JasperFx/fisher/issues/8). The side-effect seam
  is built, but the default outbox drops every message and Fisher ships no delivery mechanism.
  Whether that stays Fisher's answer (as it is Marten's and Polecat's) is the question on the issue.

[fisher#3](https://github.com/JasperFx/fisher/issues/3) (event-emitting projections),
[fisher#4](https://github.com/JasperFx/fisher/issues/4) (side effects) and
[fisher#5](https://github.com/JasperFx/fisher/issues/5) (dead letters) are all **closed** — see below.

### Event-emitting async projections

JasperFx drives three synchronous members on the batch. **All three do the same thing: record the
`StreamAction` and let `ExecuteAsync` plan it inside the transaction.** They can, because the action
JasperFx hands over already carries every raised event, and the single-stream-start path passes the
same action instance to each call — reference identity dedupes them.

Marten queues three different storage operations instead. That is right for Postgres and wrong here,
for two SQLite reasons: the version has to come from a read under the write lock (the slice
pre-assigns client-side from its own event count, which only matches when the projection has seen the
whole stream), and routing through `FisherQuickAppendEventsOperation` is the only thing that supplies
the `seq_id` a tag row is keyed by. Queueing Weasel's bare per-event operations would have made
raised events silently untaggable.

**Polecat no-ops these three members rather than throwing**, so an event-raising projection there
drops its events with no signal at all. Worth reporting upstream; do not copy it.

### Projection side effects

`IMessageOutbox` vends an `IMessageBatch` per unit of work, and both commit paths bracket their
transaction with its two hooks. Type names match Polecat's exactly — messaging is not dialect-specific,
so projection code ports between the stores unchanged.

**The thing worth knowing is what the tests pin, because the obvious test does not.** Recording the
hook order proves nothing: `before` then `after` is the order even if both run before the commit. The
invariant is what the rest of the database can see when each fires, so each hook probes the committed
state over a *separate* connection — invisible at `BeforeCommit`, visible at `AfterCommit`. The
hook-order test passed with `AfterCommitAsync` deliberately moved to before the commit; the probe
does not. Both commit paths have their own.

A related trap already avoided: `AfterCommitAsync` runs *outside* the resilience pipeline in the
projection batch. A retried `SQLITE_BUSY` re-executes the whole delegate, so a post-commit publish
inside it would fire twice for a transaction that had already committed.

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

## Flat-table projections — the one new feature 2.41.0 demanded

`FlatTableProjectionCompliance` is the only one of the four new suites Fisher could not pass by being
enrolled. It needs a real flat-table projection base, and it says so plainly: the shared partial
carries the table name, the projection name and every mapping, and each consumer supplies a small
partial with the constructor and the primary key column, because no single `base(...)` call satisfies
Marten, Polecat and Fisher. Fisher's half is three lines, in
`Compliance/ComplianceFlatTableProjection.Fisher.cs`.

`src/Fisher/Projections/Flattened/` is a port of Polecat's — the mapping API and the column-map shapes
are its. Four things are not, and each is a decision rather than a translation:

1. **One `insert … on conflict … do update` where Polecat emits a `MERGE`.** SQLite has had upsert
   syntax since 3.24, so matched and not-matched are two clauses of one statement, and a parameter
   appearing in both is bound once by name. **An unqualified column on the right of the update
   assignment is the pre-update row** — that is what makes `"a" = "a" + @p1` an increment, where
   `excluded."a"` would be what the insert branch would have written. Polecat spells it `target.[a]`.
2. **The table is created by the migration, not lazily on first write.** Registering the projection
   puts a `FlatTableFeatureSchema` into the store's feature set. Polecat issues a CREATE TABLE from
   inside its first apply, which works but routes around `AutoCreateSchemaObjects` — a store set to
   `AutoCreate.None` would still get DDL. `auto_create_none_leaves_the_table_alone` pins Fisher's.
3. **The physical name folds the store's logical schema in, resolved in `DocumentStore`'s
   constructor.** SQLite has no schemas, so the prefix *is* the isolation boundary between two logical
   stores in one file, and a flat table that kept its bare name would be silently shared by both. The
   projection's constructor cannot see the store and is usually registered in the same lambda that sets
   `DatabaseSchemaName`, in either order — so the fold waits until the options are final. The `fi_`
   family prefix is deliberately *not* applied: it marks a table Fisher owns the shape of, and a flat
   table's shape is the projection's. The rename needs `FlatTable : Table`, because
   `SchemaObjectBase.Identifier` has a protected setter and Weasel's `MoveToSchema` only changes the
   qualifier.
4. **Rebuild teardown is told the table name directly**, through `IPublishesTables`. `PublishedTypes()`
   is empty — a flat table's rows are not documents — so the mapped-type sweep in
   `TeardownExistingProjectionStateAsync` cannot see it, and without this a rebuild replays onto the
   rows the previous run left. The compliance suite catches exactly that, with a row whose events it
   archives so the replay cannot recreate it.

The Guid trap shows up here too, in the one place a flat table meets it: the primary key holds a stream
id, so it goes down through the lowercase-canonical conversion. Bound any other way, the second event
on a stream inserts a second row instead of updating the first —
`the_stream_id_key_is_stored_as_lowercase_canonical_text` is what would fail.

`MultiStreamProjection<TDoc, TId>`, by contrast, is one file that closes JasperFx's shared base over
Fisher's session pair, and all ten of its compliance tests passed on the first run — grouping,
`Identities`, `FanOut`, inline and async. The reason it was that cheap is that the document-storage
work already let a projection key on something other than the stream identity.

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

### Dates — fisher#1, closed, and it did not need duplicated fields

Earlier handoffs said lifting this needed a normalised sortable duplicated column. It did not.
`TimestampMember.TypedLocator` is `strftime('%Y-%m-%dT%H:%M:%f', json_extract(...))`, which hands the
stored text to SQLite's own date parser: the trailing offset is folded into UTC and the result is
fixed-width to the millisecond, so it sorts as text. That is the same move Polecat makes with
`CAST(... AS datetimeoffset)` and Marten with `timestamptz`, spelled the way SQLite spells it.

Three decisions in it worth not relitigating:

- **Equality goes through the same normalisation as ordering**, not the exact serializer rendering it
  used before. Two spellings of one instant must not be equal for `>=` and unequal for `==`. The cost
  is that `==` discriminates only to the millisecond — `%f` has no sub-millisecond form — but
  `timestamptz` is microsecond precision, so the siblings truncate a `DateTimeOffset` too. This is
  closer to their behaviour, not further from it.
- **`DateOnly` and `TimeOnly` did not need it and did not get it.** A `DateOnly` is fixed-width
  `yyyy-MM-dd` with no offset and no fraction; a `TimeOnly`'s optional fraction is a strict suffix, so
  trimming shortens the string without changing which of two values compares smaller. They stay on
  `DateMember` and the bare locator.
- **A `DateTime` with `Kind.Unspecified` is not shifted.** STJ writes it with no offset and SQLite
  reads an offsetless string as already UTC, so converting the literal would move it off the values it
  is meant to match.

`querying_documents` pins it end to end with four documents whose *text* order and *instant* order
disagree at every position — that list is the test, and a locator that compared raw text would pass an
assertion built on the wrong one.

**`AllowsRangeComparison` survives**, because building this turned up a second unsortable member: a
string-stored enum. Under `EnumStorage.AsString` the stored value is the member's name, so
`x.Grade > Grade.Pass` and `OrderBy(x => x.Grade)` sorted alphabetically rather than by the enum's
declared order — quietly wrong, no signal. Both now refuse and name `EnumStorage` in the message.
Fisher's default is `AsInteger`, which orders correctly and is unaffected.

fisher#2 (duplicated fields) is still the performance follow-on and is now more clearly worth having:
`strftime` over `json_extract` is computed per row and no index can serve it. A **generated column**
may be the better shape than a written one — SQLite indexes `VIRTUAL` generated columns, so the
duplication costs index space but not row space, and it cannot drift from `data`.

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
- **No ordering or range comparison on a string-stored enum.** See the LINQ section — the stored form
  is the member's name, so it sorts alphabetically rather than by declared order. Timestamps used to
  be on this list and no longer are (fisher#1).
- **No message bus.** The side-effect seam is real and both commit paths bracket their transaction,
  but the default `NulloMessageOutbox` drops every message, so nothing in the box delivers one —
  [fisher#8](https://github.com/JasperFx/fisher/issues/8). That is the sibling behaviour, not a stub.
- **No event rewriting.** `OverwriteEvent` / `CompletelyReplaceEvent` throw, stream compacting throws
  ([fisher#10](https://github.com/JasperFx/fisher/issues/10)), and `IEventDataMasking` — lifted into
  JasperFx.Events in 2.41.0 — has no implementation at all
  ([fisher#9](https://github.com/JasperFx/fisher/issues/9)). All three share a hazard: an async
  projection that has already passed a rewritten event does not see the rewrite.
- **No soft delete, hierarchies, numeric revisions, sub-classing.** All additive against the current
  column shape.
- **No duplicated fields**, so no query can use an index —
  [fisher#2](https://github.com/JasperFx/fisher/issues/2).
- **Multi-tenancy stops at a tenant id column.** No database-per-tenant, which is why every
  `IEventDatabase` parameter in `DocumentStore.Daemon.cs` is ignored.
- **No DI registration.** There is no `AddFisher(...)`; a store is built with `DocumentStore.For`.

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
