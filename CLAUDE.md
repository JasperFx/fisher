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
- **`pragma_table_info` omits generated columns.** A table carrying one never converges through
  Weasel's delta detection — see "Duplicated fields".
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
- **Dead letters** — `fi_dead_letters`, so `SkipApplyErrors` quarantines a poison event instead of
  stopping its shard
- **Projection side effects** — `IMessageOutbox` / `IMessageBatch`, with both commit paths bracketing
  their transaction; the default outbox drops every message
- **Event-emitting async projections** — a projection's raised events are planned and appended inside
  the batch's own transaction
- **Multi-stream projections** — `MultiStreamProjection<TDoc, TId>`, with `Identity` / `Identities` /
  `FanOut` grouping, inline and async
- **Subscriptions** — `Projections.Subscribe(...)`, a daemon shard driving arbitrary code over each
  range of events, its writes committing in the batch's transaction
- **DI registration** — `AddFisher(...)`, scoped sessions, and hosted services for schema application
  and the async daemon
- **Flat-table projections** — `FlatTableProjection`, projecting into a plain relational table
  through declarative column mappings rather than into a document
- **Soft delete** — `is_deleted` / `deleted_at`, `HardDelete`, `DeleteWhere` / `HardDeleteWhere` /
  `UndoDeleteWhere`, and the `MaybeDeleted` / `IsDeleted` / `DeletedSince` / `DeletedBefore` query
  operators
- **Duplicated fields** — `Schema.For<T>().Duplicate(x => x.Name)`, as an indexed SQLite `VIRTUAL`
  generated column, so a predicate against that member is served by an index
- **User-declared indexes** — `Schema.For<T>().Index(x => x.Name)` / `.UniqueIndex(...)`, as SQLite
  expression indexes that add no column at all
- **Document metadata member mapping** — `guid_version`, `last_modified`, `is_deleted` and
  `deleted_at` projected back onto members of the document, by interface, attribute or DSL
- **Strong-typed identities** — a wrapper around any of the four id types, as an aggregate's identity
  and as a document's
- **Event rewriting** — `OverwriteEvent`, `CompletelyReplaceEvent`, event data masking through
  `Advanced.ApplyEventDataMaskingAsync`, and stream compacting via `CompactStreamAsync<T>`

Not implemented yet — do not assume these work:

- **Document storage** — `Store`/`Insert`/`Update`/`Delete`/`LoadAsync`/`LoadManyAsync`, soft delete,
  duplicated fields, user-declared indexes, numeric revisions, metadata member mapping and LINQ all
  work over Guid, string, int and long ids, in the same unit of work and transaction as event appends.
  Document hierarchies work too — one table per hierarchy, keyed on a `doc_type` alias.
- **Event rewriting** — all of it works: `OverwriteEvent` / `CompletelyReplaceEvent`, event data
  masking and stream compacting.

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

### Flat-table projections

`Projections/Flattened/` — a `FlatTableProjection` writes into a plain relational table keyed on the
stream, through `Project<T>(map => …)` mappings (`Map`, `SetValue`, `Increment`, `Decrement`) and
`Delete<T>()`. Ported from Polecat's, which is the closest template; the mapping API and the column-map
shapes are its, and four things are not.

- **One `insert … on conflict … do update`, where Polecat emits a `MERGE`.** SQLite has had upsert
  syntax since 3.24, so the matched and not-matched branches are two clauses of one statement — which
  is also why a parameter appearing in both is bound once by name rather than duplicated. **An
  unqualified column on the right of the update assignment is the pre-update row**, which is what makes
  `"a" = "a" + @p1` an increment; `excluded."a"` would be the value the insert branch would have
  written. Polecat spells the same thing `target.[a]`.
- **The table is created by the migration, not lazily on first write.** Registering the projection puts
  a `FlatTableFeatureSchema` into the store's feature set, so `ApplyAllConfiguredChangesToDatabaseAsync`
  creates it with everything else and `AutoCreate.None` is honoured for free. Polecat issues a CREATE
  TABLE from inside its first apply, which works but routes around the store's schema policy.
- **The physical name folds the store's logical schema in, and is resolved in `DocumentStore`'s
  constructor** rather than in the projection's. SQLite has no schemas, so the prefix *is* the isolation
  boundary between two logical stores in one file — a flat table that kept the bare name would be
  silently shared by both. The projection's constructor cannot see the store, and the projection is
  usually registered in the same configuration lambda that sets `DatabaseSchemaName`, in either order,
  so the fold happens once the options are final. The `fi_` family prefix is *not* applied:
  `FisherTableNaming.UserTableName` exists precisely because that prefix marks a table Fisher owns the
  shape of, and a flat table's shape is the projection's. The rename needs `FlatTable : Table`, because
  `SchemaObjectBase.Identifier` has a protected setter and Weasel's `MoveToSchema` only changes the
  qualifier.
- **Rebuild teardown is told the table name directly.** `PublishedTypes()` is empty — a flat table's
  rows are not documents — so the mapped-type sweep in `TeardownExistingProjectionStateAsync` cannot see
  it, and `IPublishesTables` is what closes that. Without it a rebuild replays onto the rows the
  previous run left, which the compliance suite catches with a row the replay cannot recreate.

The primary key holds a stream id, so it is TEXT and bound through the lowercase-canonical conversion —
the `SqliteGuidIdentification` trap, in the one place a flat table meets it. Bound any other way, the
second event on a stream inserts a second row instead of updating the first.

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
- **The session's operation queue is guarded, because the daemon is not a single caller.** JasperFx's
  `ExecutionStage` fans its executions out with `Task.WhenAll` and they all queue onto the *same*
  Fisher session, so two projection slices can call `QueueOperation` at the same instant.
  `List<T>.Add` is not thread-safe and fails silently here — two concurrent adds can leave the count
  incremented once, so one slice's document write never reaches the batch, which then commits the
  progression row for a range whose documents were only partly written. **That was fisher#13**, and it
  presented as a multi-stream rebuild intermittently missing one slice's document. Note how closely it
  rhymes with fisher#12: same silent outcome, one layer up. `concurrent_operation_queueing` pins both
  the add and the take.
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

### Event-emitting async projections

JasperFx's `EventSlice.BuildOperations` drives three synchronous members on the projection batch —
`QuickAppendEventWithVersion`, `UpdateStreamVersion`, `QuickAppendEvents`. **All three do the same
thing in Fisher: record the `StreamAction` and let `ExecuteAsync` plan it inside the transaction.**

That is a deliberate divergence from Marten, which queues three different storage operations. Two
reasons, both SQLite-shaped:

- **The version has to come from a read under the write lock.** The slice pre-assigns versions
  client-side from its own event count, which is only the stream's version when the projection has
  seen every event on it. Fisher's `AppendPlanner` re-reads inside the batch's `BEGIN IMMEDIATE` and
  the optimistic guard runs there, so a projection raising events onto a stream another writer has
  moved on fails the batch instead of writing a wrong version.
- **Tags need the trailing sequence read-back.** Routing raised events through
  `FisherQuickAppendEventsOperation` is the only thing that supplies the `seq_id` a tag row is keyed
  by. Queuing Weasel's bare per-event operations would silently make raised events untaggable.

Funnelling all three members into one list works because the `StreamAction` JasperFx hands over
already carries every raised event, and the single-stream-start path passes the *same* action
instance to each call — reference identity is what dedupes them.

**Polecat no-ops these three rather than throwing**, so an event-raising projection there drops its
events with no signal. Do not copy that.

### Subscriptions

