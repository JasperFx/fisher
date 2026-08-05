# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What is Fisher?

SQLite-backed Event Store and lightweight Document Database in the Critter Stack. A deliberate
subset of Marten (PostgreSQL) and Polecat (SQL Server), built on Weasel.Sqlite for schema management
and Weasel.Storage for the shared closed-shape document/event runtime.

**Fisher is early and incomplete.** See "Current state" below before assuming a feature exists,
[ROADMAP.md](ROADMAP.md) for what comes next and in what order, and [HANDOFF.md](HANDOFF.md) for the
compliance scoreboard and the current state of play.

## Commands

```bash
dotnet build fisher.slnx
dotnet test fisher.slnx                    # both TFMs; they run serially on purpose
dotnet test fisher.slnx -f net10.0         # one TFM

# One test class or method (Microsoft Testing Platform — note the bare `--`)
dotnet test src/Fisher.Tests/Fisher.Tests.csproj -f net10.0 -- --filter-method "*appending_events*"
```

No database server is required — tests create throwaway SQLite files via
`TemporaryDatabase.Create()`.

## Architecture

### The three-layer split

Fisher owns very little storage code. The division is worth internalizing before adding anything:

- **JasperFx / JasperFx.Events** — event/projection/daemon abstractions. Implement its interfaces;
  do not reinvent them. `EventGraph` derives from `JasperFx.Events.EventRegistry`.
- **Weasel.Sqlite** — schema management, table definitions, migrations, the PRAGMA-applying
  `SqliteDataSource`. All DDL goes through it; no hand-written CREATE TABLE.
- **Weasel.Storage** — the dialect-neutral closed-shape document + event storage runtime extracted
  from Marten. Fisher supplies two dialects (`SqliteStorageDialect<TId>`,
  `SqliteEventStoreDialect`) and the runtime does the rest. **Prefer extending the dialects over
  writing bespoke storage code.**

### SQLite divergences from Marten/Polecat

These are the decisions that don't transfer from the sibling stores, and the reasons they exist:

| Concern | Marten / Polecat | Fisher |
|---|---|---|
| Schemas | real DB schemas | **none** — `DatabaseSchemaName` folds into the table prefix (`FisherTableNaming`) |
| Timestamps | `timestamptz` / `datetimeoffset` | ISO-8601 TEXT (`SqliteTimestamp`) |
| JSON | `jsonb` / native `json` | TEXT + json1 functions |
| Booleans | `boolean` / `bit` | INTEGER 0/1 |
| Guids | native | TEXT — bind via `SqliteStorageDialect<T>.ToDatabaseValue` |
| Event sequence | sequence / IDENTITY | `INTEGER PRIMARY KEY AUTOINCREMENT` |
| Append concurrency | advisory lock / UPDLOCK,HOLDLOCK | `BEGIN IMMEDIATE` (`IsolationLevel.Serializable`) |
| Document upsert | `MERGE` (Polecat) | `INSERT … ON CONFLICT … DO UPDATE … RETURNING`, as Marten |
| Exclusive append | row lock — the loser **waits** | no row lock — the loser **fails** (see below) |
| Sequence read-back | bulk function / `OUTPUT ... INTO` | trailing SELECT by stream + version range |
| Load-many ids | `= ANY($1)` / `OPENJSON` | `json_each(@ids)` |
| Hi-Lo advance | stored function / optimistic UPDATE + retry | one atomic upsert (see below) |
| Unit of work | parallel, aggregates failures | strictly sequential (one writer per file) |
| Transient retry | none needed | real Polly retry on `SQLITE_BUSY` / `SQLITE_LOCKED` |

**`AppendExclusive` / `FetchForExclusiveWriting` / `WriteExclusivelyToAggregate` are the optimistic
methods.** Marten and Polecat take a row lock so a competing session waits its turn. SQLite has no
row locks and one writer per database file, so the equivalent would be holding a `BEGIN IMMEDIATE`
open from the fetch until `SaveChangesAsync` — blocking every other writer in the process for as long
as the caller holds the session. The safety property is unchanged (the version guard still runs inside
the write transaction, so no lost update); what differs is that a loser gets
`EventStreamUnexpectedMaxEventIdException` instead of waiting. Revisiting this means giving
`FisherSession` a session-scoped transaction, which `SaveChangesAsync` would then have to join rather
than open.

Traps that have already bitten and are easy to reintroduce:

- A column `DEFAULT` that is an expression **must be parenthesized** — `DEFAULT strftime(...)` is a
  CREATE TABLE syntax error. Use `SqliteTimestamp.NowDefaultExpression` in DDL,
  `NowExpression` inside statements.