`Projections.Subscribe(subscription)` (fisher#21) — a daemon shard that hands each range of events to
arbitrary user code rather than to a projection. `Subscriptions/ISubscription.cs` carries the
interface, a `SubscriptionBase` closing JasperFx's `JasperFxSubscriptionBase` over Fisher's session
pair, and the wrapper that presents a bare `ISubscription` as one; `DocumentStore.Daemon.cs`
implements `ISubscriptionRunner<ISubscription>`.

- **`SubscriptionExecution<T>` resolves the runner with a soft `storage as ISubscriptionRunner<T>`
  cast.** Not implementing it is not a compile error — it throws when a subscription is registered,
  which is why subscriptions read as *absent* rather than broken before this. The `storage` argument
  is the **store**, not the database: JasperFx has two `BuildExecution` overloads and the one the
  daemon reaches passes `_store`.
- **The subscription's session is the batch's**, taken from `SessionForTenant`, which both opens it
  and enrols it. So a subscription's own writes commit in the same transaction as the progression
  row: it cannot advance past a range whose writes rolled back, nor commit writes for a range it will
  replay. **That guarantee stops at Fisher's database** — an HTTP call or a bus publish is
  at-least-once and nothing can make it atomic with a SQLite commit.
- **The post-commit listener runs after `ExecuteAsync` returns, outside the resilience pipeline.** A
  retried `SQLITE_BUSY` re-executes the whole batch delegate, so a listener invoked inside it fires
  twice for a transaction that already committed — the property fisher#4 established for the outbox
  and fisher#12 for the batch's own input.
- **`IDaemonChangeListener` is JasperFx's, not a Fisher type.** It was lifted out of Polecat as the
  canonical shape, so Polecat's local `IChangeListener` is the older spelling of the same thing; new
  code should not copy it.
- **There is no inline equivalent**, deliberately: "inline" would just be code in the caller's own
  unit of work. A subscription needs the daemon running — `AddAsyncDaemon()` hosts it with everything
  else.

One test-shaped trap worth recording, because it presented as an intermittent: **`WaitForNonStale`
does not imply the post-commit listener has run.** The progression row is written *inside* the batch's
transaction, so non-stale is true the moment that commits — strictly before the listener. A test that
waits on non-staleness and then asserts the listener fired fails roughly one full-suite run in
several. Wait on the listener's own signal.

### Projection side effects

`IMessageOutbox` vends an `IMessageBatch` per unit of work; a projection's `PublishMessage` buffers
into it, and the batch flushes in whichever hook its delivery guarantee needs. Both of Fisher's commit
paths bracket their transaction the same way — `FisherSession.SaveChangesAsync` and
`FisherProjectionBatch.ExecuteAsync` each call `BeforeCommitAsync` as the last thing inside the
transaction and `AfterCommitAsync` after it commits. Type names match Polecat's exactly, because
messaging is not dialect-specific and projection code should port between the stores unchanged.

- **`NulloMessageOutbox` is the intended end state, not a placeholder** (fisher#8, closed wontfix).
  It drops messages rather than throwing, which is the sibling behaviour: publishing means something
  only once a bus is wired in, and a store that threw would make every projection that *might* publish
  untestable without one. **Fisher will not ship a delivery mechanism of its own.** Marten and Polecat
  both delegate to Wolverine and the seam is what a bus integration plugs into; a second answer here
  would be a Fisher-only subsystem — retry policy, poison handling, drainer coordination — that
  projection code could not port to the siblings. So "a published side effect goes nowhere until an
  `IMessageOutbox` is supplied" is a stated contract, and should be documented as one rather than
  filed as a gap.
- **The batch is created lazily and never reused across units of work.** A session that publishes
  nothing never asks the outbox for one, so both hooks stay no-ops for the common case; and clearing
  it after commit is what stops a second `SaveChangesAsync` re-flushing the first one's messages.
- **`AfterCommitAsync` runs outside the resilience pipeline** in the projection batch. A retried
  `SQLITE_BUSY` re-executes the whole delegate, so a post-commit publish inside it would fire twice
  for a transaction that had already committed.
- **The same property bit the batch's own input, and that one was silent** (fisher#12). Everything
  the retried delegate reads has to survive being read twice, so the session's operations are taken
  *before* the pipeline (`TakePendingOperations`) and executed from that snapshot inside it. Draining
  them inside — which is what `FlushOperationsAsync` used to do — left a retry with nothing to write
  while the progression row still committed, advancing a projection past events whose documents were
  never written, with no error anywhere.
- Hook *order* is not the invariant — both hooks would fire in order even if both ran before the
  commit. What is pinned is what the rest of the database can see when each runs, probed over a
  separate connection: invisible at `BeforeCommit`, visible at `AfterCommit`. Verified by moving the
  call, in both paths.

`IProjectionBatch.PublishMessageAsync` hands over an `object`, but `IMessageSink.PublishAsync<T>` is
generic, so `MessagePublishing` closes it over the runtime message type and caches the compiled
delegate per type. Polecat does the same via polecat#46, with `FastExpressionCompiler` where Fisher
uses the BCL's `Expression.Compile` — not worth a dependency for one call site.

### Event rewriting

`Events/Protected/` — three operations that mutate events already committed, and the foundation both
event data masking (fisher#9) and stream compacting (fisher#10) are clients of. Ported from Polecat's
`Events/Protected/`, which is the same three.

- `OverwriteEventOperation` rewrites what an event **says**: `data`, plus `headers` where the store
  keeps them. Everything placing the row in the stream and in the global order is untouched.
- `ReplaceEventOperation` rewrites what an event **is**: `data`, `type`, `dotnet_type` and a fresh
  `id`, because it is no longer the event that was appended. Stream, version and sequence stay, and
  the row's **tag rows are deleted** — a tag describes the event that was appended, so carrying it
  over would let a tag query return the replacement as though it were the tagged event.
- `DeleteEventsOperation` removes rows by sequence.

All three queue onto the session, so a rewrite commits in the same transaction as everything else —
which is what lets masking rewrite a batch atomically. Four decisions in them:

- **The row is matched by `seq_id`, never by `id`.** The sequence is the primary key; `id` has no
  index, so matching on it would turn every rewrite into a table scan.
- **`ReplaceEventOperation` does not move the timestamp, where Polecat's does.** `fi_events.timestamp`
  is what `FetchStreamAsync`'s timestamp bound and the daemon's timestamp floor read, and both assume
  it rises with the sequence. Moving one row's timestamp forward puts the column out of order with
  `seq_id`, and a bounded read then returns a set that is neither the old answer nor the new one.
  Polecat can afford it because its timestamp column is not load-bearing in the same way.
- **`DeleteEventsOperation` clears tag rows before events.** `fi_event_tag_*` has a real foreign key
  to `fi_events(seq_id)` and Weasel's default profile enforces it, so the other order fails with
  `FOREIGN KEY constraint failed` — the same ordering `DeleteAllEventDataAsync` learned in fisher#6.
  `deleting_a_tagged_event_clears_its_tag_rows_first` fails with that exact error if the two are
  swapped, which was verified by swapping them. Dead letters are deliberately left alone: they have no
  foreign key precisely so they outlive the events they describe.
- **Deleting is safe only because `seq_id` is `AUTOINCREMENT`.** A bare `INTEGER PRIMARY KEY` aliases
  the rowid, which SQLite reuses after a delete, and a reused sequence below the daemon's high-water
  mark is an event no async projection would ever see. This is the operation that would otherwise have
  discovered that the hard way.

**None of it reaches an async projection that has already run.** The high-water mark is a sequence and
a rewrite does not move it, so a shard past the event never reads the new body and a projection
holding state derived from the old one stays wrong until it is rebuilt. Marten behaves the same way,
and it is why masking is a data-at-rest operation rather than a correction. Anything built on these
has to say so rather than leave it implicit.

`DeleteEvents` is internal: the safe uses of "delete these events" all go through a higher-level
operation that decides what replaces them, so there is no public surface for it.

### Stream compacting

`Events/Protected/StreamCompacting.cs` (fisher#10) — replaces a stream's events with a single
`Compacted<T>` event carrying the aggregate state. Reached by `session.Events.CompactStreamAsync<T>`,
and by `IEventStore.CompactStreamAsync` for tooling. Ported from Polecat's, which is the request
shape's other consumer.

**Reading it back needed nothing.** JasperFx's aggregator calls `Compacted<T>.MaybeFastForward` before
folding, so a stream starting with a snapshot event starts from that state and applies only what
follows — live aggregation, `FetchForWriting` and the daemon all inherit it.

- **The snapshot takes the last event's row**, so the stream's version does not move and the next
  append carries on from where it would have. The events below it are deleted.
- **The fetch is outside the write transaction, and that is safe** — which is worth stating because
  fisher#10 assumed the opposite. Compacting only touches events at or below a version it observed and
  an append only adds above one, so the two cannot overlap; two concurrent compactions of the same
  stream either write the same snapshot to the same row or find the target already gone and update
  nothing. There is no lost update to prevent, so there is no version guard — adding one would be
  theatre.
- **An aggregate that folds to null leaves the stream alone.** A stream deleted by its own
  `ShouldDelete` has no state to snapshot, and writing `Compacted<T>(null)` would be worse than doing
  nothing.
- **The tag rows of every compacted event go**, including the replaced one — see the replace operation
  above. Keeping the last event's tag while deleting the rest is the one outcome that is neither "the
  stream is still tagged" nor "the tagged events are gone".
- **`IEventStore.CompactStreamAsync` resolves the aggregate type from `fi_streams`**, where Polecat
  throws at that level despite implementing the generic overload. The type is on the row, so declining
  for every stream would be a worse answer than declining for the streams that genuinely record none —
  and that message names the generic overload.

Compacting is **one-way**: a projection rebuilt afterwards rebuilds from the snapshot rather than from
the history that produced it. `StreamCompactingRequest<T>.Archiver` is the hook for copying the events
somewhere first, and it runs before anything destructive is queued.

### Event data masking

`Advanced.ApplyEventDataMaskingAsync(...)` (fisher#9) — GDPR-style erasure, rewriting protected
information out of events already stored. JasperFx 2.41.0 lifted the *request* shape
(`IEventDataMasking`) because Marten's and Polecat's were identical; **the rule registry was not
lifted**, so `EventGraph.Masking.cs` is a port of Polecat's rather than a use of something shared.

The whole batch runs in one session, so an erasure is either done or not done — a partial one is a
compliance answer that is neither.

- **The two masking overloads do not have the same reach, and that falls out of the type system.**
  `ActionMasker` tests `@event is IEvent<T>` and `IEvent<out T>` is covariant, so an `Action` rule
  registered against an interface or base class reaches every event body implementing it. `FuncMasker`
  has to *assign* the replacement back and only the closed `Event<T>` exposes a setter, so a `Func`
  rule matches its exact type only. A `record` needs the `Func` overload (a `with` expression makes a
  new instance); a hierarchy-wide rule therefore has to be the mutating one.
- **`IncludeEvents` is the only selector translated to SQL.** The two `IncludeStream` filter overloads
  take `Func<IEvent, bool>` and are applied in memory to an already-fetched stream; `IncludeEvents`
  takes an `Expression`. That asymmetry is the interface's, not Fisher's — the parameter types say so.
  It runs through `EventOperations.QueryEventsAsync`, which is the same `WhereClauseParser` +
  `EventMemberFactory` pair `AssignTagWhere` uses. Marten spells that `QueryAllRawEvents()` and returns
  an `IQueryable<IEvent>`; Fisher takes a predicate, because its LINQ provider is built over document
  storage and an `IEvent` queryable would need a parallel provider to serve one caller.
- **An event is rewritten only when a rule matched it**, and headers follow the same gate — so
  `AddHeader` marks the events that were masked, not the events that were looked at.
- **The same event reached by two sources is masked once**, deduplicated by sequence.
  `an_event_reached_by_two_sources_is_masked_once` pins it with a deliberately non-idempotent rule,
  because an idempotent one would agree either way.
- **A batch naming no stream and no filter throws** rather than masking everything.

**Masking does not reach anything derived from the events.** The daemon's high-water mark is a
sequence and masking does not move it, so a projection that already folded the unmasked body keeps
what it derived — a snapshot, document or flat table holding the protected information still holds it
until that projection is rebuilt. Marten is the same, and this is why masking is a data-at-rest
operation rather than a correction. `masking_does_not_reach_a_snapshot_already_written` pins it as
documented behaviour rather than leaving it to be rediscovered as a bug.

### Dead letters

`fi_dead_letters` holds one row per event a shard could not apply and was configured to skip, with
`DeadLetterEvent`'s columns one for one so CritterWatch reads Fisher's the same way it reads
Marten's. Three decisions in it:

- **No foreign key to `fi_events`, deliberately** — the opposite of the tag tables. A tag is
  meaningless without its event; a dead letter is the record that something went wrong and has to
  survive the event being archived, compacted or cleaned away. A cascade would erase the evidence
  somebody came looking for. Nothing else removes them either, which is why
  `DeleteAllEventDataAsync` does.
- **The write goes on its own connection, outside the failing batch's transaction.** That batch is
  about to roll back; a dead letter written inside it would roll back with the very failure it is
  recording, and the shard would skip the event leaving no trace. The `storage` parameter the daemon
  offers as a session context is therefore ignored.
- **It is an upsert, not an insert.** JasperFx assigns the version-7 id at construction and the
  daemon retries the write in the background, so a retry landing after a successful first attempt
  carries the same primary key.

Ordering matters when clearing event data, and it is why `DeleteAllEventDataAsync` uses an ordered
pass rather than the cleaner's unordered one: `fi_event_tag_*` rows have a real foreign key to
`fi_events(seq_id)` and Weasel's default profile turns enforcement on, so clearing events first fails
with `FOREIGN KEY constraint failed` (fisher#6). Tags go first, dead letters last.

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

### LINQ aggregates and `Last`

`SumAsync` / `MinAsync` / `MaxAsync` / `AverageAsync`, `LastAsync` / `LastOrDefaultAsync`, and the
predicate overloads of the existing terminals (fisher#22).

**They never enter the expression tree.** Polecat builds a synthetic `MethodCallExpression` carrying
the selector and parses it back out; Fisher's terminal extensions take the selector as an argument, so
`LinqQueryParser` needed no change at all and stays what its doc comment says it is — a description of
the operator *chain*. The predicate overloads are `queryable.Where(predicate).XAsync()`, so they
compose rather than duplicating anything.

Four things are decisions rather than mechanics:

- **The empty result is the case that matters.** `sum`, `min`, `max` and `avg` all return NULL over no
  rows where `count` returns 0, so `Coerce` maps null to `default` before casting. An unguarded cast
  fails *only* on an empty result, which is how it would ship. `total()` would give 0.0 for an empty
  `sum` but is always REAL, so it is the wrong tool for the int and decimal overloads.
- **`Coerce` needs three explicit conversions, and they are exactly the types Fisher encodes rather
  than stores natively** — so `Convert.ChangeType` is broken precisely where this store differs from
  its siblings and nowhere else. An enum comes back INTEGER and needs `Enum.ToObject`; a timestamp
  comes back as `TimestampMember`'s `strftime` text, which has **no `Z` suffix**, so it goes through
  `SqliteTimestamp.FromDatabaseValue` whose `AssumeUniversal` is what makes that correct rather than
  local; a Guid comes back TEXT. Neither `DateTimeOffset` nor `Guid` is `IConvertible` at all — both
  throw `InvalidCastException` from inside `Convert.ChangeType`. All three were found by tests failing,
  not by inspection.
- **Two guards, which is why `AggregateFunction` is an enum and not a SQL string.** `Min`/`Max` need
  only that the member orders — a string minimum and a timestamp minimum are real answers — so they
  reuse `AllowsRangeComparison`, the same check `OrderBy` applies. `Sum`/`Average` need an actual
  number, because **SQLite's `sum()` over text returns 0 rather than failing**: summing a string-stored
  enum would report a plausible total for a column that has none. Enums are excluded from `Sum` even
  under `AsInteger`, since their numeric value is an identifier.
- **Paging wraps, for both.** `Take(5).SumAsync(...)` sums the page, so the paged query becomes a
  subquery projecting the member under an alias and the aggregate wraps it — the same trap
  `CountAsync` already documents. `Last` needs the *inverse*: `OrderBy(x).Take(3).LastAsync()` is the
  last of those three, so the reversal goes on the **outer** statement, not in place. Inverting in
  place answers about the whole table. Both verified by reverting them.

One consequence worth stating because it looks like a bug: **`MinAsync(x => x.Id)` over a Guid orders
by text, which is not .NET's `Guid` ordering.** `Guid.CompareTo` compares the first group as a *signed*
int, so the two disagree whenever the set straddles `0x80000000`. A test comparing against
`Enumerable.Min` would be a genuine intermittent; `min_and_max_over_members_that_are_not_numbers`
builds its expectation with `StringComparer.Ordinal` instead.

### Raw SQL

`IDocumentSession.QueueSqlCommand` (write, enrolled in the unit of work) and `session.AdvancedSql`
(read, typed results) — fisher#34. Ported from Polecat's `ExecuteSqlStorageOperation`,
`IAdvancedSql` and `AdvancedSqlResultReader`.

**This is worth more here than the same pair is on either sibling**, and for a structural reason: an
application using Fisher keeps its own tables in the *same file*, and SQLite permits one writer per
file. Without `QueueSqlCommand` its statements and Fisher's are two transactions on one file, so
"my rows and Fisher's, or neither" means taking the write lock twice and contending with yourself.
On Postgres or SQL Server the same method is a convenience.

**`SqliteParameterValue` is the piece with no sibling to port from.** Raw SQL is the one path where a
caller's value reaches a parameter with no conversion in between — every other write path converts
explicitly. Three CLR types bind to something Fisher never wrote, silently. Verified against
Microsoft.Data.Sqlite 10.0.9 and each arm verified load-bearing by removing it:

- **`Guid` binds UPPERCASE.** The recurring trap; matches zero rows.
- **`DateTimeOffset` binds as `2026-08-08 18:45:30.123+00:00`** — space-separated, original offset —
  against `SqliteTimestamp.Format`'s `2026-08-08T18:45:30.123Z`. Worth knowing precisely how this
  fails, because **a one-sided range test does not catch it**: at index 10 the stored form has `T`
  (0x54) and the raw binding a space (0x20), so the stored value sorts after *any* same-date raw bound
  whatever time it names. `a_timestamp_parameter_matches_fishers_stored_form` was written as a `>`
  against an earlier bound first, and passed with the conversion removed. It now asserts equality and
  a bound an hour *after* the event, which without the conversion wrongly includes it.
- **`decimal` binds as text**, which `json_extract`'s REAL never equals. Column affinity rescues the
  comparison against a *declared* column and there is no affinity inside `json_extract` — and that is
  how every undeclared document member is read, so it is the likely case rather than the exotic one.

Everything else is already right and is passed through: bool → INTEGER 1/0, enum → its integer value,
and the rest are what Fisher stores. **A declared `SqliteType` does not coerce the value** — the
provider binds by the CLR type of `Value` regardless — so Weasel's `AppendWithDbParameters` stamping
every placeholder TEXT is harmless rather than a fourth problem. `raw_sql_parameter_binding` pins that
too, because it is provider behaviour Fisher does not own.

Two divergences in the read half, both verified by reverting them:

- **A scalar is read with `GetFieldValue<T>`, not `GetValue` + `Convert.ChangeType`.** Polecat can use
  the latter because SQL Server hands back the CLR type; Fisher stores a Guid as text, and
  `Convert.ChangeType(string, typeof(Guid))` throws `InvalidCastException` outright — `Guid` is not
  `IConvertible`. This is the one Fisher read path that leans on provider coercion *by choice*, where
  `FisherEventsRowReader` converts explicitly. The row readers do so for round-trip symmetry, which
  raw SQL has nothing to protect: the caller names arbitrary columns, including ones Fisher never
  wrote.
- **A document is materialized by its storage's own selector, not by deserializing `data`.** Polecat's
  reader hand-deserializes at an offset and try/catches metadata columns. On Fisher that would be
  silently wrong for a hierarchy: the selectors resolve `doc_type` to the real sub-class, so
  deserializing to the declared type returns the base for every row, missing whatever the sub-class
  added. Swapping the selector for a `FromJson` makes
  `a_hierarchy_comes_back_as_its_real_subclasses` fail.

  The price is that `ISelector<T>.Resolve` reads from fixed positions starting at column 0, so **a
  document can only be the first result type of a query.** Anywhere else throws naming the
  restriction, rather than producing the cast error a misaligned read would.
  `IAdvancedSql.SelectFieldsFor<T>()` exists so a caller does not have to guess which columns to
  select.

**`StreamAsync` runs outside `StoreOptions.ResiliencePipeline`, deliberately.** A retried
`SQLITE_BUSY` re-executes the whole delegate, so a live reader yielded to a caller would resume
against a disposed connection — the property `GetRecentStreamsAsync` documents. Materialising first
would not be streaming, so the trade is that a busy database surfaces to the caller here alone.
`QueryAsync` stays inside the pipeline.

One trap that is SQLite's rather than Fisher's: **a bare `?` that Fisher does not treat as a
placeholder is still SQLite's own anonymous parameter marker**, so it fails with "must add values for
the following parameters" rather than passing through as text. Only a `?` inside a string literal is
safe. That is what the alternate-placeholder overloads are for.

### Soft delete

A document type marked with `[SoftDeleted]`, implementing `JasperFx.Metadata.ISoftDeleted`, or
registered with `Schema.For<T>().SoftDeleted()` gets two columns — `is_deleted` INTEGER 0/1 and a
nullable ISO-8601 `deleted_at` — and `Delete` becomes an `update … set is_deleted = 1` instead of a
`delete from`. `SoftDelete` owns both column names and the SQL that reads and writes them, because
the table definition, the storage and the LINQ layer would otherwise each spell them out.

Ported from Polecat, whose column names these are. What is worth knowing:

- **The load SQL carries the filter, not the caller.** `LoadAsync` / `LoadManyAsync` are reached from
  the session, the projection loader and the daemon alike, so a filter added by callers would present
  as a deleted document coming back to life on whichever path forgot it. The LINQ side gets the same
  filter from `DefaultWhereFragment()` — one source, so the query path cannot drift from the load
  path.
- **Storing a soft-deleted document undeletes it**, and that falls out of the upsert rather than
  being arranged. Weasel's `DocumentSoftDeletedBinder` / `DocumentSoftDeletedAtBinder` bind the *live*
  values on every write, and the `do update set` clause assigns every column from `excluded.*` — so
  the insert branch and the update branch agree without either being written twice.
- **A soft delete guards on `is_deleted = 0`; an undelete guards on `is_deleted = 1`.** Deleting an
  already-deleted document must not push `deleted_at` forward, or `DeletedSince` answers about the
  most recent call rather than about the deletion. Polecat has the guard on its by-id delete but not
  on `DeleteWhere`; Fisher has it on both, and
  `deleting_an_already_deleted_document_leaves_its_deletion_time_alone` pins it against a planted
  timestamp, because two deletes in the same millisecond would agree either way.
- **`DeletedSince` / `DeletedBefore` compare `deleted_at` as text**, with none of the `strftime`
  normalisation a document's own `DateTimeOffset` member needs (fisher#1). The column is
  `SqliteTimestamp`'s fixed-width UTC format, chosen so a string comparison *is* an instant
  comparison — the same property `fi_events.timestamp` relies on. A document member is whatever
  System.Text.Json wrote, which is why that one needs the wrapper and this one does not.
- **A soft-delete operator against a type that is not soft-deleted throws**, in the query layer and in
  `UndoDeleteWhere` alike. There is no column to answer from, so `IsDeleted()` would come back empty
  and `MaybeDeleted()` complete — both of which look like real answers.
- **`ISoftDeleted`'s own members are populated on read**, through the metadata member mapping below
  (fisher#11) — but only where a read can see a deleted row at all. Every ordinary load filters them
  out, so `Deleted` is observably true only through a query carrying `MaybeDeleted()` or
  `IsDeleted()`.

`DocumentWhereOperation` is the criteria-based half — `DeleteWhere`, `HardDeleteWhere` and
`UndoDeleteWhere` differ only in the SQL head they are handed and the guard they carry. The predicate
goes through the same `WhereClauseParser` as `Query<T>()`, and the caller's predicate is applied
*last*, after the tenant scope and the guard, because a compound predicate is parenthesized and so
cannot swallow them.

`TruncateDocumentStorageAsync` stays a real `delete from` — a rebuild's teardown clears rows rather
than flagging them, or the replay would write onto rows it cannot see.

### Duplicated fields

`Schema.For<T>().Duplicate(x => x.Name)` lifts a member into a column of its own and indexes it, so a
predicate against that member is a range scan rather than `json_extract` per row. **The column is a
SQLite `VIRTUAL` generated column, which is the whole divergence** — Marten and Polecat write theirs
on every upsert and refresh them in patch SQL. Three things follow, and they are why it was worth
diverging:

- **It cannot drift from `data`**, because nothing writes it. `Duplicate` can be added to a type that
  already has rows and every one of them is correct at once; a written column would need a backfill.
- **It costs index space, not row space.** `VIRTUAL` computes on read; only the index materialises.
- **The write path is untouched.** No extra binder, no shift in the positional `?` contract
  `SqliteDocumentStorageDescriptorBuilder` maintains. That is why this landed without touching
  document writes at all.

**The generated expression is the member's own `TypedLocator`, taken from `MemberFactory`** — not a
hand-written `json_extract`. That is what makes a duplicated member mean exactly what an unduplicated
one means, and it is what makes a duplicated timestamp work: the locator is the `strftime` wrapper
fisher#1 introduced, so the column holds the normalised fixed-width UTC form, sorts as text and is
indexable. `strftime` over a value (rather than `'now'`) is deterministic enough for a generated
column — verified against 3.51, along with the query plan actually reading `SEARCH … USING INDEX`.

`MemberFactory` swaps in a `DuplicatedMember` when a chain matches a registration, and that member
delegates **everything except `TypedLocator`** to the underlying one. So a duplicated string-stored
enum still refuses to be ordered, a duplicated bool still binds 1/0, and `RawLocator` stays on the
JSON — a null test asks whether the member is present, which is not quite whether the column is null
(an unparseable value yields a null column with the key present), and no index would serve it anyway.

Two things that are easy to get wrong:

- **`pragma_table_info` does not list generated columns; only `pragma_table_xinfo` does.** Weasel's
  delta detection uses the former, so every duplicated column reads as missing and the migration
  emits `ALTER TABLE … ADD COLUMN` for it *every time* — and since Fisher runs a migration on the
  first write of each document type per process, the second one fails with `duplicate column name`.
  `DocumentTable` overrides `ConfigureQueryCommand` to use `table_xinfo`, whose first six columns are
  `table_info`'s in the same order, so Weasel's positional reader needs no change. Reported as
  [weasel#426](https://github.com/JasperFx/weasel/issues/426); the override goes when that ships.
  `applying_the_configuration_again_is_a_no_op` fails with the real SQLite error without it.
- **The declared type is the column's comparison affinity**, so `SqliteTypeFor` is load-bearing
  rather than decorative — declare a numeric member TEXT and it starts sorting as text. `decimal`
  goes to REAL because `json_extract` hands back a REAL for any JSON number, and a column whose
  affinity disagrees with its own expression is the one shape that cannot be right.

The default column name is snake case — `LandedAt` becomes `landed_at`, `Water.Name` becomes
`water_name` — where Marten simply lowercases. Every other column on a Fisher document table is snake
case and a duplicated column sits among them. A name Fisher owns (`id`, `data`, `last_modified`, …)
is refused at configuration time, because SQLite would otherwise report it as a duplicate column at
CREATE TABLE, long after the line that caused it.

`Schema.For<T>()` returns a `DocumentMappingExpression<T>` rather than the mapping, because
`Duplicate(x => x.Name)` cannot infer its document type from a lambda and the receiver has to carry
it — the same reason Marten has `MartenRegistry.DocumentMappingExpression<T>`. The mapping is a
property on it; only what needs the type parameter lives on the expression.

### User-declared indexes

`Schema.For<T>().Index(x => x.Name)` / `.UniqueIndex(...)` (fisher#16) — an index over a member,
created as a **SQLite expression index** rather than as an index over a column. That is the whole
divergence, and it makes the feature cheaper on Fisher than on either sibling: Marten needs a computed
index and Polecat a `JSON_VALUE` index, both of which materialise something first, while SQLite has
indexed expressions (since 3.9, restricted to deterministic ones — `json_extract` qualifies). So the
member is indexed where it lives and the table's shape does not change.

This is what makes `Duplicate` and `Index` two different things rather than near-duplicates:

- `Duplicate` materialises a `VIRTUAL` generated column **and** indexes it — for when the member
  should also be a column something else can name.
- `Index` indexes the expression only. No column, no affinity to declare, nothing added to the table.

- **The indexed expression is the member's `TypedLocator`, from the same `MemberFactory` a query goes
  through.** SQLite's planner uses an expression index only when the query's expression matches the
  index's, so an index built from a hand-written `json_extract` is created without error, never used,
  and reports nothing. A timestamp is the case that proves it: its locator is fisher#1's `strftime`
  wrapper, so a bare `json_extract` index would not serve the range predicates a timestamp index
  exists for. Verified by swapping `TypedLocator` for `RawLocator` — the index is still created and
  `the_planner_uses_a_declared_index_for_a_timestamp_range` fails.
- **Indexing a member that is also duplicated indexes the generated column**, because a
  `DuplicatedMember`'s `TypedLocator` *is* the column name. Not special-cased; it falls out of reading
  the locator. The same swap makes this one regress to `json_extract` too, which
  `indexing_a_duplicated_member_indexes_the_column` catches.
- **The index name mirrors Weasel's own formula** — `idx_<table>_<members>` — repeated rather than
  called, because `DbObjectName.ToIndexName` is internal to Weasel.Sqlite. Deliberately
  indistinguishable from a duplicated field's index in `sqlite_master`: which mechanism created one is
  Fisher's business, not the reader's.
- **A `UNIQUE` index does not constrain documents missing the member.** `json_extract` yields SQL NULL
  for an absent key and SQLite treats NULLs in a unique index as distinct. Same as both siblings, and
  pinned because it is the kind of thing a reader assumes the opposite of.

Indexes go through Weasel's migration like every other schema object, so `AutoCreate.None` is honoured
for free and the table is not created lazily on first write.

### Document hierarchies

`Schema.For<TBase>().AddSubClass<TDerived>()` (fisher#17) — a base type and its sub-classes share one
table and one identity space. `Store(derived)` and `LoadAsync<TBase>(id)` share a table,
`Query<TBase>()` returns every sub-class as its own type, `Query<TDerived>()` narrows to one.

**The discriminator is a short alias in its own `doc_type` column, not `dotnet_type`.** Worth stating
because `dotnet_type` is already on every row and looks like the obvious candidate — it is not. It
holds an assembly-qualified name (long, not worth indexing, brittle across an assembly rename) and is
written by Weasel's `DocumentDotNetTypeBinder`, which takes no alias resolver. The binder built for
this job is `DocumentDocTypeBinder`, and it does. Both siblings keep the columns separate.

Weasel.Storage already had the rest: `DocumentStorageDescriptor.ResolveDocumentType`,
`docTypeReadIndex`, and a full set of `Hierarchical*ClosedShape*Selector` types. Fisher supplies the
mapping, the column, and the sub-class storage.

- **A sub-class must never acquire a mapping of its own**, and `DocumentSchema.BaseMappingFor` is what
  stops it — checked *before* the mapping cache, not after. Without it `Store(derived)` reaches
  `MappingFor(typeof(Derived))`, creates a mapping, and writes to `fi_doc_derived`: the sub-class is
  registered, carries an alias, and still lands in the wrong table. Verified by removing it — all
  three sub-classes get tables of their own.
- **`SubClassFisherStorage` wraps the base's storage rather than being built from the mapping.** The
  descriptor's selectors materialise a row as whatever its discriminator says, which only type-checks
  against the base; the wrapper downcasts through `CastingSelector`.
- **Writes delegate untouched.** `DocumentDocTypeBinder` resolves the alias from `document.GetType()`,
  so the base's write operations already stamp a derived instance with its own alias.
- **The two narrowing paths are different on purpose.** A query is narrowed in SQL; a load by id is
  narrowed in memory, by testing what came back. A load names one row and the id is unique across the
  hierarchy, so a discriminator predicate would only turn "that id is a different sub-class" into the
  same answer as "no such id".
- **The query filter is added once per statement, not inside `FilterDocuments`.** Two ways to get this
  wrong, and both were hit: composing it into `FilterDocuments` repeats it per caller predicate *and*
  omits it entirely from a query with none; hanging it off the soft-delete branch omits it for a type
  that is not soft-deleted and for the `DeletedOnly` / `MaybeDeleted` scopes of one that is. It is an
  `in` over the aliases at or below the queried type rather than an equality, because a sub-class may
  have sub-classes — Polecat emits a bare equality, correct only two levels deep.
- **A sub-class's default alias follows `DocumentMapping.Alias`'s convention, not snake case.** The
  base type's discriminator alias *is* its `Alias` — the one the table is named from — so a sub-class
  spelled differently would put two conventions in one column.
- **An unknown alias throws rather than falling back to the base.** A row written by a deployment that
  knew a sub-class this one does not is a real configuration gap; deserializing it as the base hands
  back an object quietly missing whatever the sub-class added. Deliberately the opposite of the event
  reads' policy, which skip an unresolvable `dotnet_type` — an event store must stay readable by a
  deployment that does not know every event, where a document load has one right answer.
- **An abstract or interface base is a hierarchy with nothing registered**, so its table carries the
  column from the first migration. Adding it later would leave the rows already written with no
  discriminator to read.

### Numeric revisions

`Schema.For<T>().UseNumericRevisions()`, or implementing `JasperFx.IRevisioned` (fisher#18) — an
INTEGER `revision` column as the alternative to `guid_version`, with `Store(doc, revision)`,
`UpdateRevision` and `TryUpdateRevision` on the session. The two concurrency styles are
**alternatives**: a type carries one column or the other, and `AssertConcurrencyIsCoherent` refuses
the pair rather than letting the descriptor pick one silently.

Weasel.Storage already had the whole numeric path — the descriptor's `revisionBinder` slot was
reserved and Fisher was passing null — so this is dialect SQL plus wiring, not new machinery.

- **The semantics are Marten's, deliberately, and they have a sharp edge.** `Store` passes the
  document's own `Version` as the expected revision (Marten's docs: "`Store()` is essentially
  `UpdateRevision(entity, entity.Version)`"), and the guard requires the supplied revision to be
  **strictly greater** than the stored one. So re-storing an instance that still carries the revision
  it was written at is a `ConcurrencyException`, not an increment — `UpdateRevision(doc, Version + 1)`
  or resetting `Version` to 0 (auto) is the way forward. Polecat diverged to an equality rule for its
  bespoke pipeline's parity; following it here would mean writing SQL the shared operations do not
  describe, and would silently disagree with Marten about what an explicit revision means.
  `storing_an_instance_that_carries_its_current_revision_is_rejected` pins it, message and all.
- **The trailing slot count is decided by which statement is being built, not by `guarded`.** This is
  the trap, and it bit during development. `NumericClosedShapeUpsertOperation` binds **four** trailing
  slots unconditionally (two for the SET case, two for the guard); `NumericClosedShapeOverwriteOperation`
  binds **two**; the update binds two. `guarded` is false under Numeric — it means "Optimistic" to its
  caller — so reading it produced a two-slot upsert for a four-slot binder, which surfaces as an
  `IndexOutOfRangeException` from inside Weasel that names nothing of Fisher's. Hence the explicit
  `isOverwrite` flag.
- **The revision binder occupies two client-side slots**, not one, in every insert-shaped statement:
  `case when ? = 0 then 1 else ? end`. Both get the same value.
- **`revision` is a separate `MetadataColumn` from `Version`**, because the two carry different CLR
  types — Guid and int — and `MetadataColumn` refuses a member that cannot hold its value. Sharing one
  slot would mean either dropping that check or making it lie.
- **A numeric revision is always read back, mapped member or not**, where a Guid version is dropped
  from the query-only projection. Asymmetric on purpose: the revision a caller will guard the *next*
  write with is the one the database just computed, so a read that withheld it would leave every
  explicit store guessing.
- **The column is INTEGER, and that is load-bearing.** A TEXT affinity would sort revision 10 below
  revision 9 and turn the "must be greater" guard into nonsense.

`0` means auto — increment whatever is stored — which is the sentinel the shared operations bind when
no revision was named, and why every guard starts with `? = 0 or`.

### Document metadata member mapping

`Storage/Metadata/` — which of a document's own members Fisher's metadata columns are projected onto
when a row is read. Every column is written either way; mapping only decides whether the value comes
back out, which is what makes `ISoftDeleted`'s `Deleted` / `DeletedAt` mean something rather than
being an opt-in marker (fisher#11).

Three ways to say it, each overriding the one before: the JasperFx metadata interfaces (`ISoftDeleted`,
`IVersioned`), the `Fisher.Attributes` metadata attributes, then `Schema.For<T>().Metadata(...)`. The
first two are conventions applied when the mapping is created; the DSL runs afterwards.

- **Four columns of the five, because `dotnet_type` has nowhere to go.** Weasel's
  `DocumentDotNetTypeBinder` takes no member where every other binder does, so `DocumentMetadata`
  omits it rather than offering a mapping that would silently do nothing. That is an upstream gap, not
  a Fisher decision.
- **Adding a mapping widens the SELECT**, because a binder is added to `readBinders` only when its
  column is mapped — an unmapped binder returns before touching the reader, so carrying it would cost
  a column per row to accomplish nothing. The read ordinals are `FirstMetadataColumn + index` into
  that array and `FisherDocumentStorage`'s select list is built from the same array in the same order,
  so **the two stay aligned only while both are derived from it**. Nothing should ever append to one
  without the other; `AddIfMapped` exists so there is one place that does both.
- **`IVersioned` turns optimistic concurrency on**, as it does on both siblings. Not a liberty: with
  it off the `guid_version` column is neither written nor read, so mapping a member onto it would mean
  nothing. The converse does not hold — `UseOptimisticConcurrency()` alone maps nothing, because there
  is no member named.
- **A mapped version stays in the query-only projection.** It is normally dropped there, since a
  query-only load has no version tracker to feed; once a member reads it, dropping it would make
  `Query<T>()` and `LoadAsync` disagree about what the document holds.
- **The interfaces are resolved through the interface map, not by name.** An explicitly implemented
  `ISoftDeleted.Deleted` is a private member called `Fisher.…ISoftDeleted.Deleted`, which neither
  `GetProperty("Deleted")` nor a scan of public members finds — and a document is free to have a
  public `Deleted` of its own meaning something else.
- **A bad mapping is refused at configuration time**, with the column named. `LambdaBuilder.Setter`
  would otherwise throw when the document's storage is first built — on first use, a long way from the
  line that caused it, and in a message about expression trees. Same discipline as
  `DuplicatedField.AssertColumnNameIsAvailable`.

The read path leans on Microsoft.Data.Sqlite's coercion rather than converting explicitly, which is
the one place Fisher does — `DocumentVersionBinder` reads `GetFieldValue<Guid>` over lowercase
canonical TEXT, `DocumentSoftDeletedBinder` reads `GetFieldValue<bool>` over INTEGER 0/1, and the two
timestamp binders read `GetFieldValue<DateTimeOffset>` over `SqliteTimestamp`'s fixed-width UTC text.
Those are Weasel's binders and Fisher does not own them. `metadata_column_coercions` pins all four
against the exact shapes Fisher stores, without any of the mapping machinery in the way, so a provider
upgrade that changes one fails there and names the column instead of presenting as a member that
quietly stopped being populated. All four hold as of Microsoft.Data.Sqlite 10.0.9, including a Guid in
either casing.

### Strong-typed identities

`Storage/StrongTypedId.cs` and `Storage/ClosedShape/StrongTypedIdentification.cs` (fisher#14) — a
wrapper struct or class standing in for one of the four canonical id types, as an aggregate's identity
and as a document's. The shape is JasperFx's, described by `ValueTypeInfo.ForType`: one public gettable
property, plus a matching constructor or a static builder.

**This needed no new seam.** `IIdentification<TDoc, TId>` already reserved `ToRawSqlValue`,
`RawSqlType` and `ReadIdFromReader` for exactly this, so a wrapper presents its inner value at the
ADO.NET boundary while the document keeps the wrapper. Nothing downstream knows the id was wrapped —
the table shape, the write SQL and the positional `?` contract are untouched.

- **Fisher discovers wrappers rather than requiring registration**, which is Polecat's model rather
  than Marten's, and is why the compliance seam's `RegisterValueType<T>` is a no-op here.
- **`DocumentIdentity.FindIdMember`'s predicate overload is the whole entry point.** Its default
  accepts only the canonical four; the overload exists so a store can widen it, and
  `StrongTypedId.IsSupportedIdType` is Fisher's. Before this, every strong-typed aggregate failed with
  "has no identity member", which was true only in the sense that the filter had rejected it.
- **`ValueTypeInfo.ForType` throws for anything that is not a wrapper**, and it is asked about every
  candidate identity member of every type. The answer is cached including the negative, and cheap
  exclusions run first, or resolving an ordinary aggregate's identity would raise and swallow an
  exception every call.
- **Everything about the column derives from `DocumentMapping.StoredIdType`, not `IdType`.** The
  column holds the inner value; the wrapper exists only in .NET. Deriving from the wrapper gives an
  int-backed id a TEXT column and a Guid-backed one the wrong `StorageColumnType` — **and no
  compliance suite catches either**, because the suite only uses Guid- and string-backed wrappers,
  where TEXT happens to be right. `strong_typed_identities` pins it.
- **A Guid-backed wrapper goes through the same lowercase-canonical conversion as a raw one.** That
  conversion lives in the identity strategy rather than the dialect (`SqliteGuidIdentification`), so
  the wrapper is exactly where it could have been quietly lost — and the compliance suite would not
  have seen it, because it never reads the row.
- **Generation mirrors the raw strategies**: version-7 Guid, or the document type's Hi-Lo sequence. A
  string-backed wrapper generates nothing, because a raw string key is externally assigned too.

`LoadAsync<T, TId>(id)` is the load-by-wrapper overload. Both type parameters are explicit, which is
what keeps it unambiguous against the four single-parameter overloads.

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

### DI registration

`AddFisher(...)` (fisher#20) — `FisherServiceCollectionExtensions`, plus `ISessionFactory` and two
hosted services. The store is a singleton, sessions are scoped, and the returned
`FisherConfigurationExpression` carries the two opt-ins: `ApplyAllDatabaseChangesOnStartup()` and
`AddAsyncDaemon(mode)`. Deliberately smaller than either sibling's — no multi-store
`AddFisherStore<T>`, no `IConfigureFisher` chain, no initial-data seeding.

- **`DaemonMode.HotCold` is refused, and it is a real limitation rather than an omission.** Hot-cold
  failover means several nodes competing for one leadership lease through the database, and a Fisher
  store is a file that SQLite does not make safe to share across nodes. Accepting the mode and running
  Solo would give an application the opposite of the guarantee it asked for — every node projecting at
  once. `Solo` starts the daemon; `Disabled` and `ExternallyManaged` register nothing.
- **The WAL warning moved to where somebody sees it.** `BuildProjectionDaemonAsync` has warned about a
  non-WAL journal since the daemon landed, but only a consumer building a daemon by hand ever saw it.
  The hosted service puts it in the application log at startup. Still a warning rather than a refusal:
  a non-WAL store projects correctly, just serialised against its writers.
- **`AutoCreate.None` wins over `ApplyAllDatabaseChangesOnStartup()`.** The hosted service starts and
  does nothing, rather than the registration being the thing that quietly overrides schema policy.

**Everything Fisher hands a container now implements `IDisposable` as well as `IAsyncDisposable`** —
`DocumentStore`, `FisherDatabase`, `FisherSession`, and `IQuerySession` with them. That is not a
politeness: a `ServiceProvider` disposed synchronously **refuses outright** to dispose a service
offering only `IAsyncDisposable`, with "type only implements IAsyncDisposable". Since `AddFisher`
registers sessions scoped, the async-only shape made a scoped session unusable rather than merely less
efficient — which is why this surfaced the moment there was a container at all. `SqliteConnection` and
`DbDataSource` both supply the sync form, so nothing blocks. Marten's `IDocumentStore` and
`IQuerySession` declare both for the same reason.

### `IDocumentStore`

`DocumentStore`'s own public API, extracted as an interface (fisher#45) so application code depends on
the abstraction and `AddFisher` can register both. It declares `IDisposable` **and**
`IAsyncDisposable` for exactly the reason above — an interface declaring only the async form would
reintroduce fisher#20's bug one level up, where it is harder to see.

- **The tooling surfaces are deliberately not on it.** `IEventStore`, `IEventStore<,>` and
  `ISubscriptionRunner<>` are implemented **explicitly**, so they are private members and a consumer
  casts to reach them. That is the whole point of implementing them explicitly, and re-exposing one
  through `IDocumentStore` would undo it. `the_tooling_interfaces_are_not_re_exposed` pins it, because
  "add it to `IDocumentStore` too" is the natural-looking fix for a cast somebody finds awkward.
- **`DocumentStore.For(...)` stays static on the concrete class and keeps returning `DocumentStore`**,
  mirroring Marten. `AddFisher` registers the concrete type *and* the interface against one singleton;
  the concrete registration stays so existing code keeps resolving.
- **The surface is pinned by reflection in both directions.**
  `every_public_instance_member_of_the_store_is_on_the_interface` fails, naming the member, when a
  public member is added to one and not the other — verified by adding one. Its filter is
  `BindingFlags.Public`, which is correct rather than merely convenient: an explicit implementation is
  a private member, so the tooling surfaces are excluded by the same rule that makes them explicit.
  `the_store_implements_every_interface_member_implicitly` checks the other direction through the
  interface map, so a member satisfied explicitly — compiling fine and then unreachable from the
  concrete type — is caught too.

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
- **`DeleteAllEventDataAsync` deletes in a fixed order**, tag tables first — see "Dead letters".
  `CompletelyRemoveAllAsync` needs no ordering: SQLite does not enforce a foreign key against a
  dropped table.

`CompletelyRemoveAllAsync` calls `FisherDatabase.ForgetEnsuredTables()` afterwards. Without it the
"this document table already exists" cache would still claim tables that were just dropped, and the
next `Store` would skip its migration and write to nothing.

- **A message bus, and deliberately so.** The side-effect seam exists and the default outbox drops
  every message. That is the end state, not a gap — fisher#8 was closed wontfix. Delivery is a bus
  integration's job here as it is on both siblings.
- Tenancy beyond the conjoined style. One database sliced by a tenant id column works and is pinned by
  `ConjoinedEventTenancyCompliance`; database-per-tenant is not there, which is why every
  `IEventDatabase` parameter in `DocumentStore.Daemon.cs` is ignored.
- Natural keys and bulk insert.

### The `IEventStoreOperations` surface

`EventOperations` implements JasperFx's `IEventStoreOperations` in full — the interface the
cross-store compliance suites route everything through, so declaring it is what makes
`EventStoreComplianceFixture.EventsFor(session)` possible at all.

Everything reachable without document storage is real: `FetchForWriting` rebuilds the aggregate by
live aggregation (there is no snapshot to read instead), `WriteToAggregate` is fetch + callback +
`SaveChangesAsync`, and `ProjectLatest` folds the session's pending events on top of the committed
state.

**Nothing on `IEventStoreOperations` throws any more.** What did lived in
`EventOperations.Unsupported.cs`, one file on purpose so that file shrinking was the progress
measure; it reached zero members and was deleted. Reintroduce it, rather than scattering throws, if a
future JasperFx release widens the interface past what Fisher implements.
`FetchForWriting<T, TId>` and `FetchLatest<T, TId>` are partial:
they accept an id that is already the stream identity type and throw for anything else, because in the
siblings that overload is the natural-key and strong-typed-id entry point.

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
**Nothing on `IEventStore` throws any more** — `OpenReadOnlyEventStore` was the last one and fisher#15
closed it. The standing discipline, for the next member that arrives ahead of the feature, is that one
Fisher cannot honour throws naming its milestone rather than returning an empty result a monitoring
tool would render as "no data". `CompactStreamAsync` is live; see "Stream compacting". The generic half of the interface, and `BuildProjectionDaemonAsync`
with it, lives in `DocumentStore.Daemon.cs` — see "The async daemon" below.

`OpenReadOnlyEventStore` returns `FisherReadOnlyEventStore`, which **owns session lifetime rather than
capturing a session** — the one divergence from Polecat here, and it is dialect-forced. Polecat returns
`QuerySession().Events` directly, and since `IReadOnlyEventStore` is not `IDisposable` nothing ever
disposes that session. A `FisherSession` caches its `SqliteConnection` for its whole lifetime and
releases it only in `DisposeAsync`, so the same shape would leak a pooled connection against a single
database file on every call — to a method whose caller is a polling monitoring tool. Opening a session
per read costs a pool checkout, which for an embedded database is a rounding error next to that.

`EventOperations.QueryEventsAsync(EventQuery)` is the paging read behind it. Two things in it are
load-bearing, and both were verified by removing them:

- **A `StreamId` filter is parsed and re-rendered under Guid identity**, for the same reason
  `GetStreamMetadataAsync` does it — `fi_events.stream_id` holds the lowercase canonical form and
  SQLite's default collation is case-sensitive. Binding the caller's string directly makes an uppercase
  Guid match nothing, so the Explorer renders an existing stream as empty. Without the parse,
  `a_stream_id_filter_matches_regardless_of_guid_casing` returns 0 where it expects 3.
- **The three metadata filters are gated on the options that create their columns.** `correlation_id`,
  `causation_id` and `user_name` do not exist on `fi_events` unless the matching `Enable*` option is on,
  so an ungated filter is not an empty result but `SQLite Error 1: no such column`. Ignoring the filter
  is what `EventQuery` asks for and what Polecat does.

The count is a second statement rather than `count(*) over ()`, because a window function returns no
row at all for a page past the end — and "page 9 of a 3-page result" is exactly when a tool most needs
the real total. `a_page_past_the_end_still_reports_the_total` pins it.

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
`SupportsReturning` machinery has no analogue; `TypedLocator` and `RawLocator` are the same string for
every member except a timestamp, which is the one case that needs wrapping.

Five SQLite decisions that are easy to get wrong and fail silently:

- **String predicates use `instr`/`substr`, not `LIKE`.** SQLite's `LIKE` is case-*insensitive* for
  ASCII while `=` is case-*sensitive*, so a LIKE-based `Contains("frodo")` matches `"Frodo"` on data
  where `== "frodo"` does not — a query surface contradicting itself, and not what .NET's ordinal
  `string.Contains` means. `_` and `%` are also `LIKE` wildcards needing an `ESCAPE` clause (Polecat's
  `[_]` bracket form is T-SQL-only) — the same trap as the document cleaner's table matching.
- **Paging is `limit m offset n`.** `TOP(n)` and `OFFSET … FETCH NEXT` collapse to one form; T-SQL's
  `ORDER BY (SELECT NULL)` filler is not emitted because SQLite does not need it and it would impose a
  sort nobody asked for. An offset with no limit must say `limit -1` first — a bare `offset` is a
  parse error.
- **A timestamp is compared through SQLite's date parser, not against the raw JSON.**
  `TimestampMember.TypedLocator` is `strftime('%Y-%m-%dT%H:%M:%f', json_extract(...))`, which folds the
  trailing offset into UTC and renders fixed-width to the millisecond. Without it the comparison is
  against the text System.Text.Json wrote, and that is not order-preserving twice over: trailing
  fractional zeros are trimmed, and the original offset is kept, so `12:34:56-05:00` sorts before
  `12:34:56.789+00:00` while being five hours later. **Equality goes through the same normalisation as
  ordering** — two spellings of one instant must not be equal for `>=` and unequal for `==` — which
  costs sub-millisecond discrimination on `==`, as it does on the siblings (`timestamptz` is microsecond
  precision). `RawLocator` stays bare, because a null test asks whether the member is present, not
  whether it parses. **`DateOnly` and `TimeOnly` need none of this** and stay on `DateMember`: a
  `DateOnly` is fixed-width with no offset and no fraction, and a `TimeOnly`'s optional fraction is a
  strict suffix, so trimming shortens the string without changing which of two values compares smaller.
  This is documents only — the `fi_events`/`fi_streams` timestamp columns use `SqliteTimestamp`'s
  fixed-width UTC format precisely so they *do* sort as text. Making the result *indexable* is a
  separate concern (fisher#2), and `strftime` over `json_extract` being computed per row is what makes
  it worth having.
- **`AllowsRangeComparison` is still false for one member: a string-stored enum.** Under
  `EnumStorage.AsString` the stored value is the member's *name*, so ordering by it sorts alphabetically
  rather than by the enum's declared order — `HighDistinction` before `Pass`, whatever the values say.
  Both the where parser and `OrderBy` refuse rather than answer wrongly, naming `EnumStorage` in the
  message. Fisher's default is `AsInteger`, where `json_extract` yields a number that orders correctly
  with no help. That property survives fisher#1 precisely because this case exists; it is the seam for
  "correct for equality, meaningless when ordered", not a date-specific flag.
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
the old `$(EnableComplianceTests)` gate is gone. **All 28 suites, 230 tests, are live**, as of 2.45.0
— which is the whole library, since 2.45.0 emptied the upstream event sourcing compliance backlog.
The four that arrived in 2.40.0/2.41.0 — `StringStreamIdentityCompliance`,
`SnapshotLifecycleCompliance`, `MultiStreamProjectionCompliance`, `FlatTableProjectionCompliance` —
went in on the same bump.

`StrongTypedIdentityCompliance` arrived in 2.42.0 and went green in fisher#14. It has no capability
flag to decline with, so a store either passes it or leaves it unsubclassed.
`FisherComplianceFixture.FisherComplianceRegistrar.RegisterValueType<TValue>` is a **no-op**, which is
the correct implementation for a store that discovers value types by itself — see "Strong-typed
identities".

`EventDataMaskingCompliance` and `StreamCompactingCompliance` arrived in 2.43.0 and **both went green
on the bump alone** — 21 tests, no production change. That is the useful thing about them: fisher#9
and fisher#10 were built from Polecat's shape months before a shared suite existed to check the shape
was right, and the suites are what turns "ported faithfully" from a claim into a fact. Compacting
needed nothing at all — `CompactStreamAsync<T>` was lifted onto `IEventStoreOperations` in 2.41.0, so
the suite reaches it through the shared surface. Masking cost **three seam members and no more**,
because `IEventDataMasking` is shared but the entry point that hands one out is not: every store spells
it on its own `Advanced` surface, and those share no interface.

- `FisherComplianceFixture.ApplyEventDataMaskingAsync` delegates to
  `Store.Advanced.ApplyEventDataMaskingAsync` — the signatures already matched one for one.
- The registrar's two `AddMaskingRule<TEvent>` overloads delegate to
  `EventGraph.AddMaskingRuleForProtectedInformation<T>`. The `Action` / `Func` split the seam demands
  is the same split Fisher already had, and for the same reason — see "Event data masking" for why
  only the `Action` form reaches contravariantly.

`RebuildAndCatchUpCompliance` and `DeadLetterCompliance` arrived in 2.44.0 and **both went green on
the bump alone** — 17 tests, no production change and no seam addition, the cheapest wave yet. Both
were filed upstream as needing a seam and turned out not to: the whole rebuild surface (seven
`RebuildProjectionAsync` overloads, both `CatchUpAsync` forms, `PrepareForRebuildsAsync`) is already
declared on `IProjectionDaemon`, and the dead letter path is `IEventStore<,>.ContinuousErrors` plus
`IEventStore.AllDatabases()` → `IEventDatabase.QueryDeadLetterEventsAsync`, all of which Fisher
already implements.

`ConjoinedEventTenancyCompliance` and `SubscriptionCompliance` arrived in 2.45.0 and **both went green
on the bump** — 14 tests, no production change, at the cost of two seam members and one partial.

- **The tenancy suite is the first to check a Fisher feature nothing cross-store had covered.** Every
  earlier suite either confirmed a port or demanded a new feature; conjoined event tenancy was built
  with the original schema work and had only Fisher's own `event_store_schema_creation` holding it —
  a schema test, which cannot see the property that matters. That property is *isolation*, and its
  failure mode is silent and asymmetric: a store that leaks across tenants still answers correctly for
  the tenant owning the data and misbehaves only for the other one. The suite checks both directions
  on every test, over a stream id deliberately reused across two tenants.
- **`ConjoinedEventTenancy` must be applied before the schema is created.** `StreamsTable` and
  `EventsTable` read `TenancyStyle` when they build their columns and their primary key, so the
  fixture sets it inside the `DocumentStore.For` lambda, ahead of
  `ApplyAllConfiguredChangesToDatabaseAsync`. It is a schema decision, not a runtime one.
- **Verified load-bearing by removing it**, which is the discipline a suite that passes on the bump
  deserves — that is exactly where a seam member quietly doing nothing would hide. Without the flag
  the suite fails with `ExistingStreamIdCollisionException` from `AppendPlanner`, the "collide on
  append" outcome the suite's own notes predict for a store keying on id alone.
- **The subscription's name is pinned, not defaulted.** `SubscriptionWrapper` derives
  `ComplianceSubscription` from the type name anyway, so the registrar's explicit
  `options.Name = ComplianceSubscription.SubscriptionName` is redundant today — deliberately. Daemon
  progression is keyed on that string, and the seam should not rest on a naming convention Fisher
  could reasonably change.
- **`ComplianceSubscription` is the second shared type an alias cannot reach**, after
  `ComplianceFlatTableProjection`, so it needs a per-consumer partial
  (`Compliance/ComplianceSubscription.Fisher.cs`). Fisher's is the closest of the three stores to
  being writable once: `ISubscription.ProcessEventsAsync` returns JasperFx's lifted
  `IDaemonChangeListener` rather than a product-local `IChangeListener`, because fisher#21 took the
  shared type instead of copying Polecat's older spelling.

- **The dead letter suite reaches the error policy by casting the fixture's non-generic
  `IEventStore` to the closed generic.** Safe because the suite is generic over the same session pair
  the store closes over, and worth knowing as a general trick: it reaches the whole generic store
  surface without adding a seam member. Fisher's `ContinuousErrors` returns
  `Options.Projections.Errors` — the live options object, not a per-read copy, which
  `skip_apply_errors_is_readable_back_off_the_shared_options` pins directly and
  `the_shard_survives_a_poison_event_and_keeps_going` pins indirectly by requiring the daemon to have
  actually consulted it.
- **The rebuild suite's teardown test is the one that matters**, and it is the reason this wave was
  worth having rather than a formality. A rebuild that replays onto surviving rows looks correct for
  every stream whose events still exist and is wrong only for rows the replay can no longer produce —
  so it passes any assertion about a live aggregate. The suite plants a document with no backing
  events and requires the rebuild to remove it. That is the same divergence Fisher's own
  `TeardownExistingProjectionStateAsync` had to learn for flat tables (see "Flat-table projections",
  `IPublishesTables`), now checked for document projections too.
- **Upstream disclosed an unreproduced intermittent** in `a_rebuild_reproduces_the_projected_state` —
  two failures on Polecat early in that suite's development, none since. Not seen on Fisher: 15
  consecutive runs of the suite clean, plus the full 216 twice. Recorded so a first sighting here is
  recognised rather than investigated from scratch.

The mechanics, because they are not what the package's name suggests:

- **Every suite compiles; only the subclassed ones run.** Enrolling is one empty class in
  `fisher_event_store_compliance.cs`. Not enrolling costs nothing at runtime — but the shared source
  still has to compile, which is why every global alias in `ComplianceAliases.cs` must resolve
  even for suites Fisher cannot pass.
- Every suite now compiles. The two `EventProjection*` suites were once `<Compile Remove>`d because
  they call `IDocumentSession.Store` and `IQuerySession.LoadAsync`; document storage made them
  compile and they are enrolled.
- **Nothing in `FisherComplianceFixture` throws any more.** The standing discipline is that a member
  Fisher cannot honour throws a `NotSupportedException` naming its milestone, so enrolling a suite
  prematurely fails loudly rather than passing on a stub — but as of fisher#14 there is no such
  member left. Keep the discipline for the next seam member that arrives ahead of the feature.
- `CleanEventDataAsync` delegates to `Advanced.Clean.DeleteAllEventDataAsync`. It is called before
  every test, so it must not throw the way an unsupported member would — hence the null guard rather
  than the `Store` accessor.
- **`QueryTableAsync` is the seam's only raw data access**, added in 2.41.0 for the flat-table suite —
  a table name in, every row out, deliberately predicate-free. Fisher's implementation does the
  schema fold and **converts a lowercase-canonical Guid string back to a `Guid`**. That conversion is
  not a fudge: SQL Server has `uniqueidentifier` and PostgreSQL has `uuid`, so on both siblings the
  provider hands the suite a `Guid` and `Equals(row["id"], streamId)` matches. SQLite has no such
  type, so something has to convert, and doing it there is the same explicit `Guid.Parse` the row
  readers do. Matching on the canonical rendering rather than `Guid.TryParse` alone is what keeps it
  from claiming an ordinary string column holding Guid-shaped text in some other casing.
- A suite may gate itself on a fixture capability flag rather than on enrollment —
  `SupportsFlatTableProjections` is the 2.41.0 example, and exists so a store can enroll the suite
  before the feature and use it as the specification. Fisher leaves it true; the suite's projection
  still needs a real `FlatTableProjection` base to compile against either way, and Fisher's half of
  that shim is `Compliance/ComplianceFlatTableProjection.Fisher.cs`.

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