- `AUTOINCREMENT` on `fi_events.seq_id` is load-bearing, not decorative. A bare `INTEGER PRIMARY
  KEY` aliases the rowid, which SQLite **reuses** after a delete; a reused seq_id would silently
  hide events from every async projection behind the daemon's high-water mark.
- Constraint-violation mapping needs the **extended** result code. Every constraint failure shares
  primary code `SQLITE_CONSTRAINT` (19); only 1555 (PRIMARYKEY) / 2067 (UNIQUE) distinguish them.
- Binding a `Guid` without conversion writes a 16-byte BLOB that never matches the TEXT the schema
  holds.
- **A `Guid` bound as a TEXT parameter is written UPPERCASE.** Microsoft.Data.Sqlite emits the
  uppercase form for a raw `Guid`; `SqliteStorageDialect<T>.ToDatabaseValue` emits the lowercase
  canonical form. SQLite's default collation is case-sensitive, so mixing the two writes rows that
  can never be read back — every load returns null and every `json_each` id match fails, silently and
  only for Guid-identified types. This is why `SqliteGuidIdentification` exists: the shared write
  operations bind `Identification.ToRawSqlValue` directly, bypassing the dialect, so the conversion
  has to live in the identity strategy. `a_guid_id_is_stored_as_lowercase_canonical_text` pins it.

### Row readers

`FisherEventsRowReader` and `FisherStreamsRowReader` own the canonical SELECT projection and lock
the column order — adding or renaming a column means changing those files and only those files.

Every conversion in them is **explicit** (`Guid.Parse`, `SqliteTimestamp.FromDatabaseValue`,
`GetInt64(..) != 0`) rather than `GetGuid` / `GetFieldValue<DateTimeOffset>` / `GetBoolean`. The
write path converts explicitly on the way in, so reading through a provider convenience method
would leave the round trip depending on Microsoft.Data.Sqlite's coercion rules instead of Fisher's
own storage decisions — asymmetry that breaks quietly under a provider upgrade.

### Table naming

`main` → `fi_events`; any other `DatabaseSchemaName` → `compliance_fi_events`. Every `DbObjectName`
uses schema `main`, so nothing ever renders as qualified SQL. This is what gives logical-store and
test isolation inside one database file without ATTACH lifecycle on every pooled connection.

### Closed upstream gap — weasel#423

Historical note, in case the shape of the session's execute loop looks over-engineered. Fisher used
to carry a `FisherCommandBuilder` shim because Weasel.Sqlite's `CommandBuilder` did not declare
`Weasel.Core.ICommandBuilder`, the surface every shared closed-shape storage operation configures
itself against — without it, no Weasel.Storage operation could be configured against a SQLite command
builder at all.

Fixed upstream by [weasel#424](https://github.com/JasperFx/weasel/pull/424) and shipped in
**Weasel.Sqlite 9.23.2**. The shim is gone; `FisherSession` uses `Weasel.Sqlite.CommandBuilder`
directly. Do not reintroduce it.

## Current state

Working, with tests:

- `fi_streams` / `fi_events` / `fi_event_progression` schema via Weasel.Sqlite
- `SqliteStorageDialect<TId>` and `SqliteEventStoreDialect` (Quick append + auxiliary operations)
- `DocumentStore`, `FisherSession` unit of work, `EventOperations`
- `StartStream` / `Append`, version assignment, optimistic concurrency, sequence read-back
- Reads: `FetchStreamAsync` (version / from-version / timestamp bounded), `FetchStreamStateAsync`,
  `LoadAsync`, both stream identity styles
- `ArchiveStream` / `UnArchiveStream` / `TombstoneStream`
- Live aggregation: `AggregateStreamAsync`, `AggregateStreamToLastKnownAsync`, over auto-discovered
  self-aggregating types
- Inline projections: `Projections.Snapshot<T>` and `Projections.Add`, applied during
  `SaveChangesAsync` in the same transaction as the events
- `FetchForWriting` / `WriteToAggregate` / `AppendOptimistic` / `FetchLatest` / `ProjectLatest`
- `EventOperations` implements the full `IEventStoreOperations` — see below for which members throw
- Document storage over Guid, string, int and long ids; numeric ids via Hi-Lo sequences (`fi_hilo`)
- `EventProjection.storeEntity` — an `EventProjection`'s `Create`/`Project` results are stored inline
- `DocumentStore.Advanced` — `Clean`, `ResetAllDataAsync`, `ResetHiloSequenceFloorAsync<T>`
- `DocumentStore : IEventStore` — the explorer reads (`GetRecentStreamsAsync`,
  `GetStreamMetadataAsync`) and `TryCreateUsage`; see below
- **LINQ** — `session.Query<T>()` over `json_extract`: where, ordering, paging, async terminals
- **DCB tags** — tag tables, tagged appends, `QueryByTagsAsync`, `EventsExistAsync`,
  `AssignTagWhere`, `AggregateByTagsAsync`, `FetchForWritingByTags` with its consistency guard, and
  batched queries
- **Async daemon** — `IEventDatabase` on `FisherDatabase`, `FisherHighWaterDetector`,
  `FisherEventLoader`, `FisherProjectionBatch`, `IEventStore<IDocumentSession, IQuerySession>` on
  `DocumentStore`, `FisherProjectionDaemon`, and `Snapshot<T>(SnapshotLifecycle.Async)`

Not implemented yet — do not assume these work:

- **Document storage — all four identity types work.** `Store`/`Insert`/`Update`/`Delete`/
  `LoadAsync`/`LoadManyAsync` over Guid, string, int and long ids, in the same unit of work and
  transaction as event appends. What is still missing: no LINQ, no querying beyond load by id, no
  soft delete, no hierarchies, no duplicated fields, no numeric revisions.

### Inline projections

`Snapshot<T>` closes `SingleStreamProjection<TDoc, TId>` over the aggregate's own identity type — the
same rule live aggregation follows, for the same source-generator reason — and registers the document
mapping so the snapshot's table is created with the schema.

Applying them happens in `FisherSession.SaveChangesAsync` **before the batch is taken**, because
applying a projection queues further operations (the snapshot writes) that have to commit alongside
the events that caused them.

The subtle part is versioning. A projection needs its events to already know their versions, but
Fisher normally assigns those inside the write transaction, where the current stream version has just
been read under the write lock. `AppendPlanner.AssignVersionsAheadOfProjectionsAsync` therefore reads
the version early, *outside* the lock. **That is not a weakened guard**: the same versions are
re-derived inside the transaction and the optimistic concurrency check still runs there, so a racing
writer still fails the commit. The early pass exists only to give projections something to read.

Document tables are created on demand at commit (`EnsureDocumentTablesAsync`), because a document
type can be stored without ever being registered and a snapshot type is registered by projection
configuration — either way the first write may be the first time the table is needed. Only types the
schema has already mapped are considered, which is how a document operation is told apart from an
event one.

### The async daemon

The daemon itself is JasperFx's — coordinator, subscription agents, shard tracker, throttled and
resilient loaders, roughly 10,500 lines. **What Fisher supplies is the storage seam**, and that is
all of it:

| Piece | Where | What it does |
|---|---|---|
| `IEventDatabase` | `Storage/FisherDatabase.EventDatabase.cs` | progress reads/writes, highest sequence, timestamp floor, non-stale wait |
| `FisherHighWaterDetector` | `Events/Daemon/` | how far it is safe to read |
| `FisherEventLoader` | `Events/Daemon/` | pages `fi_events` by sequence |
| `FisherProjectionBatch` | `Events/Daemon/` | one transaction of projection writes + the progression row |
| `IEventStore<IDocumentSession, IQuerySession>` | `DocumentStore.Daemon.cs` | shards, sessions, loaders, batches, progression bookkeeping |
| `FisherProjectionDaemon` | `Events/Daemon/` | a dozen lines closing `JasperFxAsyncDaemon<,,>` over Fisher's session pair |

Five things that are decisions rather than mechanics:

- **The high-water mark simply is `max(seq_id)`.** Marten and Polecat must distinguish the highest
  sequence *issued* from the highest safe to *read*, because a PostgreSQL sequence or SQL Server
  IDENTITY hands out numbers outside the transaction — a writer can hold 7 uncommitted while 8
  commits ahead of it. On SQLite one writer per file plus `BEGIN IMMEDIATE` means a transaction's
  sequences fully commit before the next writer allocates any, and a rollback returns the number
  (`sqlite_sequence` is an ordinary table and rolls back with it). Committed sequences are contiguous,
  so `DetectInSafeZone` has no separate answer to give. **Do not reintroduce gap-skipping** — it would
  guard a state that cannot occur.
- **The batch must stay atomic.** It commits the projection's document writes *and* the progression
  row in one transaction. Splitting them lets a crash between them either replay events already
  applied or skip events never applied, permanently and with nothing to signal it. Sessions are
  collected rather than merged, and each flushes its own operations into the shared transaction,
  because an operation is configured against a session as its storage context and that is what
  carries tenancy.
- **The loader diverges from the stream reads on purpose.** Fisher's stream reads skip an
  unresolvable `dotnet_type` unconditionally so a deployment can still read events it does not know
  about; the daemon must not, because silently skipping one leaves the projection permanently wrong.
  It honours `SkipUnknownEvents` and otherwise throws `UnknownEventTypeException`, which implements
  JasperFx's `IEventFailureContext` so the shard failure can be classified without knowing Fisher's
  exception types.
- **The generic interface's `IEventDatabase` parameter is ignored throughout.** Marten and Polecat
  resolve a connection string off it because they can be database-per-tenant; a SQLite store is one
  file. Separate-database tenancy is what would make those parameters start mattering.
- **Rebuild teardown checks for the table in C#, not in SQL.** SQLite resolves a table name when it
  *prepares* a statement, so a `where exists (select 1 from sqlite_master ...)` guard on the delete
  fails before the guard runs. Names come back from `sqlite_master` first and missing tables are
  skipped. Both halves of teardown — progression and documents — run in one transaction, because
  clearing progress without clearing documents replays a projection on top of rows it already wrote.

WAL is what lets the daemon read while a session writes. It is on by default via
`SqlitePragmaSettings.Default`; `BuildProjectionDaemonAsync` warns when it is not, because without it
the daemon and every writer serialize against each other and that presents as a slow projection
rather than as a misconfiguration.

### Document storage layout

Weasel.Storage supplies the selectors and the write operations but **not** an
`IDocumentStorage<T,TId>` implementation to hold them together — Marten and Polecat each write their
own, and `FisherDocumentStorage<TDoc,TId>` is Fisher's. Around it:

- `DocumentProviderRegistry` (`IProviderGraph`) caches one `DocumentProvider<T>` per document type,
  holding four flavors. Fisher has no dirty tracking, so the identity-map storage takes that slot too.
- **Storages record; the session queues.** `IStorageSession` has no operation queue, so
  `IDocumentStorage.Store` only assigns an identity and registers the document; `FisherSession.Store<T>`
  queues the `Upsert`. A storage that tried to enqueue would have to know a concrete session type.
- The document is serialized when the batch runs, not when `Store` is called, so mutating it in
  between still takes effect — matching Marten.
- The read layout is a contract with the shared selectors: writeable flavors read `id` at column 0 and
  `data` at 1 with metadata from 2; the query-only selectors omit `id` and read `data` at 0.

### Document write SQL

`SqliteDocumentStorageDescriptorBuilder` emits four statements whose **column order and `?` order are
one contract**, because the shared closed-shape operations bind by position. Two different orders are
in play, and they are not the same:

- upsert / insert / overwrite — `[tenant,] id, data, client-side binders`, then the optional
  concurrency guard (`ClosedShapeUpsertOperation.BindPreOnConflictParameters`)
- update — `data, client-side binders, id, [tenant]`, then the guard. **The id moves from the front
  to the back**, because it is a `WHERE` term rather than a value.

A server-side binder contributes a column and an expression but no parameter mark, which is what lets
`last_modified` sit in the middle of the column list without shifting a slot. That holds only while
its `ValueSql` contains no `?`.

Verified against SQLite 3.51 before anything was built on it: `ON CONFLICT … DO UPDATE SET … WHERE …
RETURNING id` parses, and when the guard does not match it returns **no row** and leaves the row
untouched — which is exactly what the Optimistic operation's postprocessing reads as a concurrency
failure. The `DO UPDATE SET` clause assigns from `excluded.*` for every column rather than repeating
each binder's `ValueSql`, so the update branch cannot drift from the insert branch.

### Hi-Lo sequences

Numeric document identities go through `Storage/Sequences/`: `fi_hilo` (one row per sequence),
`HiloSequence` over the shared `Weasel.Core.Sequences.HiloSequenceBase`, and `SequenceFactory` as the
store's `ISequenceSource` — which is what `FisherDatabase.SequenceFor` delegates to and what the
shared `HiloIntIdentification` / `HiloLongIdentification` strategies resolve through.

Three things worth knowing:

- **Advancing the hi is one statement, not read-then-compare-and-swap.** Marten calls a stored
  function; Polecat reads `hi_value`, updates it guarded by the value it just read, and retries when
  the row moved. SQLite's upsert does the whole thing atomically —
  `insert … on conflict (entity_name) do update set hi_value = fi_hilo.hi_value + 1 returning
  hi_value` — so there is no window to lose. The retry loop that survives in `HiloSequence` is there
  only to honour the base class's "a negative hi means try again" contract.
  `concurrent_stores_never_hand_out_the_same_id` pins it: six stores over one file with `MaxLo = 5`
  must between them produce exactly 1..300, no duplicates and no gaps. Rewriting the advance as a
  read followed by an unguarded update fails it every run.
- **The sequence creates `fi_hilo` itself.** An id is assigned inside `session.Store(document)` —
  `IIdentification.AssignIfMissing` is synchronous and returns before any commit — so the commit-time
  `EnsureDocumentTablesAsync` path is far too late. `HiloFeatureSchema` also puts the table in the
  store's migration, but only when a registered mapping actually has a numeric id; the runtime does
  not depend on that having run. `AutoCreate.None` is honoured in both places.
- **`AdvanceToNextHiSync` is not an oversight.** It exists because `AssignIfMissing` is synchronous,
  and it runs through `StoreOptions.ResiliencePipeline` exactly as the async path does — otherwise
  half of all `fi_hilo` access would skip the SQLITE_BUSY retry.

Sequences are cached by sequence *name*, not by document type, so two types sharing a configured
`SequenceName` share one allocation instead of each holding a private lo range over the same row.

### `Advanced` and cleaning

`DocumentStore.Advanced` carries `Clean` (`IDocumentCleaner`), `ResetAllDataAsync` and
`ResetHiloSequenceFloorAsync<T>`. Two things about the cleaner are SQLite-specific rather than
arbitrary:

- **Scoping is by table prefix**, because there is no schema to scope to. `PrefixFor(schemaName)` is
  the entire isolation boundary between two logical stores in one file, and
  `cleaning_one_logical_store_does_not_touch_another_in_the_same_file` is what holds it.
- **Table matching is done in C#, not with `LIKE`.** `_` is a single-character wildcard in SQL's
  LIKE and every Fisher prefix contains one, so `like 'fi_%'` would happily match a table called
  `fixtures`. Names come back from `sqlite_master` and are filtered with `StartsWith`.

`CompletelyRemoveAllAsync` calls `FisherDatabase.ForgetEnsuredTables()` afterwards. Without it the
"this document table already exists" cache would still claim tables that were just dropped, and the
next `Store` would skip its migration and write to nothing.

- **Projection side effects.** `GetOrStartMessageSink` throws, so `PublishMessageAsync` on the
  projection batch does too.
- **An async projection that appends events of its own.** `FisherProjectionBatch.QuickAppendEvents`
  and friends throw — they need the append planner's version assignment and sequence read-back inside
  the batch's transaction.
- **Dead letters.** `StoreDeadLetterEventAsync` throws, so a failing event stops its shard.
- Multi-tenancy beyond a tenant id column, subscriptions, DI registration.
- The two event-rewrite members in `EventOperations.Unsupported.cs`.

### The `IEventStoreOperations` surface

`EventOperations` implements JasperFx's `IEventStoreOperations` in full — the interface the
cross-store compliance suites route everything through, so declaring it is what makes
`EventStoreComplianceFixture.EventsFor(session)` possible at all.

Everything reachable without document storage is real: `FetchForWriting` rebuilds the aggregate by
live aggregation (there is no snapshot to read instead), `WriteToAggregate` is fetch + callback +
`SaveChangesAsync`, and `ProjectLatest` folds the session's pending events on top of the committed
state.

What throws lives in **`EventOperations.Unsupported.cs`**, one file on purpose — the DCB tag members
and the two event-rewrite members. That file shrinking is the progress measure. `FetchForWriting<T,
TId>` and `FetchLatest<T, TId>` are partial: they accept an id that is already the stream identity
type and throw for anything else, because in the siblings that overload is the natural-key and
strong-typed-id entry point.

One Fisher-specific hazard in this area: pending streams are tracked in a **dictionary keyed by
identity**, where Polecat uses a list. `FetchForWriting` must therefore reuse an already-tracked
`StreamAction` rather than construct a fresh one — replacing the dictionary entry would silently drop
events an earlier `Append` had queued for the same stream in the same session.

### Live aggregation

`EventGraph` implements `IAggregationSourceFactory<IQuerySession>`: given an aggregate type it closes
`Fisher.Projections.SingleStreamProjection<TDoc, TId>` (which only closes the JasperFx base over
Fisher's session types) and asserts validity. `EventGraph.AggregatorFor<T>` is the single seam every
live aggregation goes through, and it defers to `FisherProjectionOptions.AggregatorFor<T>` — the
shared `ProjectionGraph` implementation, which checks registered projections first and only then falls
back to this factory. Fisher cannot register a projection yet, so auto-discovery is still the whole
story; routing through the graph is what makes a registered projection win once there is one.

Two things that are not obvious:

- **Conventional `Apply`/`Create`/`ShouldDelete` dispatch is compile-time only.** JasperFx's source
  generator emits the dispatcher; there is no runtime fallback. The generator keys it on
  `(TDoc, TId)`, resolving `TId` from the aggregate's identity member — so an aggregate with no `Id`
  gets no dispatcher at all. `AggregateIdentity.ResolveIdType` therefore *requires* an identity member
  and says so, rather than defaulting to the stream identity primitive and failing later with a
  message about a missing generated dispatcher. The generator runs in the assembly that defines the
  aggregate, which is why `Fisher.Tests` references `JasperFx.Events.SourceGenerator`.
- **`TId` is the aggregate's own id type, not the stream identity primitive.** They coincide for a
  plain `Guid Id`, but a strong-typed id is a wrapper struct and the generated dispatcher is keyed on
  the wrapper.

`AggregateIdentity` resolves identity through the shared `JasperFx.DocumentIdentity` helper. When
`DocumentMapping` arrives it should resolve identity *through* `AggregateIdentity` rather than beside
it, so the live-aggregation and snapshot paths cannot disagree about what `TId` is.

### The `IEventStore` surface

`DocumentStore` is `partial`; its `IEventStore` implementation lives in
`DocumentStore.EventStore.cs` and is written **explicitly** (`Uri IEventStore.Subject => ...`), as
Polecat does, so none of a tooling-only surface lands on the store's own public API.

Most of `IEventStore` is default-implemented by JasperFx and deliberately left alone. Fisher supplies
the required members plus the three things `EventStoreExplorerCompliance` exercises:
`GetRecentStreamsAsync`, `GetStreamMetadataAsync` and `TryCreateUsage`. The required members it
cannot honour — `OpenReadOnlyEventStore`, `CompactStreamAsync` — throw naming their milestone rather
than returning an empty result a monitoring tool would render as "no data". Same discipline as
`EventOperations.Unsupported.cs`. The generic half of the interface, and `BuildProjectionDaemonAsync`
with it, lives in `DocumentStore.Daemon.cs` — see "The async daemon" below.

Three SQLite-specific points:

- **`GetStreamMetadataAsync` parses the incoming Guid string and re-renders it.** That is not
  redundant. `fi_streams.id` holds the lowercase canonical form and SQLite's default collation is
  case-sensitive, so an uppercase Guid string matches nothing — the same trap
  `SqliteGuidIdentification` exists for. The shared compliance suite cannot catch a regression here
  because it only ever passes `Guid.ToString()`, which is already lowercase;
  `event_store_explorer.stream_metadata_is_found_regardless_of_guid_casing` is what pins it.
- **Recent-stream ordering is `order by timestamp desc` over TEXT** — a string sort, correct only
  while `SqliteTimestamp.Format` stays fixed-width, UTC and millisecond-precision. A format with a
  variable-width offset or no sub-second component would silently mis-order streams written in the
  same second.
- **Rows are materialised inside the resilience pipeline, not streamed out of it.** A retried
  `SQLITE_BUSY` re-executes the whole delegate, so yielding a live reader to the caller would let a
  retry resume against a connection the previous attempt had already disposed.

`fi_streams`' column projection stays in `FisherStreamsRowReader` — `ReadStreamSummary` and
`ReadStreamMetadata` sit beside `Read`, so all three move together when the table's shape does.
`ReadStreamMetadata` returns an **empty tag dictionary, not null**: the record declares `Tags`
non-nullable, and returning null there is what polecat#412 was.

### LINQ

Ported from Polecat, which owns `Polecat.Linq.SqlGeneration` itself rather than taking it from
`Weasel.SqlServer` — so Fisher carrying its own fragment set is the mirror, not a divergence, and no
upstream Weasel change was needed. The fragments bind to `Weasel.Core.SqlGeneration.ISqlFragment`
(the neutral one the storage layer already uses) rather than a Fisher-local copy, so a parsed
predicate can be handed to `FisherDocumentStorage.FilterDocuments`.

`session.Query<T>()` supports `Where`, the four ordering operators, `Take`/`Skip`, and async
terminals. Anything else throws `BadLinqExpressionException` naming the operator rather than falling
back to client-side evaluation.

The port is **smaller** than its source. `json_extract` returns a JSON number as INTEGER, a float as
REAL, a string as TEXT and `true`/`false` as INTEGER 1/0 — unlike `JSON_VALUE`, which always returns
`nvarchar`. So there is no `CAST` anywhere, and Polecat's `SqlTypeMap` / `BuildTypedLocator` /
`SupportsReturning` machinery has no analogue; `TypedLocator` and `RawLocator` are the same string.

Four SQLite decisions that are easy to get wrong and fail silently:

- **String predicates use `instr`/`substr`, not `LIKE`.** SQLite's `LIKE` is case-*insensitive* for
  ASCII while `=` is case-*sensitive*, so a LIKE-based `Contains("frodo")` matches `"Frodo"` on data
  where `== "frodo"` does not — a query surface contradicting itself, and not what .NET's ordinal
  `string.Contains` means. `_` and `%` are also `LIKE` wildcards needing an `ESCAPE` clause (Polecat's
  `[_]` bracket form is T-SQL-only) — the same trap as the document cleaner's table matching.
- **Paging is `limit m offset n`.** `TOP(n)` and `OFFSET … FETCH NEXT` collapse to one form; T-SQL's
  `ORDER BY (SELECT NULL)` filler is not emitted because SQLite does not need it and it would impose a
  sort nobody asked for. An offset with no limit must say `limit -1` first — a bare `offset` is a
  parse error.
- **Dates support equality but not ordering.** `DateMember.AllowsRangeComparison` is false and both
  the where parser and `OrderBy` refuse. System.Text.Json trims trailing fractional zeros and keeps
  the original offset, so `12:34:56-05:00` sorts before `12:34:56.789+00:00` while being five hours
  later. The literal for an equality comparison is rendered *through the store's own serializer*,
  because no format string reproduces STJ's trimming. **Lifting this does not need a duplicated
  column** — `strftime('%Y-%m-%dT%H:%M:%f', json_extract(...))` normalises the offset inline and
  keeps milliseconds, verified against 3.51; see fisher#1. A duplicated column is what would make
  the result *indexable* (fisher#2), which is a separate concern. This is documents only: the
  `fi_events`/`fi_streams` timestamp columns use `SqliteTimestamp`'s fixed-width UTC format precisely
  so they *do* sort as text.
- **`array.Contains(x)` binds to `MemoryExtensions.Contains(ReadOnlySpan<T>, T)`**, not
  `Enumerable.Contains`, so `EnumerableContains` matches on the call's shape rather than its declaring
  type. The span operand cannot be evaluated by compiling a lambda either — `ReadOnlySpan<T>` is a ref
  struct and cannot be returned as `object` — so it is unwrapped back to the array first.

The provider takes both the column list and the materializer from the **query-only** closed-shape
storage (`ISelectClause.SelectFields()` / `BuildSelector()`) rather than hand-writing `select data`,
which is what keeps the query path's read layout aligned with `LoadAsync`'s.

### DCB tags

One `fi_event_tag_<suffix>` table per registered tag type, composite primary key leading with
`value`. That key is load-bearing twice over: a tag query filters on `value`, so leading with it makes
the lookup a range scan; and it is what lets both the append path and `AssignTagWhere` write
`on conflict do nothing` instead of reading first, which is where idempotency comes from.

**Tags are written after the batch and inside its transaction** (`FisherSession.SaveChangesAsync` →
`EventTagWriter`). A tag row is keyed by the `seq_id` SQLite assigns on insert, which Fisher only
learns from the trailing sequence read-back in `FisherQuickAppendEventsOperation` — so there is
nothing to write until the appends postprocess. Committing separately would leave an event visible
but untagged, and to a tag query that is indistinguishable from an event that was never tagged.

Query shape: each condition is a `seq_id in (select seq_id from <tag table> where value = ?)`
subselect, OR'd. **Subselects rather than joins**, because joining several tag tables multiplies rows
when one event carries two matching tags and the caller expects each event once. Ordering is by
`seq_id`, since a tag query spans streams and version is not a global order — which is also why
`FisherEventsRowReader.ReadEventAcrossStreams` exists, taking stream identity from the row rather
than from the hydration context the single-stream reads use.

Guid tag values bind as lowercase canonical text, same trap as `SqliteGuidIdentification`.

**`AssignTagWhere` is a client of the LINQ `WhereClauseParser`**, as Marten builds it. The only piece
it needed was `EventMemberFactory`, an `IMemberResolver` resolving `IEvent` members to `fi_events`
columns instead of `json_extract` paths — which is why `IMemberResolver` is an interface rather than
a concrete type. Note `IEvent.Timestamp` permits range comparison where a document's
`DateTimeOffset` does not: same CLR type, but `fi_events.timestamp` is `SqliteTimestamp`'s
fixed-width UTC format, chosen precisely so a string comparison is an instant comparison.

**The consistency check runs inside the write transaction, before anything is written.** Checking
after would be checking against the session's own appends; checking outside the transaction would
prove nothing, because `BEGIN IMMEDIATE` is what holds the write lock. A boundary over an empty
result still enforces consistency — `LastSeenSequence` is 0 and any later matching event exceeds it.

`IBatchedQuery` matches the siblings' shape but exists **for API parity, not for speed** — a batch
elsewhere collapses network round trips, and SQLite is embedded, so there are none to collapse. It is
carried so DCB code ports between stores unchanged and so Fisher enrolls in the shared batched-query
tests with a real implementation rather than a test-only shim; `DcbTagQueryAndConsistencyCompliance`
has no opt-out flag, so declining would have cost all 26 of its tests. Implemented without statement
coalescing on purpose. Do not present it as a performance feature.

### Compliance suites

**Fisher is enrolled, in full.** `JasperFx.Events.ComplianceTests` is referenced unconditionally —
the old `$(EnableComplianceTests)` gate is gone. **All 17 suites are live, 124 shared tests.**
`AsyncDaemonCompliance` was the last one in.

The mechanics, because they are not what the package's name suggests:

- **Every suite compiles; only the subclassed ones run.** Enrolling is one empty class in
  `fisher_event_store_compliance.cs`. Not enrolling costs nothing at runtime — but the shared source
  still has to compile, which is why all four global aliases in `ComplianceAliases.cs` must resolve
  even for suites Fisher cannot pass.
- Every suite now compiles. The two `EventProjection*` suites were once `<Compile Remove>`d because
  they call `IDocumentSession.Store` and `IQuerySession.LoadAsync`; document storage made them
  compile and they are enrolled.
- **`FisherComplianceFixture` throws `NotSupportedException` naming the milestone** for each member
  Fisher cannot honour. Only `LoadDocumentAsync` for a strongly typed id still does. Enrolling a
  suite prematurely therefore fails loudly rather than passing on a stub.
- `CleanEventDataAsync` now delegates to `Advanced.Clean.DeleteAllEventDataAsync`. It is called
  before every test, so it cannot throw the way the unsupported members do — hence the null guard
  rather than the `Store` accessor.

### Session metadata on appended events

`AppendPlanner.ApplySessionMetadata` copies the session's correlation id, causation id, user name and
headers onto each event that does not already carry its own, each gated on its `Enable*` option. The
session seeds correlation/causation from `Activity.Current` (`RootId` and `ParentId`) at construction,
so tracing context reaches events with no application code; an explicit assignment afterwards wins.

This duplicates the private `StreamAction.ProcessMetadata`, which is normally reached through
`StreamAction.PrepareEvents` — and Fisher **cannot** use `PrepareEvents`. In Quick mode it numbers
events only when `ExpectedVersionOnServer` is already set, because Marten and Polecat let the database
assign versions while Fisher numbers them client-side from the version it just read. Pre-setting
`ExpectedVersionOnServer` to make it number them would make the optimistic-concurrency check inside
the same method compare that value against itself and pass unconditionally. Keeping version
assignment and metadata application apart is what keeps the guard real; the cost is that a new
metadata field in JasperFx will not reach Fisher's events until this method learns about it.

`ComplianceEventProjection` binds to `Fisher.Projections.EventProjection`. Its one required member,
`storeEntity`, is now an ordinary `IDocumentSession.Store` onto the session the events are committing
in, so a `Create`/`Project` method's return value lands in the same transaction as the event that
produced it. `inline_event_projections` covers it directly — note that a conventional-method
projection class must be declared `partial`, because the dispatcher is source-generated into it and
there is no runtime fallback.

## Conventions

- Test files and classes are `snake_case` (`appending_events.cs`). CS8981 is suppressed repo-wide.
- xUnit v3 on Microsoft Testing Platform, no VSTest bridge. Test projects need `OutputType=Exe` and
  must not have top-level statements. Pass `TestContext.Current.CancellationToken` to async calls.
- MTP extension packages stay on the **1.x** line — xunit.v3 3.2.2 is built against
  Microsoft.Testing.Platform 1.x and 2.x dies at startup with a `TypeLoadException`.
- **CI runs the test executable directly**, not `dotnet test` — `dotnet test` cannot emit TRX under
  MTP. `--logger "trx;..."` and `-- --report-trx` both run, exit 0, and silently write nothing;
  `--report-trx` is rejected outright by MSBuild. See `.github/workflows/fisher.yml`.
- Mirror Marten's public API surface where it costs nothing; mirror Polecat's internals where the
  concern is not dialect-specific.
- Database execution should go through `StoreOptions.ResiliencePipeline`.
- **Never call `SqliteConnection.ClearAllPools()`.** It disposes every pooled connection in the
  process, and xUnit runs test collections in parallel — one test's cleanup will take out another
  with `ObjectDisposedException: SQLitePCL.sqlite3`, intermittently enough to look like a flake.
  `TemporaryDatabase.Dispose` clears only its own connection string's pool.

## Related codebases

| Codebase | Path | Use |
|---|---|---|
| Polecat | `~/code/polecat` | **The closest template** — SQL Server sibling; mirror its structure |
| Marten | `~/code/marten` | PostgreSQL reference implementation |
| Weasel | `~/code/weasel` | `Weasel.Sqlite` + `Weasel.Storage` sources |
| JasperFx | `~/code/jasperfx` | Core + Events framework (local clone may lag the pinned package) |
