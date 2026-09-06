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

# The documentation site
npm install
npm run docs                               # mdsnippets + dev server on :5173
npm run docs-build                         # a dead internal link fails the build
```

No database server is required — tests create throwaway SQLite files via
`TemporaryDatabase.Create()`.

Documentation code samples are compiled — see "The documentation site" below before editing a page's
code block.

## Architecture

### The three-layer split

Fisher owns very little storage code. The division is worth internalizing before adding anything:

- **JasperFx / JasperFx.Events** — event/projection/daemon abstractions. Implement its interfaces;
  do not reinvent them. `EventGraph` derives from `JasperFx.Events.EventRegistry`.
- **Weasel.Sqlite** — schema management, table definitions, migrations, the PRAGMA-applying
  `SqliteDataSource`, and `CommandBuilder`. All DDL goes through it; no hand-written CREATE TABLE.
  **All raw data access goes through it too** — see below.
- **Weasel.Storage** — the dialect-neutral closed-shape document + event storage runtime extracted
  from Marten. Fisher supplies two dialects (`SqliteStorageDialect<TId>`,
  `SqliteEventStoreDialect`) and the runtime does the rest. **Prefer extending the dialects over
  writing bespoke storage code.**

### Raw data access goes through Weasel.Sqlite first

**Reach for Weasel.Sqlite before writing ADO.NET by hand, and deviate only where it genuinely cannot
express what is needed.** Connections come from `SqliteDataSource` (via
`FisherDatabase.OpenConnectionAsync`), never from `new SqliteConnection(...)` in production code — the
data source is what applies the store's PRAGMA settings and what holds an in-memory database alive.
Statements are built with `Weasel.Sqlite.CommandBuilder` rather than by concatenating SQL and adding
parameters by hand, so parameter binding and placeholder handling stay in one implementation. Schema
work goes through Weasel's table definitions and migrations, which is what makes
`AutoCreate.None` honoured everywhere for free rather than at each call site's discretion.

**When Weasel.Sqlite cannot do it, the workaround is local, commented, filed upstream, and removed
when the fix ships.** That cycle has now run twice and closed twice, and **both removals are the
point of the rule** rather than a tidy-up:

- `FisherCommandBuilder` existed because `CommandBuilder` did not declare
  `Weasel.Core.ICommandBuilder`. Gone since weasel#424 shipped in 9.23.2 — do not reintroduce it.
- `DocumentTable.ConfigureQueryCommand` overrode Weasel's `pragma_table_info` query because it omits
  generated columns. Gone since [weasel#426](https://github.com/JasperFx/weasel/issues/426) shipped
  in **9.24.0** — do not reintroduce that either.

**The second one shows what the rule is actually protecting against**, because it was not removed on
the bump that fixed it and the cost arrived one release later. Weasel 9.25.0 added a fifth statement
(triggers) to the metadata query; Fisher's override still emitted four, and the override and the
reader that consumes it are one contract. The result was not a stale-but-harmless copy — it was
`ArgumentOutOfRangeException` out of `readForeignKeysAsync`, a result-set misalignment with nothing in
the message about Fisher or about generated columns. A deviation with no issue behind it is a
deviation nobody will ever remove; a deviation whose issue has *closed* is worse, because it now
diverges silently from the thing it was copied from.

**Reusable data-access helpers belong in Weasel.Sqlite eventually, so write them where they can move.**
The test is whether the helper would be equally correct for any SQLite consumer or whether it encodes
one of *Fisher's* storage decisions. `SqliteTimestamp`, `SqliteParameterValue`'s conversions, the row
readers and `FisherTableNaming` are all the latter — they are about the shape of Fisher's data, not
about SQLite, and they stay. Anything that is really "how you do X against SQLite" should be built as
a self-contained piece with no Fisher types in its signature, so pushing it upstream later is a file
move rather than a rewrite.

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

### Serialization is Weasel's, and so is the adapter

`Fisher.Serialization.Serializer` is a **subclass of `Weasel.Core.SystemTextJsonSerializer` with an
empty body** (weasel#555). It and Polecat's were byte-identical — the same options plumbing, the same
`ToJson`/`FromJson` matrix, and, the part worth naming, the same `[UnconditionalSuppressMessage]`
justifications on every reflection-based member. All of that is upstream now, suppressions included,
so **the trim/AOT contract Fisher documents is the one the shared base declares** rather than a second
copy free to drift from it.

What stays in Fisher is the identity: the type name `StoreOptions.Serializer` defaults to and that
applications subclass, in Fisher's namespace, declaring Fisher's two interfaces. Both declarations are
satisfied entirely by inheritance, and neither is redundant —

- `Fisher.Serialization.ISerializer` extends `Weasel.Core.ISerializer` with the two string-based
  overloads. The shared base carries both, with the propagating
  `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]` those two, and only those two, are meant to
  have.
- `Weasel.Storage.IStorageSerializer` lives in Weasel.Storage, which references Weasel.Core, so the
  base *cannot* declare it — but it carries every member with BCL-typed signatures, which is what lets
  a subclass declare the interface and satisfy it by inheriting. That is why
  `StorageSerializerAdapter.For` hands the default serializer straight back rather than wrapping it.

**`Fisher.Serialization.StorageSerializerAdapter` is deleted**; `Weasel.Storage.StorageSerializerAdapter`
(public since 9.31.0) is the same class and is what the three call sites use. Note that
`EventGraph.BuildClosedShapeEventStorage` names it fully qualified: that file imports
`Fisher.Serialization`, so an unqualified reference would be ambiguous the moment anyone reintroduces
a local one — which is exactly the signal wanted.

`shared_serializer` pins the shape rather than the behaviour, on purpose: behaviour is covered
everywhere a document or event round-trips, and what a subclass can silently lose is an interface
declaration that stops being satisfied by inheritance and quietly falls through to the adapter.

### Closed upstream gap — weasel#423

Historical note, in case the shape of the session's execute loop looks over-engineered. Fisher used
to carry a `FisherCommandBuilder` shim because Weasel.Sqlite's `CommandBuilder` did not declare
`Weasel.Core.ICommandBuilder`, the surface every shared closed-shape storage operation configures
itself against — without it, no Weasel.Storage operation could be configured against a SQLite command
builder at all.

Fixed upstream by [weasel#424](https://github.com/JasperFx/weasel/pull/424) and shipped in
**Weasel.Sqlite 9.23.2**. The shim is gone; `FisherSession` uses `Weasel.Sqlite.CommandBuilder`
directly. Do not reintroduce it.

### One execution and one reader per operation, and what that immunizes

`FisherSession.ExecuteBatchAsync` executes each queued operation **on its own, against its own
reader**, sharing only the transaction. The reasons are in its remarks — operations bind by position,
and Fisher's append ends in a SELECT — and the consequence is worth stating separately, because it is
what a whole class of bug needs (fisher#66).

**What is shared is the prepared statement, and only across a consecutive run (fisher#171).**
`ReusedCommand` holds the command last prepared on the connection and hands it back when the next
operation compiles to character-identical SQL, moving that operation's parameters onto it;
Microsoft.Data.Sqlite keeps a command's prepared statements alive while its `CommandText` does not
change, so a hundred-document save is one `sqlite3_prepare_v2` rather than a hundred — inside the
exclusive `BEGIN IMMEDIATE` transaction, so it is write-lock time rather than merely CPU. Runs are
coalesced and nothing is ever reordered: a mixed batch falls back to a command per operation, having
paid one string comparison per step. **Grouping the batch by statement would coalesce more and is
refused**, because the queue's order is load-bearing — an event's tag rows are deleted before the
event itself (fisher#6), and the foreign key enforces it.

**Concatenating the batch into one multi-statement command is the obvious version of this and is
measurably the wrong fix here.** Measured against Microsoft.Data.Sqlite 10.0.9 before anything was
built: 1000 single-row upserts in one transaction take 4–6 ms as separate commands and **82–192 ms
concatenated**. The cost is parameter binding, not statements — the same 1000 statements with their
values inlined and no parameters run in 7.5 ms, and chunking traces the quadratic in the open (10 per
command 10 ms, 50 per command 22 ms, 250 per command 59 ms), because `SqliteParameterCollection` is
rebound per prepared statement against the whole collection. **Every chunk size measured is worse than
a command per operation**, so there is no sweet spot to tune to; the ceilings are not the constraint
either (50,000 parameters and 50,000 statements in one command both execute). Do not reach for it
again without re-measuring.

Marten concatenates a unit of work into one command and walks the result sets with `NextResultAsync`,
skipping the advance for an operation marked `Weasel.Storage.NoDataReturnedCall`. marten#5210 was an
operation carrying that marker whose SQL *did* return a row, so the reader stayed one result set
behind and **every operation after it in the batch postprocessed against somebody else's rows** —
silently, with the symptom surfacing nowhere near the cause. Fisher has no `NextResult` walk to fall
behind, so a mislabelled operation costs nothing;
`no_data_returned_operations.a_mislabelled_operation_cannot_misalign_the_batch` plants exactly that
shape rather than leaving the immunity to be inferred. **fisher#171 did not weaken this** — sharing a
prepared statement is not sharing a reader, and that test still passes unchanged.

**Microsoft.Data.Sqlite makes the concatenated shape sharper than Marten's, not softer**, which is the
second and more important reason not to reach for it. It surfaces a result set only for statements
that return *columns*: a four-statement command whose second and fourth statements select yields
exactly **two** result sets, so a `NextResult` walk's alignment would rest entirely on
`NoDataReturnedCall` being truthful — the thing marten#5210 proves cannot be assumed. Worth knowing
precisely, because the near-miss is the other way round: a guarded upsert matching no row **does**
surface its own empty result set (the provider skips zero-*column* statements, not zero-*row* ones),
so the optimistic-concurrency read is not what would break first. The marker is.

The marker is still audited — it is a claim a reader would trust, and the execution strategy could
change — by *executing* each marked operation's compiled SQL and asserting it returns no columns. The
audit is a claim about the statement, not its spelling: what would go wrong is a `returning` clause
added to a statement whose operation still declares no-data.

## Current state

Working, with tests:

- `fi_streams` / `fi_events` / `fi_event_progression` schema via Weasel.Sqlite
- `SqliteStorageDialect<TId>` and `SqliteEventStoreDialect` (Quick append + auxiliary operations)
- `DocumentStore`, `FisherSession` unit of work, `EventOperations`
- `StartStream` / `Append`, version assignment, optimistic concurrency, sequence read-back
- Reads: `FetchStreamAsync` (version / from-version / timestamp bounded), `FetchStreamStateAsync`,
  `LoadAsync`, both stream identity styles
- `ArchiveStream` / `UnArchiveStream` / `TombstoneStream` — and **an archived stream refuses further
  appends** (`Exceptions.ArchivedStreamException`, fisher#184), because archiving is not a soft delete
  you can keep writing through. Checked in `AppendPlanner.PlanStream` before the version guard and
  deliberately *not* for a `StartStream`, where an archived id is still an id in use and
  `ExistingStreamIdCollisionException` is the more useful answer. An `Archived` event reaching a
  single stream projection that owns the stream archives it too, through
  `FisherProjectionStorage.ArchiveStream`
- Live aggregation: `AggregateStreamAsync`, `AggregateStreamToLastKnownAsync`, over auto-discovered
  self-aggregating types
- Inline projections: `Projections.Snapshot<T>` and `Projections.Add`, applied during
  `SaveChangesAsync` in the same transaction as the events
- `FetchForWriting` / `WriteToAggregate` / `AppendOptimistic` / `FetchLatest` / `ProjectLatest`, with
  the shared opt-in second-level aggregate cache behind `FetchForWriting`
  (`Events.CacheAggregatesForWriting<T>()`)
- `EventOperations` implements the full `IEventStoreOperations` — see below for which members throw
- Document storage over Guid, string, int and long ids; numeric ids via Hi-Lo sequences (`fi_hilo`)
- `EventProjection.storeEntity` — an `EventProjection`'s `Create`/`Project` results are stored inline
- `DocumentStore.Advanced` — `Clean`, `ResetAllDataAsync`, `ResetHiloSequenceFloorAsync<T>`, the
  daemon escape hatches (`AdvanceHighWaterMarkToLatestAsync`, `TryCorrectProgressInDatabaseAsync`),
  the projection progress reads, `RebuildSingleStreamAsync<T>` and `DeleteAllTenantDataAsync`
- `DocumentStore : IEventStore` — the explorer reads (`GetRecentStreamsAsync`,
  `GetStreamMetadataAsync`) and `TryCreateUsage`; see below
- **LINQ** — `session.Query<T>()` over `json_extract`: where, ordering, paging, projections, grouping,
  aggregates and **joins** (`Join` / `GroupJoin(...).SelectMany(...)`, chained across any number of
  tables), with async terminals
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
- **Container-scoped projections and subscriptions** — `AddProjectionWithServices<T>` and
  `AddSubscriptionWithServices<T>`, so a projection or subscription takes injected services, resolved
  from a fresh IoC scope per unit of work
- **The command line** — `ISystemPart` and `IDatabaseSource` registered from both `AddFisher` and
  `AddFisherStore<T>`, so `db-apply` / `db-assert` / `db-patch` / `db-dump`, the `resources` commands
  and `describe` all see a Fisher store; plus `AssertDatabaseMatchesConfigurationOnStartup()`
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
- **Event upcasting** — `Events.Upcasters`, over the shared `JasperFx.Events.Upcasting` contract: an
  old stored event schema is reinterpreted as the current CLR event type on every read path, so the
  old type can be deleted from the codebase
- **Sessions and `SessionOptions`** — `QuerySession()` and `OpenSession(SessionOptions)` on the store,
  and enlistment: a session running on a connection or inside a transaction the caller owns
- **Session tracking** — an identity map, dirty tracking, and `Eject` / `EjectAllOfType` /
  `EjectAllPendingChanges`, through `SessionOptions.Tracking` and the `IdentitySession()` /
  `DirtyTrackedSession()` factories
- **Session listeners** — `IDocumentSessionListener` and `IChangeSet`, registered store-wide or per
  session, bracketing the unit of work the same way the outbox does
- **Cross-tenant writes** — `session.ForTenant(id)`, so one `SaveChangesAsync` writes several tenants'
  documents and streams in one transaction
- **Document foreign keys** — `Schema.For<T>().ForeignKey<TOther>(x => x.OtherId)`, enforced, with
  cascade options; the child column is the generated one a duplicated field creates
- **Declarative and store-wide configuration** — `[Index]` / `[UniqueIndex]` / `[DuplicateField]` /
  `[HiloSequence]`, `AddSubClassHierarchy()`, `StoreOptions.Policies` and `StoreOptions.InitialData`
- **Binary event bodies** — `JasperFx.Events.BinaryEventAttribute` or
  `Events.UseBinarySerializer<TEvent>(…)` over a supplied `JasperFx.Events.IEventBinarySerializer`,
  stored in an always-present `data_binary` BLOB column beside the JSON one
- **Document-side tooling** — `IDocumentStoreUsageSource`, `IDocumentStoreDiagnostics` and projection
  step-through, so a monitoring console sees the document half as well as the event half
- **Tracing** — an `ActivitySource` named `Fisher`, with spans for commits, queries and loads and a
  retry event that says a call waited on the write lock
- **Multi-store registration** — `AddFisherStore<T>` and `IConfigureFisher`, so several independently
  configured stores live in one container
- **Database-per-tenant** — `ITenancy` and a SQLite file per tenant, with per-database migration, the
  async daemon routed per database, and tenants that appear, suspend and resume at runtime
- **Transaction participants** — `ITransactionParticipant`, so an application's own writes commit in
  Fisher's transaction rather than contending with it for the file's one write lock
- **The store-agnostic document contract** — `JasperFx.Events.Documents`, implemented by Fisher's own
  session and store types with no adapter, so a consumer can hold a document session without naming
  Fisher
- **`Fisher.AspNetCore`** — streaming `IResult` types over the JSON reads, ETag/`304` handling, event
  stream results, and a high-water health check
- **`Fisher.EntityFrameworkCore`** — a `DbContext` saving inside Fisher's transaction, and projections
  whose documents are EF entities

Not implemented yet — do not assume these work. **There are no open issues as of 2026-08-12**, so this
list and the deliberate gaps in HANDOFF.md are the live account rather than the tracker; these are the
ones most likely to be assumed present:

- **A message bus** — the side-effect seam exists and the default outbox drops every message. That is
  the end state, not a gap: fisher#8 was closed wontfix, and delivery is a bus integration's job here
  as it is on both siblings.

### Registering a projection by type

`Projections.Add<T>(lifecycle)` (fisher#76) **hides an inherited overload rather than adding a missing
one**, and that is the whole content of the fix. `ProjectionGraph.Add<TProjectionType>` compiled on
Fisher already and went straight to `All.Add`, bypassing `Register` — so it invoked neither
`onAddProjection`, which registers the projection's event types (without which a read-only process
cannot resolve them by name), nor `FisherProjectionOptions.Add`'s `PublishedTypes()` sweep, **without
which the projection's document type is never mapped and its table is never created**. Both silent at
registration; the symptom is `no such table` on the first event, or a rebuild that finds nothing.

The `new` overload routes through the instance form, so the two spellings mean the same thing. Its
constraint is deliberately *weaker* than the base's (`ProjectionBase, new()` rather than also
`IProjectionSource`), because the instance form wraps a bare `IProjection` and refusing one here would
decline a projection the store runs perfectly well; every call that satisfied the base still compiles.
`registering_a_projection_by_type` asserts equivalence with the instance form rather than existence,
which is the property that was actually missing.

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

### Reads provision the table too

**Reaching that path from the write side only was fisher#74.** A `Query<T>()` or `LoadAsync<T>` against
a type nothing had written yet failed with a raw `no such table`, where Marten and Polecat provision it
and return an empty result. That is not an exotic shape — it is what every cold start does (resolve a
cache before anything populates it, list a collection on a fresh install), and it is **asymmetric in
the worst direction: it works on a warm database and fails on a fresh one**, so it passes in
development and fails on first deploy.

`FisherSession.EnsureDocumentTableForReadAsync` is the read-side entry, with three callers:

- **`FisherQueryProvider.CommandFor`**, which is where every LINQ terminal converges — the same reason
  the query span is opened there. A per-terminal call would be a dozen copies of one line and the one
  that got forgotten would be the terminal nobody exercises against a fresh database. The types come
  from `Statement.DocumentTypes`, which is the only thing on a statement that is not about rendering
  SQL; the walk follows `Subquery`, because a count or an aggregate over a paged query holds the real
  statement there, and each join adds its own side.
- **`LoadByIdAsync` / `LoadManyByIdAsync`**, which go through storage rather than through LINQ.
- **`MetadataForAsync`**, hand-built SQL and therefore its own caller — as it already is of the three
  implicit filters.

`CheckExistsAsync` and `LoadJsonAsync` need nothing, being routed through the LINQ path already.

Three things that are decisions:

- **A type with registered projection storage is skipped**, the same guard `FetchProjectionStorageAsync`
  applies. Its rows are not in a Fisher document table, so provisioning would create one nothing ever
  writes to.
- **An enlisted session asserts rather than creating**, which is exactly what the write path does and
  for the identical reason: the migration runs on its own connection and would block against the write
  lock the caller's transaction is holding — a session deadlocking against itself. Naming the type
  beats that, and beats the raw SQLite error it replaced.
- **`AutoCreate.None` is honoured: the on-demand path checks and declines** (fisher#81). Weasel's
  `ApplyAllConfiguredChangesToDatabaseAsync` upgrades `None` to `CreateOrUpdate`, because that call
  *is* the explicit "apply it" — correct for the call as Weasel means it, and wrong for a path whose
  whole point is that it fires implicitly, on the first write and (since fisher#74) the first read of a
  document type. So a store configured "the schema is not yours to change" was still issuing DDL from
  inside a session, while `HiloSequence` checked the same setting and declined. Both halves were
  answered together, because answering the read alone would make the weaker operation the stricter one
  — `a_read_and_a_write_agree_about_auto_create_none` still pins that they agree, having been rewritten
  to pin the refusal rather than the provisioning.
  - **An existing table is not an error**, so the common case for a store deploying this way — schema
    applied out of band — is untouched. `auto_create_none_is_happy_once_the_schema_has_been_applied`
    exists because "honours `AutoCreate.None`" could otherwise be an unconditional throw and still pass.
  - **A refused type is removed from the ensured-tables cache**, the same discipline that keeps the
    first-use migration uncached until it succeeds. Without it the second call succeeds silently and the
    failure resurfaces as `no such table` from wherever the caller went next — and worse, a read would
    cache the type and let the *write* through. Verified by removing it: two tests fail.
  - The message names the document type, the setting, and the call to make — strictly better than the
    raw `SQLite Error 1: no such table` about a name the caller never wrote.

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

**A member-valued `Decrement` onto a row that does not exist yet inserts the *negated* value**
(fisher#183, over the jasperfx#773 ruling): the insert branch applies the event to an implicit zero
row, so a first event carrying 5 lands the column at -5. Put the other way, which is the form worth
holding onto: a decrement event must never leave a column higher than it found it. Fisher inserted the
parameter unchanged until this wave, so a stream whose first event was a decrement of 5 landed at +5.
Marten was the only store that had it right; Fisher, Polecat and the first cut of the lifted Weasel
DSL were the majority and the majority was the wrong side. **The by-column form still inserts 0** —
that half of jasperfx#773 is open, all four stores agree, and it is no longer symmetric with
`IncrementMap`, which moved to inserting 1 in marten#5341.

**Weasel has since caught up**: weasel#574 shipped the same negation in
`Weasel.Storage.Flattened.DecrementMemberMap` in **9.31.0**, so the shared DSL and Fisher now agree
and adopting the DSL would no longer cost fisher#183's fix. That was the reason to check before
deferring the adoption, and it is not the reason it stayed deferred — see below.

**The shared flat-table DSL (weasel#568/#569) is deliberately not adopted yet.** What Weasel lifted is
the mapping model plus an `IFlatTableSqlDialect` seam; what Fisher would have to move across it is
where the cost is, and none of it is mechanical. Its column maps are `internal` and render through
`SchemaUtils.QuoteName` directly rather than through a dialect's `Quote`/`Existing` context, so every
one of them is a rewrite rather than a swap; `FlatTable : Table` exists to fold the store's logical
schema into the physical name, which is Fisher's isolation boundary and has no counterpart in
`FlatTableStatementBuilder`; and `FlatTableFeatureSchema` puts the table in the store's migration
rather than creating it lazily, which is the divergence from Polecat this section opens with. Set
against a shared model Fisher already matches behaviourally, that is churn through a projection type
the compliance suite covers, for no behaviour change. Worth doing on its own node with the fisher#183
semantics pinned first, not as a rider.

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

Six things that are decisions rather than mechanics:

- **The high-water mark simply is `max(seq_id)`.** Marten and Polecat must distinguish the highest
  sequence *issued* from the highest safe to *read*, because a PostgreSQL sequence or SQL Server
  IDENTITY hands out numbers outside the transaction — a writer can hold 7 uncommitted while 8
  commits ahead of it. On SQLite one writer per file plus `BEGIN IMMEDIATE` means a transaction's
  sequences fully commit before the next writer allocates any, and a rollback returns the number
  (`sqlite_sequence` is an ordinary table and rolls back with it). Committed sequences are contiguous,
  so `DetectInSafeZone` has no separate answer to give. **Do not reintroduce gap-skipping** — it would
  guard a state that cannot occur.
- **Non-stale is decided against the shards the store *registers*, not the rows
  `fi_event_progression` holds** (fisher#102). Reading it off the rows makes a shard that has not run
  yet invisible — with no row it has no sequence to be behind — so a store with two async projections
  was declared non-stale the moment the first reached the head. It is a *window*, from one shard's
  first commit to the next shard's, which is why it surfaced as an intermittent
  `rebuild_and_catch_up_compliance` failure on a loaded two-core CI runner and never locally. Behind
  `QueryForNonStaleData` it tells an application its data is current while a projection has never run.
  Three consequences, each deliberate:
  - **A registered shard with no row is stale**, so a wait with no daemon running now times out rather
    than returning early. That is the honest answer, and the message names the shards that recorded
    nothing, because "never started" and "still catching up" are different operational situations.
  - **A store with no async projections returns immediately.** The same rule was broken the other way
    round: with events present and no rows, the old `shards.Length > 0` clause was false forever, so
    `QueryForNonStaleData` on a store with no async projections *always* threw `TimeoutException`.
  - **A row for a shard nothing registers is ignored.** A projection removed from configuration leaves
    an orphan row that will never advance again — `DeleteProjectionProgressByShardNameAsync` exists for
    exactly those — and waiting on one would hang every subsequent call.
  `one_shard_at_the_head_does_not_speak_for_a_shard_that_never_ran` is the discriminating fact, and it
  needs *two* registered shards: with no rows at all the old rule waited too, so a store where nothing
  has run cannot tell the two rules apart.
  **The correlation itself is `JasperFx.Events.Daemon.ProjectionLagCalculator`'s now, not Fisher's**
  (jasperfx#619). That type's xmldoc names Marten's `WaitForNonStaleDataAsync` as one of the three
  independent implementations of exactly this semantic that the lift exists to collapse, and Fisher's
  was the third — so this is the shared spelling of fisher#102's rule rather than a change to it.
  Adopting it also brings two rules Fisher's own version did not have: a progression row is only ever
  consulted at the shard's *current version*, and a row whose name does not parse as a shard identity
  is dropped rather than string-compared (marten#5161). `IEventDatabase.FetchProjectionLagAsync` is
  the shared read the same correlation backs, and Fisher inherits it as a default interface method —
  no implementation, and the numbers rather than the wait for a caller who wants them.
  - ⚠️ **The wait's bar stays `max(seq_id)`, not `ProjectionLag.HighWaterMark`**, so
    `HasProgressionRow` and `Sequence` are what it reads and `IsCaughtUp`/`Lag` deliberately are not.
    The calculator measures each cell against the *persisted high-water row*, which is right for a
    status endpoint — a shard cannot pass a mark the agent has not published — and wrong for this
    caller: a session that just committed is asking about its own events, which are at `max(seq_id)`
    and may sit above a mark the agent has not reached. On SQLite that ceiling is honest with no
    safe-zone reasoning behind it, since committed sequences are contiguous, so it is strictly
    stricter than the mark. `a_mark_that_trails_the_committed_events_does_not_make_the_store_current`
    is the discriminating fact — it asserts every cell reports `IsCaughtUp` and requires the wait to
    time out anyway, so swapping the check for `IsCaughtUp` fails it by returning.
- **The session's operation queue is guarded, because the daemon is not a single caller.** JasperFx's
  `ExecutionStage` fans its executions out with `Task.WhenAll` and they all queue onto the *same*
  Fisher session, so two projection slices can call `QueueOperation` at the same instant.
  `List<T>.Add` is not thread-safe and fails silently here — two concurrent adds can leave the count
  incremented once, so one slice's document write never reaches the batch, which then commits the
  progression row for a range whose documents were only partly written. **That was fisher#13**, and it
  presented as a multi-stream rebuild intermittently missing one slice's document. Note how closely it
  rhymes with fisher#12: same silent outcome, one layer up. `concurrent_operation_queueing` pins both
  the add and the take.
- **So is every other lazily-built field on the session, and that half was missed for a year.** The
  queue got a lock out of fisher#13 and the fields beside it kept their `??=`, which is a read, a
  branch and a write — so two slices arriving together each construct one and **one is silently
  discarded with everything recorded on it**. Two of them are on the write path a slice actually
  takes: `IStorageSession.Versions` is resolved by the numeric and optimistic storages while
  *constructing* an upsert, on the calling thread, and `_queryProvider` is resolved by `Query<T>()`,
  which is how a slice reads what it is about to fold into. A discarded version tracker means a later
  guarded write compares against a version the session no longer remembers reading — a spurious
  `ConcurrencyException` at best, a stale write accepted at worst. One layer down,
  `FisherVersionTracker`'s two `Dictionary<Type, object>` fields were mutated unguarded by
  `ForType`/`RevisionsFor`, which is the shape that loses an entry outright.
  **This is marten#4657/#4667 reached from the other side**: there, concurrent slices were handed
  separate sessions that *shared* a `VersionTracker`, an `ItemMap` and a `ChangeTrackers` list; here
  they share the session itself, which is a shorter route to the same place. `FisherSession.LazilyCreate`
  is the one place a lazily-built field is now created, and `concurrent_session_tracker_state` pins it
  — four of its five tests fail without the guards.
  **The tracker's *inner* dictionaries stay unguarded, deliberately**: they are handed to a storage
  operation, which writes into one during postprocessing, and postprocessing runs inside
  `ExecuteBatchAsync`, which is strictly sequential because SQLite takes one writer per file.
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
  **`Weasel.Storage.EventLoaderBase` (weasel#566) is not adopted yet, and that is a judgement rather
  than an oversight.** The base is well shaped for Fisher — the paging, skip accounting, ceiling
  calculation and cancellation translation are all there behind an `IEventPagingDialect` seam, and it
  carries `ReportLastObservedSequence` (jasperfx#667), which Fisher genuinely lacks: a full batch whose
  every row was skipped currently takes a ceiling from a page with no surviving event. What it would
  cost is a SQLite `IEventPagingDialect` (page SQL *and* the skip-ahead probe, which Fisher has no
  caller for), a mapping of the loader's constructor-time allow-list work onto `EventTypeAllowList` —
  including fisher#191's expansion of a transformation's *source* names, which is the half that fails
  silently if it is lost — and a home for the `DaemonTrace.Record` call, for which the base offers no
  hook. Worth its own node with fisher#153 and fisher#191 pinned first, since both are regressions the
  compliance suites structurally cannot catch.
- **The generic interface's `IEventDatabase` parameter carries the answer** (fisher#57), where for a
  long time every one of them was ignored on the true-enough grounds that a Fisher store was one file.
  Under database-per-tenant it is not, and ignoring it would read one tenant's events and write every
  tenant's documents from them. `DocumentStore.DatabaseFrom` is the single place that resolution
  happens; a null falls back to the default database, which is right for every store that is not
  database-per-tenant.
- **Rebuild teardown checks for the table in C#, not in SQL.** SQLite resolves a table name when it
  *prepares* a statement, so a `where exists (select 1 from sqlite_master ...)` guard on the delete
  fails before the guard runs. Names come back from `sqlite_master` first and missing tables are
  skipped. Both halves of teardown — progression and documents — run in one transaction, because
  clearing progress without clearing documents replays a projection on top of rows it already wrote.

- **The high-water agent re-stamps `last_updated` on an idle cycle, and that is the only liveness
  signal there is** (fisher#60, sibling of marten#5181). The mark's row moves when the mark
  *advances*, which is a different question from whether the loop is *running* — a quiet store
  advances nothing and would otherwise be indistinguishable from a dead daemon. **The extended
  progression `heartbeat` column does not answer it either**: JasperFx's `ExtendedProgressionWriter.OnNext`
  returns early for `ShardState.HighWaterMark`, so nothing ever writes it for that row, and a health
  check reading it looks like it has a signal it does not have. `a_running_daemon_never_writes_the_heartbeat_column`
  pins the premise against a daemon that has genuinely run, because Marten's equivalent tests passed
  for years by seeding the column with raw SQL.
  **Throttled where Marten's per-tenant equivalent writes on every cycle**, and that difference is
  SQLite's: a write takes the file's one write lock, so touching at `SlowPollingTime` would make an
  otherwise read-only store a permanent 1 Hz writer with a WAL to check point.
  `EventStoreOptions.HighWaterLivenessInterval` bounds it (five seconds; zero turns it off and leaves
  the health check on the gap heuristic alone).

**The hosted service is the store's `IProjectionCoordinator`** (fisher#138). It registered only as an
`IHostedService` over an internal class implementing nothing else, so **both** documented routes to the
running daemon failed — the service was not resolvable, and the
`GetServices<IHostedService>().OfType<IProjectionCoordinator>()` fallback found nothing either. Marten
and Polecat have registered JasperFx's interface since jasperfx#430, so store-agnostic code could do
daemon operations against them and not against Fisher. **No shared suite covers this** — Fisher passed
all 37 suites throughout, which is jasperfx#732, the third instance of the pattern after jasperfx#700
and jasperfx#718.

- **JasperFx's interface, not a Fisher-local sub-interface.** Both siblings have a local one that adds
  no members; theirs are historical (Marten's predates the lift, Polecat copied it) and a
  store-agnostic consumer resolves the shared one anyway. Nothing new lands on Fisher's public API.
- **No `ProjectionCoordinatorBase`.** The siblings close over it for leadership election across nodes;
  Fisher refuses `HotCold` outright, so what is left of a coordinator here is the daemon cache and the
  pause/resume pair, which this class already had in all but name.
- **One instance, registered as the coordinator and resolved from there as the hosted service.**
  Registering them separately would run two daemons over one file — two writers contending for one
  write lock, which is the thing `_running` exists to prevent.
- **`StartAsync` had to learn to resume**, and this is the non-obvious half. It only ever called
  `StartAnyNewDaemonsAsync`, which *skips* a database already in `_running` — so after a pause it
  would have started nothing at all. It now restarts agents on the daemons it holds first, which makes
  a second `StartAsync` equivalent to `PauseAsync` + `StartAsync`, the property JasperFx's own
  coordinator base establishes and the one `ResumeAsync` rests on.
- **Pausing stops the tenant poller too.** It builds and starts daemons of its own, so leaving it
  running would let a tenant appearing mid-pause quietly begin projecting; "paused" has to mean paused
  for tenants that do not exist yet.
- **An ancillary store gets `IProjectionCoordinator<T>` and not the unmarked one**, so resolving
  `IProjectionCoordinator` is never ambiguous about whose daemons come back.

**`Advanced.ResetAllDataAsync` pauses a hosted daemon around the wipe, and that is a deliberate
divergence from Marten**, which leaves its daemon alone. The wipe deletes `fi_event_progression` out
from under agents holding their positions in memory: they carry on from where they were, record nothing
against an event store that now starts at zero, and every later `WaitForNonStaleData` times out naming
shards that recorded no progress. Silent until something waits.

- **The reason to diverge is that the alternative was unreachable rather than merely manual.** Until
  the coordinator existed there was no handle on the running daemon, so the caller this method
  overwhelmingly has — a spec fixture resetting between scenarios — could not have paused it by hand.
- **`DocumentStore.RunningDaemons` is the seam**, set by the hosted service on start and cleared on
  stop, because `Advanced` reaches the store and not the container. It is a claim about *right now*
  rather than about registration, and it is deliberately not a general escape hatch — application code
  resolves `IProjectionCoordinator` from DI, which is the store-agnostic route.
  **Unwrapped through `SecondaryStoreProxy`**, or an ancillary store — which arrives as its marker
  proxy rather than as a `DocumentStore` — would silently get the old behaviour.
- **The resume is in a `finally`.** A half-done wipe with the daemon left paused is the worse of the
  two failures: the caller sees the exception either way, and a daemon that never resumes turns one
  failed reset into every subsequent projection silently not running.
- **Only a daemon this process hosts is paused.** `ExternallyManaged`, a hand-built store, or another
  process all keep the hazard, which is the honest outcome since nothing here can reach them.
- `a_reset_leaves_the_running_daemon_able_to_project_again` needs **two rounds** — the first
  establishes real in-memory positions to be stranded, and a single-round test passes against the old
  behaviour. Verified by reverting: it fails with the reported `TimeoutException` verbatim.

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

### Composite projections

`Projections.CompositeProjectionFor(name, composite => …)` (fisher#19) — several projections as ordered
stages under one shard, rebuilt together in one pass. The issue guessed this would be close to free and
it nearly was: `FisherCompositeProjection` closes JasperFx's `CompositeProjection<,>` over Fisher's
session pair, and `CompositeIProjectionSource` (ported from Polecat) presents a bare `IProjection` as
something a stage can hold.

**What a composite buys, precisely: ordered execution in one batch and one rebuild pass.** It does
*not* let a later stage read an earlier stage's writes with `LoadAsync` — the whole composite commits
as one batch, so nothing an earlier stage queued is in the database yet. JasperFx's mechanism for
sharing across stages is the **aggregate cache** (`CompactCachesAsync` runs once at the composite
boundary rather than per stage, precisely so downstream stages can read upstream in-flight entities),
which aggregation projections participate in and a bare `IProjection` does not. A first version of
`composite_projections` assumed the database-read model and failed; the test now says so explicitly, so
the next reader does not have to rediscover it.

- **Composites are always asynchronous.** A stage boundary only means something inside a daemon batch;
  an inline composite would be a boundary with nothing on either side of it.
- **The child projections' event types are registered on the event graph by
  `CompositeProjectionFor`**, not by each child — a child inside a composite is never registered on its
  own and would otherwise contribute nothing to what the store knows how to deserialize.
- **`CompositeIProjectionSource`'s execution does not dispose the batch it is handed.** Every stage
  writes into one batch so the composite commits together; a stage disposing it would commit the
  earlier stages and leave the later ones writing into a disposed session.
- A document a bare `IProjection` stores still needs a registered mapping, since Fisher only creates
  tables for types the schema has mapped. That is the ordinary on-demand rule, not a composite quirk.
- **A member held by the wrapper has to be asked what it publishes, or a rebuild replays onto its
  surviving rows** (fisher#63, sibling of marten#5175). `CompositeProjection.PublishedTypes()` walks
  its stages' members, and `CompositeIProjectionSource` was constructed with a fresh, empty
  `AsyncOptions` and never told what the projection inside it writes — so teardown saw the wrapper and
  enumerated nothing, while the progression rows were deleted anyway. It now adopts a `ProjectionBase`
  projection's options and published types, exactly as JasperFx's own `ProjectionWrapper` does.
  **`Name` and `Version` are deliberately not adopted**: they compose the member's shard identity, and
  changing them orphans every progression row already written.
- **A raw `IProjection` that is not a `ProjectionBase` declares nothing, and the composite cannot
  invent it** — `Add(projection, options => options.DeleteViewTypeOnTeardown<T>())` is where that is
  said. Its rows otherwise survive a rebuild, which is pinned as a decision rather than left to look
  like the bug above.
- **The composite's own `Options` are its own, and were being dropped.** JasperFx's override returns
  the stages' types and therefore loses the `Options.StorageTypes` the base would have contributed, so
  `composite.Options.DeleteViewTypeOnTeardown<T>()` was a silent no-op;
  `FisherCompositeProjection.PublishedTypes()` puts them back.
- **An ordinary rebuild test cannot catch any of this.** A replay rewrites every row it can still
  produce, so a surviving row is invisible except where the replay *cannot* recreate it —
  `composite_member_teardown` plants one against an id no event mentions. Same discipline the
  flat-table (`IPublishesTables`) and EF Core teardowns each had to learn.

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
- **`FetchDeadLetterCountsAsync(tenantId)` is overridden** (fisher#77), where without it the call lands
  on JasperFx's default and *throws* `NotSupportedException` for a non-null tenant while the
  store-global overload beside it works — which a monitoring console meets as soon as it renders
  per-tenant badges. Cheap because the table is store-global and records the failing event's tenant as
  an ordinary data column, so it is the same query with a `where tenant_id = …` rather than a second
  shape. **A null tenant stays store-global and leaves `TenantId` null** rather than defaulting it, so
  a consumer keying by `{ProjectionName}:{ShardKey}` can tell "every tenant" from "the default
  tenant" — and rows the daemon recorded with no tenant are counted there and reachable from no
  tenant-scoped read, which is the honest answer for them.
- **A `DeadLetterEvent` is refused as a document**, in `DocumentSchema.MappingFor`. On both siblings it
  is *also* an ordinary document, so `session.Store(deadLetterEvent)` lands it in the very table
  `QueryDeadLetterEventsAsync` reads; here it is infrastructure with its own table and its own write
  path, so the same call compiled, succeeded, wrote a `fi_doc_deadletterevent` row and the query never
  saw it. **Fisher's arrangement is the better one and the divergence is still worth failing over**,
  because it is silent in the direction that hurts: ported code keeps working and quietly stops
  recording anything. The guard sits on the mapping rather than on `Store`, because every path into
  document storage — write, query, load, an explicit `Schema.For<T>()` — resolves a mapping first, so
  one guard covers all of them and cannot be reached around.

Ordering matters when clearing event data, and it is why `DeleteAllEventDataAsync` uses an ordered
pass rather than the cleaner's unordered one: `fi_event_tag_*` rows have a real foreign key to
`fi_events(seq_id)` and Weasel's default profile turns enforcement on, so clearing events first fails
with `FOREIGN KEY constraint failed` (fisher#6). Tags go first, dead letters last.

### Document storage layout

Weasel.Storage supplies the selectors and the write operations but **not** an
`IDocumentStorage<T,TId>` implementation to hold them together — Marten and Polecat each write their
own, and `FisherDocumentStorage<TDoc,TId>` is Fisher's. Around it:

- `DocumentProviderRegistry` (`IProviderGraph`) caches one `DocumentProvider<T>` per document type,
  holding all four flavors. Which one a session resolves is `SessionOptions.Tracking`'s answer — see
  "Session tracking".
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

### Tenant scoping in LINQ, and the marker operators

**fisher#51 was a cross-tenant read.** The tenant filter was applied by wrapping each caller predicate
through `IDocumentStorage.FilterDocuments`, so a query with *no* `Where` got no tenant term at all —
`Query<T>()` on a conjoined type returned every tenant's rows. Silent, and asymmetric in the way that
makes it hard to spot: the tenant owning most of the data sees a correct-looking answer with extras,
and a tenant with none sees somebody else's.

**It is the same mistake `ApplyHierarchyFilter` already documents**, whose comment names both halves —
composing a filter into `FilterDocuments` repeats it per predicate *and omits it from a query with
none*. fisher#17 learned that for the `doc_type` discriminator and fixed it there; the tenant filter
had the identical shape and was not revisited. All three implicit filters — tenant, hierarchy, soft
delete — are now one statement-level pass each, so no query shape can drop one. Do not fold any of
them back into a per-predicate wrapper.

Nothing caught it because `ConjoinedEventTenancyCompliance` covers the *event* store, Fisher's own
document tests were single-tenant, and `LoadAsync`/`LoadManyAsync` bake the tenant term into SQL built
once in the storage's constructor — so the bug was confined to the one path that composed it per
predicate. `tenanted_queries` now checks every statement shape in **both** directions.

Being its own pass is also what `AnyTenant()` and `TenantIsOneOf(...)` need: they *replace* the term,
which is impossible while it is welded to each predicate. Both are refused against a type that is not
`MultiTenanted()`, because there is no column to have an opinion about — the same rule the soft-delete
operators follow.

The rest of fisher#26's operators:

- **`IsOneOf` / `In`** produce the same `in (…)` as `EnumerableContains`, reached from the other
  direction — there the collection is the receiver, here the member is.
- **`IsEmpty()` has to test null as well as length.** `json_extract` yields SQL NULL for an absent key
  and `json_array_length(null)` is NULL rather than 0, so a bare `= 0` leaves the row out instead of
  matching. A caller asking "is this empty" means "is there anything in it", and "the key is not
  there" is an honest yes.
- **`ModifiedSince` / `ModifiedBefore`** compare `last_modified` as text with no `strftime` wrapper,
  because the column already holds `SqliteTimestamp`'s fixed-width UTC form — the same asymmetry as
  `DeletedSince` / `DeletedBefore`, and for the same reason. **`CreatedSince` / `CreatedBefore` are
  deliberately absent**: there is no `created_at` column to answer from (fisher#29), and answering from
  `last_modified` would be a different question asked with a straight face.
- **`QueryForNonStaleData` waits for the whole store**, where Polecat waits for the projections feeding
  the queried type. Stricter rather than weaker, and it needs no type-to-shard map. It is a wait, not
  SQL, so it hangs off `Statement.NonStaleTimeout` and is read through `EffectiveNonStaleTimeout`,
  which walks the subquery chain — that way wrapping a statement for a count, a page, an aggregate or a
  reversal carries it without each wrap site having to remember.

One test-shaped lesson worth keeping: `modified_since_and_before` first took its boundary from
`DateTimeOffset.UtcNow` between two writes. It failed once and then passed on every rerun, which is the
worst possible signal. `last_modified` is written by SQLite's `strftime('now')`, so a client-sampled
bound compares two clocks that are only incidentally the same one. The test now reads the stored value
back — removing the question rather than widening the window and hoping.

### LINQ projections

`Select`, `Distinct` and `DistinctBy` (fisher#23). Before this every query materialised the whole
`data` column and deserialized it, so `Select(x => x.Name)` over a table of large documents was the
difference between reading one JSON scalar per row and reading every document — and Fisher refused it
rather than answering it expensively, which left the caller with only the expensive shape.

**`SelectProjection` rewrites rather than interprets.** Every document member reachable in the lambda
body is collected and replaced by an indexer into an `object?[]` of values read from the row; the
rewritten body is compiled once. So the *shape* of the projection — anonymous type, constructor,
object initialiser, interpolation, arithmetic — is whatever C# already allowed and none of it needs
translating.

- **Only member accesses become columns; everything around them runs in .NET, per row.** That is the
  boundary, and it is deliberate: `Select(x => x.First + " " + x.Last)` reads two columns and
  concatenates client-side. Marten's answer is the same, and it keeps the surface honest — there is no
  set of expressions that silently falls back to reading whole documents.
- Members are deduplicated by locator, so `new { A = x.N, B = x.N * 2 }` selects `n` once.
  `repeated_members_are_selected_once` asserts on the analyzer directly, because the *result* is
  identical either way — two columns holding the same value compute the same answer while reading
  twice as much.
- **Projected values go through the same `CoerceTo` as an aggregate's**, so an enum, a timestamp and a
  Guid convert the way fisher#22 established rather than through `Convert.ChangeType`.

**The provider had to split source type from result type.** A projection makes the queryable's element
type diverge from the document's — `Query<Catch>().Select(x => x.Species)` is an `IQueryable<string>` —
so the terminals can no longer take the document type from their own type parameter. `SourceTypeFor`
walks to the root `ConstantExpression`, which holds the original `FisherQueryable<T>`, and
`BuildStatement` is non-generic so one path serves both. `ToListAsync`, `FirstAsync`, `LastAsync`,
`CountAsync` and `AnyAsync` all handle a projected query; a projected `CountAsync` wraps rather than
replacing the select list, because `count(*)` over `select distinct …` is the only shape that answers
"how many distinct values".

Three restrictions, each refused by name:

- **`Distinct()` requires a `Select`.** Over whole documents DISTINCT compares serialized JSON byte
  for byte, so two documents equal in every member but written with different serializer settings, or
  with members in a different order, count as distinct. It would look right on small test data.
  `DistinctBy` is the operator for documents.
- **`DistinctBy` is refused after a `Select`** — use `Distinct()`. It emits
  `row_number() over (partition by …)` filtered to 1, because keeping one *whole document* per key is
  not something DISTINCT can express. SQLite has had window functions since 3.25.
- **Ordering after a `Select` is only `OrderBy(x => x)` over a single-value projection**, which is the
  `Select(...).Distinct().OrderBy(x => x)` idiom. Ordering by a member of a *shaped* projection is
  refused rather than unimplemented: the mapping back from an anonymous member to a locator exists
  only while the projection is a plain member-for-member copy, and the whole point of the rewrite is
  that it need not be. Ordering before the `Select` always works.

One `Select` per query; a second would have to project members of the first's result, which is not a
document and has no locators.

**A NULL column becomes the target type's default, not a null.** `json_extract` yields SQL NULL for an
absent key and an aggregate over an empty group yields NULL too, and the compiled projection *unboxes*
each value to its declared type — so a null reaching a non-nullable value type is a
`NullReferenceException` thrown from generated code, with nothing in the message to say which column
or why. Defaulting matches what deserializing the document would have produced for an absent key, and
what the aggregate terminals already return over no rows. Found while building fisher#24, and it
affected both projection paths; pinned by `a_null_column_becomes_the_default_rather_than_throwing` in
both test classes.

### LINQ joins

`Join` and `GroupJoin(...).SelectMany(...)` across document tables — `Linq/Joins/`. One join is
fisher#25; a chain of them is fisher#55, and the difference between the two is one type
(`JoinShape`).
**This is the LINQ tier where SQLite is the easiest of the three dialects rather than the hardest**: a
join between two document tables is `join fi_doc_catch inner_t on outer_t.id =
json_extract(inner_t.data, '$.anglerId')`, with no `OPENJSON`, no lateral join, and an expression index
(fisher#16) usable on either side. It is also worth *more* here than on either sibling — the usual
argument against joins in a document store is that a round trip is cheap next to a join's cost, and an
embedded store has no round trip to be cheap; the alternative is two statements and a client-side
stitch.

- **The join is on the ordinary `Statement`, not a parallel one.** Polecat's `JoinStatement`
  re-implements the select list, the wheres, the ordering and the paging, so anything built for one
  shape has to be built again for the other — its join path carries its own `Count` and its own
  `TOP`/`OFFSET` rendering. Fisher's `Count`, `Any`, `ToPagedListAsync` and `ToSql` serve a join
  without knowing it is one. The one place that had to learn about joins is `WrapAsSubquery`, which
  must carry `Joins` and `FromAlias` or a count over a paged join counts the outer table instead.
- **The alias goes into `MemberFactory`, so every locator is built qualified.** Polecat rewrites the
  rendered string afterwards (`AliasingCommandBuilder`, `JoinStatement.AliasLocator`), which produces
  valid SQL that reads the wrong table whenever the pattern matches something it should not. Building
  `json_extract(outer_t.data, …)` from the start cannot — and the alias belongs *inside*
  `json_extract`, on `data`, not on its result. `a_member_both_sides_have_reads_its_own_table` is the
  case that tells the two apart, and removing the qualifier fails 17 of the 27 join tests.
- **Everything about the inner side goes in the `ON` clause; a post-join `where` goes in the `WHERE`.**
  Not an inconsistency — an inner-side filter says which rows the join may *match*, a post-join
  predicate says which joined rows *survive*. On a left join the two differ visibly: the first keeps an
  unmatched outer row and the second may remove it, which is exactly what the same clauses do in
  memory. Putting an inner-side term in the `WHERE` turns a left join back into an inner one, silently
  and only for the rows the left join exists to keep; moving them there fails five tests.
- **The inner query's own predicates are applied. Polecat drops them silently** — it collects only the
  tenant and soft-delete filters for its inner table, so `GroupJoin(session.Query<Catch>().Where(...))`
  there returns rows the caller excluded. Fisher parses the inner source with the same parser and the
  inner alias; anything beyond filtering (ordering, paging, projection) is refused, being a question
  about one outer row's matches after the join has flattened them.
- **All three implicit filters apply per side**, through the same statement-level passes the unjoined
  path uses, now taking a qualifier. That is fisher#51's lesson held to: a fourth caller composing its
  own tenant term is how that bug happened.
- **The inner document is materialized by its own storage's selector, through an offsetting reader.**
  A closed-shape selector reads from fixed positions (id 0, data 1, metadata 2+), so the inner side —
  whose columns start after the outer's — needs the *reader* shifted rather than the selector changed.
  `OffsetDataReader` is that. Polecat's join handler instead calls the serializer on the `data` column
  directly, which loses `doc_type` resolution — a sub-class comes back as its base, quietly missing
  whatever it added — and the metadata binders with it. `a_joined_hierarchy_comes_back_as_its_sub_classes`
  is the dividend.
- **Both spellings collapse to one lambda over the two documents.** A `GroupJoin`'s pair of selectors
  and a query-syntax `Join`'s transparent identifier are the same shape one call apart, so
  `JoinResultSelectorRewriter` serves both; a plain `Join` that spelled its result out needs no rewrite
  at all. **A `GroupJoin`'s second parameter is the group, not a row**, and is deliberately left
  unmapped — an expression still naming it (`x.catches.Count()`) is asking about rows the join has
  flattened, and is refused rather than silently answered about the one matched row.
- **A predicate or ordering key written after the join is resolved against whichever shape it names**,
  decided by its parameter's type rather than by trying one and falling back: method syntax names the
  projected result, query syntax's `where`/`orderby` come before its `select` and name the intermediate
  shape. Both land on the projection's own two parameters, so which side a member belongs to is decided
  **by parameter reference, not by type** — a self-join has the same type on both sides.
- **A member the projection computed is refused rather than sorted or filtered on**, since its value
  exists only after the row is read.
- **The scalar aggregates and `Last` work over a join** (fisher#54), and cost one seam: `JoinPlan.Member`
  holds the post-join member mapping as a closure, so a terminal reaches the same attribution the
  `Where` and the `OrderBy` already used rather than re-deriving it. The two aggregate guards are the
  *resolved member's*, so they apply unchanged. Two SQLite-shaped details:
  - **The aggregate's paged subquery carries `Joins` and `FromAlias`**, exactly as `WrapAsSubquery`
    does — a qualified locator inside a subquery that dropped them is `no such column`, which is the
    one mercy of qualifying: it errors rather than aggregating the outer table alone.
  - **A paged `Last` cannot reuse `ReverseOverPage`.** That one works unjoined because
    `json_extract(data, …)` resolves against the subquery's own `data` column; a join's locator says
    `json_extract(outer_t.data, …)` and the alias does not survive into the enclosing scope. So
    `ReverseJoinOverPage` aliases each ordering key into the page's select list and orders by the
    alias — the trick keyset paging already uses. Trailing columns are safe because both selectors
    read from fixed positions at the front of the row.
- **More than one join per query works** (fisher#55), and what a chain needed was one idea rather than a
  rewrite. `Statement.Joins` was already a list that rendered in order; what a second join could not do
  was resolve its *outer key*, because it is written against the **shape the previous join produced** —
  `x => x.catch.WaterId` names no document until that shape is resolved back to one. `JoinShape` is
  that: per rung, each member of the shape as an expression over the sides' parameters, composed
  forward. Everything that looked like it would need generalising — the offsets, the left-join null
  check, the result selector's arity — turned out to be the same code already written for a list that
  happened to have two entries, so `JoinPlan` holding an outer and an inner became `JoinPlan` holding
  `JoinSide`s.
  - **A shape has to be carried as a whole as well as member by member.** A `GroupJoin`'s own selector
    writes `(y, waters) => new { y, waters }`, where `y` is the *entire* previous rung rather than a
    member of it — so `JoinShape.Body` is mapped directly and member accesses go through the map.
  - **Third and later joins need member folding; the second does not.** A second join's shape holds
    documents, so `x.c.WaterId` resolves to a plain `inner_t.WaterId`. A third join's shape holds the
    *second's shape*, so `z.y.a.Name` would otherwise resolve to `new { a = t0, c = t1 }.a.Name` — a
    legal expression tree that evaluates correctly in memory and is not a member chain rooted at a
    parameter, which is the only thing the member factories and the where parser translate.
  - **The group stays unmapped, and that is what refuses a question about it.** Dropping a
    `GroupJoin`'s group from the shape's members leaves an expression naming it still rooted at the
    shape's own parameter after the rewrite, which is exactly the condition reported as untranslatable.
    Mapping it would silently turn `x.catches.Count()` into a count of the one matched row.
  - **`outer_t` and `inner_t` are kept rather than renumbered to `t0`/`t1`**, with a chain numbering
    from `inner_t2` on. `ToSql` exists to be read, one join is overwhelmingly the common case, and the
    two names say which side is which where a number does not.
- Refused by name, each with the alternative: keyset paging, JSON reads, and
  `Select`/`GroupBy`/`Distinct`/`DistinctBy` after the join. `ToListAsync`, the `First`/`Single`/`Last`
  families, the scalar aggregates, `CountAsync`, `AnyAsync`, `ToPagedListAsync` and `ToSql` all work,
  over a chain as well as over one join.

### LINQ paging

Two operators answering different questions, both carried for the reason Polecat and Marten carry both
(fisher#27). `ToPagedListAsync` can jump to an arbitrary page and reports a total; `ToCursorPageAsync`
can do neither, but is stable under concurrent writes and does not degrade as the offset grows.

- **The page's total is a second statement, not `count(*) over ()`.** A window function returns *no
  row at all* when the page is past the end — which is exactly when a pager most needs the real total.
  Same reasoning as `a_page_past_the_end_still_reports_the_total` in the event-store explorer's paging,
  and pinned the same way here.
- **`CountIgnoringPagingAsync` is deliberately distinct from `CountAsync`.** The latter counts the
  *page* when the query is paged (`Take(5).CountAsync()` is 5); the former discards `Take`/`Skip`
  because a total that counted the page would say nothing. Both are right for their caller; conflating
  them would make one silently wrong.
- **Keyset pagination requires a terminal identity key**, and this is the guard that makes the rest
  honest. Without a total order, rows tied on the sort key have no defined order between them and a
  seek boundary lands mid-tie — skipping some and repeating others, silently, and only when there are
  ties. Verified by removing the check and walking a fully-tied key: the walk loses rows.
- **The seek is the expanded OR-of-ANDs, not SQLite's row-value comparison.** Row values (available
  since 3.15) would be one comparison the planner could serve from a composite index, but they only
  express a seek when every key runs the same direction — and mixed direction is the common case
  (`OrderByDescending(x => x.Landed).ThenBy(x => x.Id)`). Special-casing uniform orderings is an
  optimisation, not a correctness matter.
- **Cursor values are typed on decode by the query's ordering members, never by the cursor.** The
  payload carries no type information, so a hand-edited cursor can change values but not what they are
  read as. Every value then binds as a parameter. The `v1:` base64-JSON format is byte-identical to
  Polecat's so a cursor is portable between the stores.
- **`CursorPage<T>` is typed where Polecat's `CursorPageResult` is pre-rendered JSON.** Polecat's shape
  exists to feed a `StreamPagedByCursor` HTTP result in its ASP.NET Core package; Fisher has neither
  that package (fisher#49) nor JSON-returning reads (fisher#28), so a JSON result would be a shape with
  no consumer. The JSON variant belongs with fisher#49, built on this.
- Ordering keys are read **off the row**, not off the materialized document — a key can be any locator,
  including one no member of the result exposes. They are appended to the select list *after* the
  document's own columns, which is safe because the storage selector resolves from fixed positions
  starting at 0.

### LINQ grouping

`GroupBy`, a `Select` over the group, and `HAVING` (fisher#24). `GroupProjection` uses the same
compiled rewrite as `SelectProjection`; what differs is what counts as a column — over a group there is
no document parameter, only `g.Key` and aggregates over the group. `GroupingTranslator` turns those
into SQL and is shared with the HAVING parser, so the two cannot disagree about what `g.Count()` means.
`RowProjection` is the common shape both projections reduce to, so the provider has one projected read
path rather than two that would drift.

**The trap this feature was expected to have does not exist.** SQLite permits a bare non-aggregated
column in a `GROUP BY` select and picks an arbitrary row for it, where T-SQL rejects the query — so a
query that errors on Polecat would silently return arbitrary data here, and the plan was to validate
it in the parser. It is unreachable through this API: the `Select`'s parameter is the *grouping*, so
there is no ungrouped member in scope to select. The type system does the validation for free, and
that is worth knowing before someone adds a validator for a case that cannot arise.

- **Where a `Where` sits decides what it filters.** Before the `GroupBy` it is a `WHERE` over rows;
  after it, a `HAVING` over groups. The chain is walked source-outward, so which one it is falls out of
  whether the key has been seen yet — no lookahead.
- **The HAVING parser is deliberately narrower than `WhereClauseParser`**: a comparison between a
  grouping expression and a constant, composed with `&&`/`||`/`!`, with reversed operands flipped
  (`1 < g.Count()` is `count(*) > 1`). Widening it would mean answering questions about individual
  rows from a clause that runs after they have been collapsed.
- **Aggregates over a group reuse fisher#22's two guards**, for the same reasons — `sum()` over text
  returns 0 rather than failing, and a string-stored enum does not order.
- **Ordering a grouped query is by the key or an aggregate**, which is the reason grouping is usually
  reached for (`OrderByDescending(g => g.Count())`). It must come *before* the `Select` in the chain,
  because after it the element is the projected type; after a single-value grouped `Select`,
  `OrderBy(x => x)` works the same way it does for an ordinary projection.
- **`GroupBy` without a `Select` is refused**, rather than handing back `IGrouping` instances — that
  would mean reading every row of every group, which is the opposite of what grouping in SQL is for.
  The element- and result-selector overloads are refused for the same reason.

### LINQ aggregates and `Last`

`SumAsync` / `MinAsync` / `MaxAsync` / `AverageAsync`, `LastAsync` / `LastOrDefaultAsync`, and the
predicate overloads of the existing terminals (fisher#22).

**They never enter the expression tree.** Polecat builds a synthetic `MethodCallExpression` carrying
the selector and parses it back out; Fisher's terminal extensions take the selector as an argument, so
`LinqQueryParser` needed no change at all and stays what its doc comment says it is — a description of
the operator *chain*. The predicate overloads are `queryable.Where(predicate).XAsync()`, so they
compose rather than duplicating anything.

**The aggregate builds from the chain's source type, not from its element type** (fisher#54). Both a
join and a `Select` make the queryable's element type diverge from the document's, and asking the
schema for a mapping of *that* is an `InvalidOperationException` about identity members — a message
naming neither the operator nor the reason. Since `SourceTypeFor` already answers the question for
every other terminal, the aggregates now use it too: a join is answered, and a projection is refused as
a `BadLinqExpressionException` naming the operator. `an_aggregate_after_a_select_is_refused_by_name`
pins the second half, which was a real defect rather than a missing feature.

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

### Sessions, `SessionOptions` and enlistment

`DocumentStore.QuerySession()`, `OpenSession(SessionOptions)`, and `Internal/Sessions/` (fisher#30).
The store used to expose exactly one factory, `LightweightSession(tenantId)`, against Polecat's eight.

**Enlistment is the part that matters, and it is worth more here than the same feature is on either
sibling.** One writer per file, and an application using Fisher keeps its own tables in that file — so
without a way to hand Fisher an open transaction, "my rows and Fisher's, or neither" means taking the
write lock twice and contending with yourself. `QueueSqlCommand` (fisher#34) answers the same problem
from the other direction.

**Three modes, one rule each.** No `Connection` and no `Transaction` is the ordinary session; a
`Connection` alone means Fisher opens and commits its own transaction on it and **never disposes a
connection it did not open**; a `Transaction` means Fisher **neither commits nor rolls back**. Marten's
`OwnsConnectionLifecycle` / `OwnsTransactionLifecycle` pair is deliberately absent — four combinations
of which two are traps, in place of two rules that are always true.

`IConnectionLifetime` is much narrower than Polecat's interface of the same name, which wraps every
execution. Fisher's session hands its connection out rather than executing through a lifetime object
(the append planner, the tag writer and the boundary check all take a connection *and* a transaction),
so it carries only the two things that actually vary: where the connection comes from, and the
caller's transaction to join.

Five decisions in the enlisted path, each of which would be silently wrong the other way:

- **`command.Transaction` is set on every command, and without it enlistment does not work at all.**
  Microsoft.Data.Sqlite refuses to execute a command whose `Transaction` is unset while its connection
  has a pending local transaction — *"Execute requires the command to have a transaction object"*. A
  command from `connection.CreateCommand()` inherits the connection's transaction and never meets
  this; **Weasel's command builder compiles a detached command**, which is what every Fisher statement
  is. `FisherSession.ConfigureCommandAsync` is the one place that sets it, and routing the four
  command sites through it is what keeps that true. Verified by removing the line: six of
  `session_options`' tests fail with that exact message.
- **No resilience pipeline.** An ordinary commit can be retried after `SQLITE_BUSY` because the failed
  attempt's transaction rolled back with it. An enlisted one did not — it is the caller's and still
  open — so a retry would write everything the first attempt already wrote a second time. The busy
  surfaces to the caller instead. Same property as `StreamAsync` and `AfterCommitAsync`.
- **No post-commit step.** An outbox's `AfterCommitAsync` and the append observer both claim "everyone
  can see this now", and Fisher is not told when the caller commits. Neither fires; `BeforeCommitAsync`
  does, as the last thing the session writes.
- **Document tables are not created on demand.** That path runs a migration on its own connection,
  which would block against the write lock the caller's transaction is holding — a session deadlocking
  against itself, presenting after thirty seconds as `database is locked`. A missing table throws by
  name instead. The existence check runs on the *caller's* connection, so a table created inside the
  same transaction counts, which is what makes "your tables and Fisher's in one transaction" work.
- **A deferred caller transaction weakens the append guard, and Fisher cannot warn about it.** The
  append planner reads a stream's version and writes version+1 on the strength of holding the write
  lock. SQLite still refuses the second writer, so there is no lost update; what changes is that the
  loser gets `SQLITE_BUSY` at first write rather than a clean concurrency failure. The provider reports
  `Serializable` for a deferred transaction and an immediate one alike, so the two are
  indistinguishable from outside — documentation is the only instrument available.

**`SessionOptions.IsolationLevel` is carried for parity and refuses exactly one value.** Verified
against Microsoft.Data.Sqlite 10.0.9: `Unspecified`, `ReadCommitted`, `RepeatableRead` and
`Serializable` all produce the same `BEGIN IMMEDIATE` and all report `Serializable` back; `Chaos` and
`Snapshot` are refused by the provider. **`ReadUncommitted` is refused by Fisher** — it is the one
value that begins a *deferred* transaction, and nothing would signal the loss, because the transaction
still describes itself as `Serializable`. So Polecat code setting `ReadCommitted` (its default) ports
across and behaves identically.

**`SessionOptions.Timeout` means something different here than on either sibling, in two ways.** It
bounds how long a *statement* waits for the write lock before `SQLITE_BUSY`; it does not interrupt a
query that is genuinely slow. And it does not bound the wait at `BEGIN IMMEDIATE`, because the
transaction is begun on the connection rather than through a command — that busy wait comes from the
connection string's `Default Timeout`, 30s by default. Both halves verified.

**`Tracking` and `Listeners` were both absent as knobs that would silently do nothing, and both now
mean something** — see "Session tracking" and "Session listeners".
**`LightweightSession(SessionOptions)` and `OpenSessionAsync` are absent** for a
related reason: tracking is a property of the options rather than a choice of constructor, so
`OpenSession` already *is* the lightweight one, and a session opens its connection lazily so the
second has nothing to await.

**`QuerySession()`'s narrowing is a convention, not a guarantee**, and fisher#30 asked for that to be
decided rather than left implied. It is the same session narrowed to the read interface, so a cast
gets a write handle back; a genuine query-only type would cost a connection per scope to express a
distinction the store does not make. Said on `IQuerySession` itself, and pinned by
`a_query_session_is_the_same_session_type_narrowed` so that making it real is a deliberate change.

### Session tracking

`DocumentTracking`, `SessionOptions.Tracking`, `IdentitySession()` / `DirtyTrackedSession()`, and the
`Eject` family (fisher#31). **Almost none of this was new machinery.** Weasel.Storage already ships
the identity-map *and* dirty-tracking closed-shape selectors, `ChangeTracker<T>`, and the
`ChangeTrackers` / `ItemMap` slots on `IStorageSession`; Fisher already had a whole
`IdentityMapFisherStorage<TDoc,TId>` family built and cached in `DocumentProvider<T>` with nothing
able to select it. What was missing was the mode, three one-member subclasses, and the session's half
of change detection.

- **A dirty-tracking storage is its identity-map storage with the selector replaced**, which is one
  overridden member and nothing else. Marten's `DirtyCheckedDocumentStorage` is the same one-line
  subclass, and for the same reason: the difference between the two flavors is entirely what happens
  when a row is *read* — Weasel's dirty selectors do everything the identity-map ones do and
  additionally register a `ChangeTracker<T>` per materialised document.
- **The identity map covers `Query<T>()`, not just `LoadAsync`.** fisher#31 asserted the opposite
  ("the map must be defeated by a query") and it is wrong about Marten, whose documentation says
  plainly: "applied to all documents loaded by Id **or Linq queries**". It falls out rather than being
  arranged — the LINQ provider resolves its storage through `IStorageSession.StorageFor`, so a query
  under a tracking session builds a tracking selector without knowing it did. Raw SQL
  (`AdvancedSql`) does bypass it, resolving the query-only flavor directly, which is also Marten's
  behaviour and for a real reason: a raw query names its own columns and may select no identity at all.
- **Storing a second *instance* under a mapped id throws**, as on Marten. That is the safety property
  the map exists for rather than a caching detail: two instances of one document, both stored, is a
  last-write-wins outcome indistinguishable from a lost update. A type declaring `IEquatable<T>` is
  taken at its word and exempted. A lightweight session keeps no map and does not check.
- **`SessionOptions.Tracking` defaults to `None`, where Marten's `OpenSession(SessionOptions)`
  defaults to its identity map.** Marten's default predates `LightweightSession()`; following it would
  silently give every existing `OpenSession` caller a map they did not ask for, *and* that throw.
- **`LoadManyAsync` preselects out of the map** and asks only for the ids it does not hold. Reference
  identity would survive either way — the identity-map selector returns the cached instance for a row
  it re-reads — so what the preselect buys is the read itself, which is the other half of the point.
  Pinned by deleting the row out of band between the two calls, which is the only way to tell "not
  read" from "read and discarded".
- **`ProcessChangeTrackers` runs before the emptiness check**, because a dirty session's whole point is
  that a unit of work with nothing queued may still have work to do; **`ResetChangeTracking` runs after
  the write and outside the resilience pipeline**, which is fisher#12's property again — re-baselining
  inside a retried delegate would leave the retry comparing against a snapshot it had already taken.
- **Reset has two halves and they fail differently.** Re-baselining stops a second commit rewriting a
  document nothing has touched; registering trackers for documents the unit of work *stored* is what
  makes dirty tracking apply to documents the session created rather than only to ones that pre-existed
  it. The first half is easy to test wrongly: a test that changes a document twice passes without any
  reset at all, because the document really did change both times. It is only observable as somebody
  else's write disappearing, which is what `a_committed_document_is_not_written_again_by_the_next_commit`
  plants. Verified by removing the reset — the two-changes test still passed.
- **A delete by id removes the change tracker as well as the map entry**, and they are not the same
  thing: the map decides what a later load hands back, the tracker decides what the next commit writes.
  A tracker left behind resurrects the row the caller just deleted, with nothing anywhere to report it.
  `RemoveDirtyTracker` is implemented once on the base rather than per flavor, because outside a dirty
  session the tracker list is empty and it returns immediately.
- **`Eject` matches by reference**, so ejecting one instance leaves a queued write made with a
  different instance alone — the distinction the map exists to make. `EjectAllOfType` cannot simply
  drop the map entry: a document hierarchy shares one table and therefore one entry keyed by the
  *base*, so entries whose key is not exactly the type are scanned and matching values removed
  individually. That handles a hierarchy without knowing it is one.
- **`EjectAllPendingChanges` keeps the identity map and clears the change trackers**, which looks
  inconsistent and is not: a tracker is a queued write that has not been asked for yet. Pending DCB
  boundaries go too — a boundary guards appends that are being dropped, so keeping it would fail a
  later commit on behalf of a unit of work that no longer exists.
- **The map's and the tracker list's *contents* are unguarded, and fisher#13 is the reason that needs
  saying.** They are exactly the kind of shared mutable per-session state the operation queue turned
  out to be. The difference is that a tracking mode is only ever chosen by whoever opens the session,
  and the one caller that drives a session from several threads — the async daemon — opens
  `LightweightSession()` everywhere. `the_daemon_opens_untracked_sessions` and
  `concurrent_session_tracker_state.the_daemons_own_sessions_are_untracked` both pin that, so making
  the daemon's sessions tracked has to be a deliberate act. Guarding the contents would also only be
  half a guard: Weasel's selectors hold their own reference to the same dictionary.
  **Their *creation* is guarded**, along with every other lazily-built field on the session — see the
  async daemon section. Not an inconsistency: it keeps the argument above about what the dictionary
  holds, rather than also about whether two callers can end up holding different dictionaries, and it
  costs a lock taken once per session.
- **There is no `QueryOnly` tracking value**, where Marten has one. Marten's names a session that
  cannot write; Fisher has none — `QuerySession()`'s narrowing is a convention — so a mode resolving
  the query-only flavor would make `Store` throw on a session the store hands out as writeable.

### Session listeners

`IDocumentSessionListener`, `Services/IChangeSet.cs`, `StoreOptions.Listeners` and
`SessionOptions.Listeners` (fisher#32). **The hard part was already done**: fisher#4 settled where a
commit hook goes and what the rest of the database can see when it fires, and pinned it by probing
over a separate connection. A session listener is a second client of that seam, and reuses the probe
rather than resettling the question.

- **`AfterCommitAsync` runs outside `StoreOptions.ResiliencePipeline`.** A retried `SQLITE_BUSY`
  re-executes the whole delegate, so a hook invoked inside it fires twice for a transaction that
  already committed. Fourth client of that property after the outbox (fisher#4), the batch's own input
  (fisher#12) and the subscription listener (fisher#21).
- **An enlisted session fires the before hook and not the after one.** "Everyone can see this now" is
  a claim only the caller's commit can make, and Fisher is not told when that happens — the same rule
  the outbox's after-commit hook and the append observer already follow.
- **An empty unit of work fires nothing**, as on Marten. Without it every no-op `SaveChangesAsync`
  would run every store-wide listener.
- **The async daemon's projection batch does not fire session listeners**, and that is a decision. A
  projection batch is the daemon's unit of work, not the application's; firing user listeners for it
  would run an application's `AfterCommitAsync` on the daemon's threads for every batch of every
  shard. JasperFx's `IDaemonChangeListener` is the hook for that side and Fisher already supports it.
- **Pending streams are collected *after* the before hook, where Marten collects them before.** Costs
  nothing, and makes "work queued in the hook joins this transaction" true of appended events as well
  as of documents. Marten's listener that starts a stream is appending to the *next* unit of work.
- **The two synchronous members are default-implemented**, which is both why a commit-only listener
  needs two methods and why a listener written against Polecat's two-member interface compiles here
  unaltered. Marten's `DocumentSessionListenerBase` exists for the same purpose and predates default
  interface members. `DocumentLoaded` / `DocumentAddedForStorage` needed no new seam either —
  `IStorageSession.MarkAsDocumentLoaded` and `MarkAsAddedForStorage` were already called by Weasel's
  selectors and Fisher's storages, and were empty no-ops. The listener list is composed once and
  cached per session, because `MarkAsDocumentLoaded` runs per row.
- **`IChangeSet.Deleted` is `IEnumerable<IDocumentDeletion>`, not `IEnumerable<IDeletion>`.**
  `Weasel.Storage.IDeletion` is already in this codebase, is the storage *operation* that deletes, and
  is referenced unqualified in the very file that builds one — so a second `IDeletion` one namespace
  away is the `DocumentMetadata` collision again. Members are unchanged, so a listener body ports; only
  a declaration naming the type has to be edited.
- **Classification tests `IDeletion` before the role, and that ordering is load-bearing.** Every
  deletion carries `OperationRole.Deletion`, *including the soft form whose statement is an `UPDATE`* —
  so a role-first switch would route by-id deletions through the predicate branch and report every one
  of them with a null id. A predicate delete really does report a null id, because it named no row.
- **`Clone()` returns `this`.** On Marten the change set *is* the live unit of work, which is reset
  after every commit, so retaining one without cloning watches it empty out. Fisher builds it from the
  operations snapshot `TakePendingOperations` already took — the same snapshot the transaction wrote
  from, for fisher#12's reason — so it is immutable by construction. The member is carried so a
  listener that clones out of habit still compiles.
- A patch, a raw `QueueSqlCommand` and an `UndoDeleteWhere` appear in no bucket: none of them carries a
  document, and inventing one would be worse than the omission. Marten is the same.

### Cross-tenant writes

`session.ForTenant(id)` returning an `ITenantOperations` (fisher#33), so one `SaveChangesAsync` writes
several tenants' rows in one transaction. `IDocumentOperations` — everything that queues work, minus
the commit — was split out of `IDocumentSession` to express it, as it is on both siblings.

**This is the one place SQLite's single-writer model is the advantage rather than the constraint.**
The alternative is a session and a transaction per tenant, which on one database file means taking the
write lock N times in sequence and leaves a part-written admin operation if the process dies between
two of them. A database-per-tenant store would need a distributed transaction to match what falls out
here for free.

- **A tenant scope is a real `FisherSession`, not a delegating facade.** Polecat's
  `NestedTenantSession` is 250 lines of one-line delegation, and a delegation site that forgot to pass
  the tenant would be a silent cross-tenant write — fisher#51's exact failure mode. Everything a scope
  does differently is "read a different `TenantId`", and a session already reads its own everywhere, so
  a second session is the version with no per-member correctness to get wrong. `FisherSession`
  therefore implements `ITenantOperations` as well, and `ForTenant` returns one.
- **What is shared and what is not, each way round for a reason.** Shared: the connection lifetime and
  the operation queue — that *is* the feature. Not shared: the `EventOperations`, whose pending-stream
  dictionary is keyed by stream id and would merge two tenants' same-id streams into one; and the
  identity map, change trackers and version tracker, all keyed by document identity, which is unique
  per tenant rather than globally. Shared by delegation: correlation id, causation id, user name and
  headers, which describe the unit of work rather than the tenant.
- **The append path needed nothing.** `AppendPlanner` already wrote `stream.TenantId` rather than the
  session's, so a cross-tenant append works the moment the `StreamAction` is stamped with the scope's
  tenant. `SaveChangesAsync` gathers the scopes' pending streams alongside its own — they are gathered
  rather than queued as operations because planning happens inside the write transaction.
- **`StorageFor<T>` is the single choke point for refusing a single-tenant type**, and it covers every
  read and every write because all of them resolve storage through it. A type without `MultiTenanted()`
  has no `tenant_id` column, so a write "for another tenant" would land in the one shared table and look
  like it worked. `EventOperations.TenantId` is the event-store half of the same rule.
- **Scopes are flattened and cached per tenant.** `ForTenant` twice is the same scope, so its queued
  events are collected once; a scope of a scope is a scope of the session, so the parent has one level
  to walk. `ITenantOperations` deliberately does not offer `ForTenant` — the flattening exists for
  whoever finds the cast.
- **A scope cannot commit and disposes nothing.** `SaveChangesAsync` throws naming `Parent`; `Dispose`
  returns without touching the shared connection, so `await using` out of habit does no harm.
- The scopes' DCB boundaries are checked by the parent inside its transaction, for the same reason
  their operations are written there: a guard checked in no transaction guards nothing.

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
  delta detection used the former, so every duplicated column read as missing and the migration
  emitted `ALTER TABLE … ADD COLUMN` for it *every time* — and since Fisher runs a migration on the
  first write of each document type per process, the second one failed with `duplicate column name`.
  **Fixed upstream in Weasel.Sqlite 9.24.0** ([weasel#426](https://github.com/JasperFx/weasel/issues/426)),
  which reads `table_xinfo` and filters `hidden <> 1` — so generated columns (2 and 3) come back and a
  virtual table's hidden columns do not. Fisher carried a `DocumentTable.ConfigureQueryCommand`
  override until 9.25.0 and **it is gone**; do not reintroduce it.
  `applying_the_configuration_again_is_a_no_op` is still the test that catches this either way.
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

### Configuration layers, attributes, policies and initial data

fisher#39 — the declarative and store-wide halves of configuration: `[Index]`, `[UniqueIndex]`,
`[DuplicateField]`, `[HiloSequence]`, `AddSubClassHierarchy()`, `StoreOptions.Policies` and
`StoreOptions.InitialData`.

**There are now four configuration layers, and their order is the thing to remember.** A store policy,
then the JasperFx metadata interfaces, then the schema attributes, then `Schema.For<T>()` — each
overriding the one before. Weakest first, for a reason that reads off the layer: a policy was written
without knowing about the type it lands on; an interface is intrinsic to the type but says nothing
about this store; an attribute is on the type and about storage; the DSL names the type in this
store's own configuration. The first three run in `DocumentMapping`'s constructor, in that order.

- **The attributes are read where the mapping is created, not by an assembly scan.** A mapping is
  built lazily per type, which is the moment the type is known — so there is no world to scan and
  nothing to keep in sync.
- **Fisher's `[Index]` is deliberately narrower than Polecat's**, which carries `SortOrder`, `Casing`
  and `SqlType`. All three describe a *computed column*, which is what a Polecat index is built over;
  a Fisher index is an expression index and has no column to type, no casing to apply (SQLite's
  default collation is case-sensitive and the LINQ string operators are ordinal to match) and no
  direction worth naming. Carrying them would be three knobs that silently do nothing.
- **Members sharing an `IndexName` become one composite index**, in `GetMembers` order. That is the
  only reason a per-member attribute carries a name at all.
- **`AddSubClassHierarchy()` orders by full name, not by reflection order.** Two sub-classes whose
  default aliases collide have to fail the same way on every run, and `Assembly.GetTypes()` promises
  no ordering — a collision that appeared on one machine and not another would be the worst version of
  that error. Abstract and interface types are skipped, because a discriminator names something a row
  can be read back *as*.
- **`ForDocument<T>` is not `Schema.For<T>()`**, and the difference is the one that matters: it does
  not create the mapping. A type nothing ever stores stays unmapped and gets no table. It means "if
  you store one of these, store it like so".
- **`SeedInitialDataOnStartup()` refuses to be registered before
  `ApplyAllDatabaseChangesOnStartup()`.** Hosted services start in registration order, so the other
  way round writes to tables that do not exist yet — and that presents as `no such table`, which names
  the table and not the mistake.
- **No "already seeded" marker, deliberately.** A seeder that upserts by a known id is idempotent for
  free, which is what every useful seeder does; a marker table would be a table nobody asked for
  holding a claim Fisher cannot verify. Both siblings say the same.

**Partitioning is out of scope permanently, not pending.** Polecat's
`AllDocumentsAreMultiTenantedWithPartitioning()` and its relatives have no SQLite equivalent — no
partition functions, no partition schemes, no per-partition storage. The nearest thing is separate
tables behind a `UNION ALL` view, which carries none of the operational properties (partition
switching, aged-partition drop) that make the feature worth having. Said in `StorePolicies` so it is
not rediscovered as a gap.

### Document foreign keys

`Schema.For<T>().ForeignKey<TOther>(x => x.OtherId)` (fisher#38) — a real, enforced foreign key
between two document tables.

**SQLite's reputation invites the question, so state it plainly: it supports this completely.**
Foreign keys, `ON DELETE CASCADE` and `ON DELETE SET NULL` are all there. Enforcement is
per-connection through `PRAGMA foreign_keys` and off by default *in the SQLite library* — but on for
every connection Fisher opens, because Weasel's default profile sets it. That is the fact fisher#6
discovered the hard way, and it means a document foreign key bites the moment it is declared.

- **The blocker fisher#38 flagged does not exist.** It asked, correctly, whether SQLite accepts a
  `VIRTUAL` generated column as a foreign key *child*, because a "no" would have forced a `STORED` or
  written column and reopened the write-path question fisher#2 closed. **Probed against SQLite 3.50.4
  before anything was built** (the version Microsoft.Data.Sqlite 10.0.9 bundles): the table is
  created, an orphan insert fails, a row whose key is absent from the JSON is allowed, `ON DELETE
  CASCADE` works, and `pragma_foreign_key_list` reports it. So the write path is untouched and a
  foreign key costs index space only.
- **Declaring a foreign key duplicates the member implicitly, and that is a real divergence.** A
  constraint needs a column and a document member lives in `data`; the alternative is an error message
  telling the caller to write a `Duplicate(...)` line with no other purpose. On both siblings the two
  are already separate concepts because their duplicated columns are *written*. Here the column is
  generated, so folding one into the other loses nothing — and an explicit `Duplicate` on the same
  member still wins, because `DocumentMapping.Duplicate` is idempotent. The column is indexed, which
  SQLite wants anyway: without an index on the child column every parent delete scans the child table.
- **The referenced side is always the other type's `id`.** SQLite requires a foreign key to reference a
  `PRIMARY KEY` or `UNIQUE` column, and a document table's identity is its primary key. Referencing a
  duplicated field would need that field's index to be `UNIQUE`.
- **A document whose member is absent or null is unconstrained**, because `json_extract` yields SQL
  NULL and SQLite exempts a NULL child. Same asymmetry as a `UNIQUE` index over an absent member.
- **The referenced table is named unqualified, and that is forced rather than chosen.** SQLite's
  `REFERENCES` clause cannot be schema-qualified — and Fisher folds its logical schema into the table
  *prefix* rather than using real schemas, so the rendered name is already the whole name and two
  logical stores in one file each reference their own table.
- **`DeleteAllDocumentsAsync` now orders by foreign key, referencing tables first** — fisher#6's lesson
  one layer over. The order comes from `pragma_foreign_key_list` rather than from the store's
  configuration, so it is the database's account of what references what: a table left behind by an
  earlier configuration is still enforced and the store no longer knows about it. Verified by removing
  the ordering. `CompletelyRemoveAllAsync` still needs none — SQLite does not enforce a key against a
  dropped table.
- **A self-reference is refused at configuration time.** Not because SQLite minds, but because the only
  shape that wants one (a tree) has no insert order that satisfies the constraint for its own root.

Adding a foreign key to a type whose table already exists means recreating the table: SQLite has no
`ALTER TABLE ADD CONSTRAINT`, and Weasel reports that rather than attempting it.

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

### Session metadata on documents, and `MetadataForAsync`

`Storage/Metadata/` gained five opt-in columns and `IQuerySession.MetadataForAsync` (fisher#29). The
event store already copied the session's correlation id, causation id and user name onto every
appended event; a document written in the same unit of work got none of it, so an application could
answer "which request wrote this event" and not "which request wrote this document".

**Weasel.Storage already had every binder** — `DocumentCreatedAtBinder`, `DocumentCorrelationIdBinder`,
`DocumentCausationIdBinder`, `DocumentLastModifiedByBinder`, `DocumentHeadersBinder`,
`DocumentTenantIdBinder`. This is wiring, not machinery, which is what the issue hoped and rarely is.

- **`created_at` needs no exception to the `excluded.*` rule, and the obvious implementation does.**
  The upsert's `do update set` assigns every column in the *write list* from `excluded.*`, so a
  `created_at` contributed by a write binder moves forward on every save. Adding it and then carving
  it out of the set clause is the shape that suggests itself; instead the column is filled by its own
  parenthesized `NowDefaultExpression` DEFAULT and the binder is added to `readBinders` only. It is
  then in no INSERT column list and no set clause, and nothing has to remember why.
  `created_at_survives_an_update_and_last_modified_does_not` fails when it is made a write binder,
  which was verified.
- **`tenant_id` is read-only for a different reason**: it is part of the primary key and the storage
  operations bind it inline ahead of the binder loop, so a write binder would be a second writer of a
  value that already has one. Enabling its metadata column creates nothing — `MultiTenanted()` does
  that — so it decides only whether the value is projected back onto a member.
- **The four session-sourced binders are appended after every existing write binder**, which is what
  keeps the positional `?` contract intact: a new client-side slot at the end shifts nothing above it.
- **Only the opt-in columns get an `Enabled` flag, and `MetadataColumn.Enable` throws on the others.**
  Whether `guid_version`, `revision`, `is_deleted` and `deleted_at` exist is already decided by
  `UseOptimisticConcurrency()`, `UseNumericRevisions()` and `SoftDeleted()`, and `last_modified` is
  always there — so a second flag over any of them would be a knob that silently does nothing. Marten
  puts `Enabled` on all of them; `OptionalMetadataColumnExpression` is what keeps the DSL from
  offering it where it would mean nothing. **Mapping an optional column enables it**, since a mapping
  onto a column that would not exist is configuration that silently does nothing too.
- **Turning an enabled column back off throws.** A column is created by the migration and dropping one
  that may hold data is a migration, not a configuration flag.
- **`MetadataForAsync` is hand-built rather than routed through LINQ**, which is the second place in
  Fisher where going around the implicit filters is correct (bulk insert's duplicate probe is the
  other). The LINQ path applies the soft-delete filter, and a soft-deleted row's metadata is exactly
  what a caller asking "when was this deleted" wants — no ordinary load can answer it. The tenant term
  stays. Columns are chosen from the mapping rather than selected blindly, because naming one the
  table does not have is `no such column` rather than a null.
- **The returned type is `Fisher.Metadata.StoredDocumentMetadata`**, not `DocumentMetadata` as on both
  siblings: Fisher already has a `DocumentMetadata` one namespace away doing the opposite job (which
  columns are mapped onto which members), and two same-named types with opposite jobs is a collision
  that only gets noticed by whoever imports the wrong one. Every optional value on it is nullable,
  where Polecat's constructor requires `CreatedAt` — here null means "the column is not on this table",
  and a default `DateTimeOffset` would be indistinguishable from a real one.
- **`IDocumentSession` now declares `CorrelationId` / `CausationId` / `CurrentUserName` / `Headers` /
  `SetHeader`.** They were public on `FisherSession` and on no interface, so setting one meant casting
  to an internal type — tolerable while only events read them, wrong once a document does.

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
  than Marten's — so nothing depends on `StoreOptions.RegisterValueType<T>()`, which exists anyway
  (fisher#75) for the reason polecat#459 gives: **store configuration ought to be portable**, and a
  shared block that reads `opts.RegisterValueType<AlertId>()` on two stores should not have to drop the
  line for the third and explain the omission in a comment. **It is not an accepted no-op**, and that
  is what makes it worth having rather than merely tolerable: discovery must treat "not a wrapper" as
  the ordinary answer, since it is asked about every candidate identity member of every type, whereas
  naming a type here is an *assertion* — so the same answer becomes a configuration error, reported
  with the type named rather than surfacing later as "has no identity member" from a place that cannot
  mention the wrapper. `StrongTypedId.Register` is the throwing half of `TryResolve`, and it tells a
  bad *shape* apart from a good wrapper around an inner type Fisher cannot store. The compliance
  seam's `RegisterValueType<T>` now delegates to it rather than staying empty, so the suite exercises
  the method a consumer would call.
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
- **The host-level `JasperFxOptions` is read in `Configured`, which is one call where the siblings
  need two** (fisher#141). `JasperFxOptions.EnableAdvancedTracking` is documented as telling *all*
  Critter Stack tools to opt in and is what a CritterWatch host sets; Fisher had
  `Events.EnableExtendedProgressionTracking` and read `JasperFxOptions` **nowhere**, so the same host
  configuration lit up Marten and Polecat and silently did nothing here. Silent in the direction that
  hurts: a console showing no per-shard state is indistinguishable from a store with none to report.
  - **Every registration path already funnels through `Configured`**, primary and ancillary alike, so
    "and the ancillary stores too" holds by construction. Marten and Polecat each call their
    equivalent at two sites because their two paths are separate, and a third path added there is a
    third place to remember.
  - **After the `IConfigureFisher` chain, not before**, so a per-store instrumentation default cannot
    clobber the host's opt-in. `the_host_opt_in_outranks_a_store_level_contribution` pins it and is
    the only test that fails when the call is moved.
  - **One-way.** A false `EnableAdvancedTracking` is the default rather than a statement, so it must
    not turn *off* a store that opted in for itself — which a plain assignment would do.
  - **`JasperFxOptions.ApplicationAssemblyReuseWarning` is surfaced from `Configured` too**
    (fisher#142), where the siblings log it from an always-registered startup activator. All three of
    Fisher's hosted services are conditional — `ApplyAllDatabaseChangesOnStartup`,
    `SeedInitialDataOnStartup`, `AddAsyncDaemon` — so a plain `AddFisher` has no startup hook at all,
    and registering an unconditional one for a single rare warning would change what every consumer's
    container holds. `Configured` already runs exactly once per store with the container in hand.
    JasperFx only *detects* the condition and says plainly that consumers surface it, so a warning
    nobody prints is a warning that does not exist.
    - **Once per container**, keyed on the `JasperFxOptions` instance through a
      `ConditionalWeakTable`. Several Fisher stores share one host condition and would otherwise each
      repeat the same four-sentence warning. **A process-wide flag would be worse than the noise it
      saves**: the warning exists *because* a second host started in this process, so suppressing
      repeats globally would silence it for precisely the host that needs it.
      `a_second_host_in_the_same_process_gets_its_own_warning` is that half.
    - **The value is planted by reflection in the tests, and that is the right amount of test.** The
      setter is internal to JasperFx and reproducing the real condition needs two hosts from two
      assemblies in one process in a fixed order — an intermittent under xUnit's parallel collections
      rather than a test. What is Fisher's to get right is read it, buffer it, log it once; detecting
      it is JasperFx's and is tested there.

#### Container-scoped projections and subscriptions — fisher#194

`AddProjectionWithServices<T>(lifecycle, lifetime)` and `AddSubscriptionWithServices<T>(lifetime)`, on
`FisherConfigurationExpression`, on `FisherStoreConfigurationExpression<T>`, and as bare
`IServiceCollection` extensions for a modular monolith registering from a module rather than from the
composition root. `IFisherRegistrable` is the static-abstract dispatch, mirroring `IMartenRegistrable`
— which wrapper a projection needs depends on what *kind* of projection it is, and only its base class
knows.

**Almost none of this is Fisher's, and that is the finding rather than the shortcut.**
`JasperFx.Events.Projections.ContainerScoped` already carries `ScopedProjectionWrapper<,,>`,
`ScopedAggregationWrapper<,,,,>` / `ScopedSingleStreamAggregationWrapper<,,,,>` and the non-generic
`ScopedAggregationWrapper.Build(...)` factory, all public and all generic over the store's session
pair. So Fisher closes them over `IDocumentSession` / `IQuerySession` and writes no wrapper at all.
**Polecat has no equivalent surface**, so Fisher is the second store to have this rather than the
third.

- **Scope lifetime is the design, and the shared wrappers settle it: one `IServiceScope` per unit of
  work.** An async projection outlives every request scope by construction — it runs from a hosted
  service where there is no request — so a wrapper that resolves once and holds is reaching into a
  disposed provider by its second batch, and one that opens a scope and keeps it leaks a scope per
  registration for the life of the process. The wrappers open, resolve, use and dispose inside each
  inline `ApplyAsync`, each daemon page and each slicing pass, holding nothing across a boundary.
  That is also why `Transient` is treated as `Scoped` rather than refused: the wrapper resolves afresh
  per batch either way, so the two lifetimes describe the same behaviour.
- **The scopes come from the root provider**, which is what `IConfigureFisher` hands the callback and
  what the store is built from. A scope created from a scoped provider is a child of something that
  gets disposed.
- **Both paths land on Fisher's own `Add(ProjectionBase, lifecycle)`, where Marten's equivalent calls
  the base graph's `Add(IProjectionSource, ...)`.** That overload is what sweeps `PublishedTypes()`
  into the schema, so a container-scoped projection's document table is created with the rest of the
  migration. Through the narrower one the projection would work against a table that was never
  migrated — the silent half of fisher#111, reached from a new direction.
- **The subscription wrapper is Fisher's, and that is a gap rather than a choice.**
  `JasperFx.Events.Subscriptions.ScopedSubscriptionServiceWrapper` exists, is generic over the session
  pair, and is `internal` with nothing in the library referencing it — so no consumer can reach it, and
  Marten carries its own copy for the same reason. `Subscriptions/ScopedSubscriptionWrapper.cs` is
  Fisher's, and its constructor copies the inner subscription's `Name`, `Version`, `Options` and event
  filtering across, because it is the *wrapper* the daemon reads those from. Dropping them is how a
  scoped subscription silently loses a batch size or a `SubscribeFromPresent()` its constructor set —
  marten#4318, found the hard way over there.
- **A `Singleton` registration is the projection itself, with no wrapper**, which is correct whenever
  its dependencies are singletons too. A bare `IProjection` is wrapped in `ProjectionWrapper` *here*
  rather than at the graph, so the caller's `configure` lambda can reach the name and filtering surface
  a raw `IProjection` does not have.
- **The load-bearing tests count scopes, not results.** A projection that merely works passes every
  correctness assertion written against a single commit and then fails on the second daemon batch, so
  `a_scoped_projection_gets_one_scope_per_unit_of_work` counts creations *and* disposals — a wrapper
  that opened a scope per batch and kept it passes the first half and leaks — and
  `a_scoped_projection_runs_under_the_async_daemon` deliberately commits a second batch after the
  daemon has already run one.

#### The command-line seam — fisher#172

`AddFisher` registered the store, sessions and its hosted services, and **nothing the JasperFx or
Weasel command line looks for** — so `dotnet run -- db-apply` failed with *"No Weasel databases were
registered in this application"*, `resources list` reported an application with no resources, and a
CI step asserting the deployed schema still matches the code was not expressible at all.

**Two registrations, because the two command families resolve different things**, and registering one
leaves the other reporting an empty application:

| Surface | Resolves | Fisher supplies |
|---|---|---|
| `resources setup/list/check`, `AddResourceSetupOnStartup()`, `describe` | `JasperFx.CommandLine.Descriptions.ISystemPart` | `FisherSystemPart` / `FisherSystemPart<T>` |
| `db-apply` / `db-assert` / `db-patch` / `db-dump` | `Weasel.Core.Migrations.IDatabaseSource` | `FisherDatabaseSource` |

- **An adapter rather than widening `ITenancy`**, following polecat#501's reasoning. Marten satisfies
  the Weasel half because its own `ITenancy` *is* an `IDatabaseSource`; Fisher's `ITenancy` is public
  and implementable outside this repo, so extending it is a breaking change — and it would pull a
  migration contract into a tenancy abstraction for what is purely a CLI concern. Nothing was needed
  underneath: `FisherDatabase` already extends Weasel's `SqliteDatabase`.
- **Both take a factory, never the resolved store.** The `IConfigureFisher` chain has to have run
  before the tenancy means anything, and that happens on first `IDocumentStore` resolution — so
  injecting the store would build it while the container is still being assembled.
- **Both refresh a `DynamicTenancy` first.** A tenant nothing has resolved yet still has a file to
  migrate, and `db-apply` silently skipping it is the exact failure the whole seam closes. Read
  through `ITenancy` rather than the store's internal `RefreshTenantsAsync`, because an ancillary
  store arrives as its marker `DispatchProxy` and is not a `DocumentStore`.
- **An ancillary store gets its own subject uri** (`fisher://iotherstore`), and that matters more here
  than on either sibling: a second Fisher store is usually a second *file*, so collapsing the two
  would hide one from `resources list` outright rather than merely mislabelling a schema.
- **Neither ancillary registration unwraps the proxy**, unlike the `IEventStore` bridge beside them.
  Correct rather than an oversight: both reach the store through `IDocumentStore` members (`Tenancy`,
  `Options`), which a marker interface inherits and the proxy therefore implements — where the tooling
  interfaces are implemented explicitly and are not on `IDocumentStore` at all.

**`AssertDatabaseMatchesConfigurationOnStartup()`** is the third opt-in, beside
`ApplyAllDatabaseChangesOnStartup()` and `SeedInitialDataOnStartup()`, over a new
`IDocumentStore.AssertDatabaseMatchesConfigurationAsync` that spans every tenant database.

- **The two schema opt-ins are refused together, in either order.** Applying the changes at startup
  makes the assertion a check on the schema that same startup just wrote, so a host asking for both is
  expressing a contradiction rather than being careful — and accepting it silently would leave
  somebody believing they had a guard they do not have.
- **`AutoCreate.None` is deliberately not consulted**, where the migration activator honours it. That
  setting says the schema is not Fisher's to change and this changes nothing — declining to *verify*
  because the store was told not to write would make the strictest configuration the one with the
  fewest guarantees.
- **A seeder may follow either activator.** What `SeedInitialDataOnStartup` needs is that the tables
  exist by the time it runs, and an assertion that passed is exactly that claim.

**The tests run the commands rather than asserting on the container**, and that is the point: a
registration test passes against a source that resolves, enumerates nothing and reports success —
which is `db-assert` answering "everything matches" about a store it never looked at. They needed a
`ConsoleWritingCollection`, because `Console.SetOut` is process-wide and `CliJsonCapture` was already
swapping it; two tests printing JSON objects at once made the existing `event_query_command` capture
parse somebody else's report. Same family as the process-wide `ActivityListener` lesson in tracing.

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
  concrete type — is caught too. **Both had to learn about the document contract below**: the store's
  session-factory members are satisfied by default interface implementations on `IDocumentStore`
  itself, and a DIM that explicitly implements a base interface member is a *private* method on the
  interface — so the second test's filter is `IsPublic` on the **interface** method, separating a
  promise from a forwarder. `the_store_is_the_shared_document_session_factory` pins the forwarders
  directly, since they are excluded from the map walk and would otherwise be pinned by nothing.

### The store-agnostic document contract

`JasperFx.Events.Documents` (fisher#68 / jasperfx#647), shipped in JasperFx 2.47.0 — the document
slice a store-agnostic consumer needs alongside the event store, and the first half of what
`Wolverine.Fisher` and a Fisher-backed CritterWatch are built on. Three session tiers, a session
factory and a query-execution hook; seven operations at 2.47.0 and **eight from 2.49.0**, measured off
CritterWatch's actual call sites rather than designed as a document-store facade.

#### `Events` on the session contracts — the non-covariance trap

jasperfx#669 (JasperFx 2.50.0) added an `Events` accessor to both document session tiers:
`IQueryEventStore Events` on `IDocumentReadOperations`, narrowed to `IEventStoreOperations Events` on
`IDocumentSessionOperations`. It is the store-agnostic route from a session a consumer opened through
`IDocumentSessionFactory` to that session's event store.

⚠️ **Both carry a throwing default, and Fisher's pre-existing `Events` did NOT satisfy either.** C#
interface implementation is not return-type covariant: `IQuerySession` and `IDocumentSession` both
declare `Events` as Fisher's concrete `EventOperations`, which *implements* `IEventStoreOperations` but
does not *implement the member*. The near-miss binds to the throwing default with **no compile error
anywhere**, and nothing notices until a caller holds the session as the contract — which is the only
caller the accessor exists for. Two explicit implementations on `FisherSession` close it, and
`document_session_events_compliance` is what proves them closed. Deleting them still compiles.

⚠️ **A second, louder consequence:** `IDocumentSession` now reaches an `Events` down two unrelated
branches — `IQuerySession`'s and `IDocumentSessionOperations`' — and neither hides the other, so every
`session.Events` in the tree became CS0229 ambiguous on the bump. `IDocumentSession` re-declares
`new EventOperations Events { get; }` to resolve the lookup. That declaration looks redundant and is
not.

⚠️ **The compliance suite appends by string stream key throughout**, and Fisher's default — like every
sibling's — is Guid, which refuses every append in the suite by name. The suite now *says* so:
`DocumentComplianceConfig.StreamIdentity` (jasperfx#672 / fisher#98) is nullable, defaults to null
meaning "leave the store on its own default", and `FisherDocumentComplianceFixture` replays it exactly
as it replays `ValueTypes`. **What that replaced was an inference made in the fixture** — string
identity whenever the config declared *event types* — which was right only because
`DocumentSessionEventsCompliance` was the one suite populating `EventTypes`, and would have silently
mis-configured the first Guid-keyed event suite to arrive. A precondition a config cannot carry is one
each fixture has to guess, and a correct store then fails an undeclared requirement.

#### `PendingStreams` — the same trap one member over

jasperfx#673 (JasperFx 2.51.0) added `IReadOnlyList<StreamAction> PendingStreams` to
`IDocumentSessionOperations`: the stream actions a session has queued and not yet committed, for code
that did **not** do the appending — a listener, or a pre-commit hook deciding something from what is
about to be written. Code at the call site already has the `StreamAction`, because `StartStream` and
`Append` return it. fisher#96 is Fisher's half, and it is an explicit implementation on `FisherSession`
beside the two `Events` ones.

- **Fisher is the store most exposed to the non-covariance trap here**, because it already has a member
  *named* `PendingStreams` — on `EventOperations`, returning `IReadOnlyCollection<StreamAction>` over
  `_streams.Values`. That is not the contract's `IReadOnlyList<StreamAction>`, so had that shape ever
  been put on the session it would have bound to the throwing default with no compile error anywhere.
  The default **throws rather than answering empty**, deliberately: empty is indistinguishable from a
  session with nothing pending, so a silent default would discard a consumer's derived work with a
  clean build and green tests.
- **The forward copies, and both reasons are worth keeping.** The types differ, so a copy is forced;
  and the native collection is a *live* view of the tracking dictionary, so a caller holding it across
  another append watches it change. The contract permits either and tells a caller wanting stability to
  copy — doing it here is what makes every caller's answer stable rather than the ones who read that
  remark.
- **Tenant scopes are included**, where `EventOperations.PendingStreams` is per-scope by construction.
  `SaveChangesAsync` commits the scopes' streams in the same transaction and `ChangeSet` already
  reports the two together; a hook told "this is what is about to be written" and handed only the
  default tenant's would be wrong about the commit it is bracketing. Each action carries its own
  `TenantId`, and a scope holds no scopes of its own, so reading it from one reports that tenant alone.
- **Not added to Fisher's own `IDocumentSession`.** `session.Events.PendingStreams` is the native
  spelling and predates the contract; a second public member of a different type saying the same thing
  is how the two would drift.

`pending_stream_actions_compliance` (9 tests) is the definition; `pending_stream_actions` covers the
two decisions above, which a suite written against three stores has no vocabulary for.

#### `LoadAsync<T>(object)` — the eighth operation

jasperfx#665 added `Task<T?> LoadAsync<T>(object id, …)` to `IDocumentReadOperations`, and fisher#89
is Fisher's half. **It is not a compile break**: the member ships with a default implementation that
unboxes a `Guid` or a `string`, forwards to the existing overload, and throws `NotSupportedException`
for anything else — so the bump is clean on its own and what tells you the member is only half-present
is the compliance suite, not the compiler.

- **`LoadAsync<T, TId>` does not satisfy it**, different arity, so the contract falls to the default.
  Fisher carries both. The two-parameter spelling keeps its own reason for existing — both type
  parameters explicit is what keeps it unambiguous against the four single-parameter overloads.
- **Declared on `IQuerySession` as public API rather than implemented explicitly**, matching Marten and
  Polecat, so a consumer moving between the stores meets one spelling. The four canonical overloads are
  more specific and still win overload resolution, so no existing call site moved.
- **The trap is the canonical case, not the strong-typed one.** The overload is reached by a caller
  holding *any* identity in an `object`-typed local, so an implementation that assumed a wrapper passes
  the two strong-typed facts and silently regresses the boxed-`Guid` one.
  `the_object_overload_resolves_canonical_identities_too` exists for exactly that, and the default gets
  it right for free — which is what makes an override the only way to break it.
- **`CoerceIdentity` is where the three shapes are decided**: exact match, wrapper-from-inner (through
  `StrongTypedId`, so it agrees with how the identity was discovered in the first place), and integral
  widening. The last is narrow on purpose — an untyped literal is an `int`, so refusing it against a
  `long`-keyed document would refuse a difference the caller cannot see, while a general
  `Convert.ChangeType` would turn `"12"` into an id and hide a real mistake.
- **The wrapper half is what let fisher#88's gate widen from `IdType` to `StoredIdType`**, closing the
  `FetchLatest` phantom for strong-typed aggregates too. Verified by narrowing it back: the strong-typed
  phantom test fails.
- **`FisherDocumentComplianceFixture` replays `config.ValueTypes` before the mappings.** Fisher
  discovers wrappers by itself, so it is an assertion rather than a prerequisite — but a fixture that
  would break under a store needing it is not worth writing.

One thing this surfaced and did **not** change: the typed overloads still throw a raw
`InvalidCastException` for a mismatched identity — `LoadAsync<Order>("12")` against a `long`-keyed type
binds to `LoadAsync<T>(string)` and casts storage. Pre-existing, out of fisher#89's scope, and noted in
`an_identity_of_the_wrong_type_is_refused_by_name` so the next reader does not mistake it for this
member's behaviour.

**Fisher's own types *are* the contracts — there is no adapter, and that is the result rather than a
convenience.** The binding is four interface declarations and one partial class:

| Contract | Fisher |
|---|---|
| `IDocumentReadOperations` | `IQuerySession` |
| `IDocumentWriteOperations` | `IDocumentOperations` |
| `IDocumentSessionOperations` | `IDocumentSession` |
| `IDocumentSessionFactory<TOperations, TQuerySession>` | `IDocumentStore` |
| `IDocumentQueryExecutor` | `FisherQueryProvider` |

- **The three-tier split lands exactly where fisher#33 already drew it.** The contract separates
  enlisting from committing because a projection writes and must never commit; `IDocumentOperations`
  was split out of `IDocumentSession` for tenant scopes, which cannot commit either. Same line, two
  reasons, so the middle tier was accepted with no reshaping — and `ITenantOperations` becomes an
  `IDocumentWriteOperations` for free.
- **The one non-mechanical part was a constraint widening**, as it was on Polecat. The contract is
  `where T : notnull` and Fisher's by-identity document read surface — `LoadAsync`, `LoadManyAsync`,
  `CheckExistsAsync`, `LoadJsonAsync` — was `where T : class`, which is strictly narrower and so cannot
  implement it. Widening it *removed* an inconsistency rather than creating one: `Store`, `Delete`,
  `DeleteWhere` and `Query<T>` were already `notnull`, so only the read half disagreed.
- **`Query<T>()` binds implicitly, where both siblings need a default interface implementation to
  forward it.** Marten returns `IMartenQueryable<T>` and Polecat `IPolecatQueryable<T>`; C# has no
  return-type covariance for interface implementation, so each has to declare theirs `new` and forward.
  Fisher already returned a plain `IQueryable<T>`. The one place this binding is cheaper here.
- **`IDocumentQueryExecutor`'s `T` is unconstrained, so the four provider methods behind Fisher's
  terminals were widened to match.** Nothing downstream wanted the constraint — `ISelector<T>` is
  unconstrained and the projected path casts through `object?` — so `notnull` there was a claim being
  made rather than a requirement being met, and dropping it cascaded to nothing. Fisher's own public
  terminals keep it, where it is a useful signal to a caller. Each of the four **reads
  `queryable.Expression`**, never a captured one, because the shared extensions compose
  `Queryable.Where` onto the queryable before dispatching — that is how the predicate overloads come
  free, and reading a captured expression would silently drop the predicate.
- **`IDocumentSessionFactory` goes on `IDocumentStore` rather than being implemented explicitly**,
  which is the one shared contract that does, and the distinction from the tooling surfaces is the
  point: those describe a monitoring console's view of the store, this describes opening a session,
  which is already the store's own API. It is also what makes an **ancillary** store work with no
  second mechanism — `AddFisherStore<T>` constrains its marker to `IDocumentStore`, so the marker
  inherits the factory and the `DispatchProxy` implements it. Marten and Polecat both declare it in
  this position; had the three each picked their own, a store-agnostic consumer could not resolve an
  ancillary store portably, which is the failure mode fisher#68 names.
- **Neither of Fisher's factories is genuinely parameterless** — both take an optional tenant id — so
  all three factory members are forwarded by DIM on the interface rather than by three one-liners on
  `DocumentStore` and three more on `SecondaryStoreProxy`. The tenant argument defaults to null, which
  is the right reading of a contract with no tenant parameter: JasperFx left tenant-scoped opening out
  deliberately and can add it additively.
- **No DI registration was needed or added**, matching both siblings. `IDocumentStore` is already
  registered and *is* the factory.

Four compliance suites, 45 tests, enrolled in `fisher_event_store_compliance.cs` and green on the
first run. `FisherDocumentComplianceFixture` is three members wide and **deliberately not generic over
Fisher's session pair**, unlike the event fixture: everything the document suites do runs through the
shared contracts, so `Sessions` is typed as the bare `IDocumentSessionFactory` and a suite reaching
past it would not compile. Two Fisher-specific notes:

- **Document types are registered up front, and on SQLite that is required rather than tidy.** Fisher
  creates a document table at first write and SQLite resolves a table name when it *prepares* a
  statement, so `query_over_an_empty_document_type_returns_an_empty_list` and the `AnyAsync` over an
  untouched type would fail with `no such table` rather than answering empty.
  `DocumentComplianceConfig.DocumentTypes` exists for exactly this. Same lesson rebuild teardown,
  `CleanAsync<T>` and the document diagnostics each had to learn.
- **Fisher needs no shared xUnit collection, where Marten and Polecat both do.** All four suites pin
  `SchemaName` to `compliance_documents` and the base suite wipes document data before every test, so
  against one server one class's wipe lands in the middle of another's test. A throwaway SQLite file
  per fixture instance — one per test — leaves the wipe nothing else to reach.

**Half two of fisher#68 is `Wolverine.Fisher` and is not actionable from this repository**, the same
place Polecat's issue landed: the integration is built in the wolverine repo against wolverine#3907.
Two SQLite constraints carry into its design and are worth restating there — Wolverine's durability
tables must commit through `ITransactionParticipant` on Fisher's own connection rather than through a
parallel connection to the same file (a second connection presents as a *hang*, not an error), and
leader election across a cluster is not viable on one file, so hosting is solo/embedded.

### Transaction participants

`ITransactionParticipant` and `IDocumentSession.AddTransactionParticipant` (fisher#50, half one) —
something else writing on Fisher's connection, inside Fisher's transaction, committed with it.

**More important on SQLite than on either sibling, and structurally so.** One writer per database
file. An application using Fisher for its events and something else — EF Core, Dapper, hand-written
ADO.NET — for its relational tables, in the same file, which is the natural thing to do with an
embedded database, cannot write both atomically without this. Worse, it cannot write both *at all*
without contending against itself: the two transactions are two writers on one file, and one waits or
fails with `SQLITE_BUSY`. On PostgreSQL the equivalent is a nicety.

**`Fisher.ITransactionParticipant` is a one-line alias now**, deriving from
`Weasel.Storage.ITransactionParticipant<SqliteConnection, SqliteTransaction>` (weasel#561) and
declaring no members of its own. **Adopting it is a simplification rather than a loss, and the reason
is that the shared contract is Fisher's**: `AfterCommitAsync`'s default — the "reconcile whatever
`BeforeCommitAsync` left pending, now that it is durable" half that the two-attempt retry rule creates
— was Fisher's member, and the lift took it upstream as the contract for all three stores. So the
retry rule and its answer are documented once, in Weasel, and Fisher's file carries only the two facts
the shared remarks cannot: `BeforeCommitAsync` occupies the position fisher#4 pinned, and
`AfterCommitAsync` does not fire at all for an enlisted session.

What contravariance does and does not give is worth stating, because the shared interface is
contravariant in both parameters and that reads like more than it is. A participant written against
the base `DbConnection`/`DbTransaction` pair *converts to* the closed shape; it still has to **declare**
`Fisher.ITransactionParticipant` to be handed to `AddTransactionParticipant`. Porting a participant
between the three stores is therefore a change to its base declaration and nothing else, which is what
the shared contract's own remarks promise.

- **The inverse of enlistment, and both are worth having.** `SessionOptions.ForTransaction` lets a
  caller hand Fisher a transaction they own; this lets a participant join one Fisher owns. Which fits
  depends on the participant — a component whose "save" is a method call rather than a connection to
  borrow (`DbContext.SaveChangesAsync`) is far easier this way round.
- **A participant must write on the connection it is handed, not merely to the same file.** Two
  connections to one file are two writers, and the second blocks on the first *from inside the first's
  transaction* — a genuine self-deadlock that presents as a hang rather than an error. That is the
  single most likely way to build one wrong, and it is why the connection is a parameter rather than
  something the participant is expected to find.
- **The hook is the last thing inside the transaction**, the position `IMessageBatch.BeforeCommitAsync`
  already occupies — so its visibility semantics are the ones fisher#4 pinned, and
  `a_participants_write_is_invisible_until_the_commit` re-probes them over a separate connection.
- **Both commit paths invoke participants**, `FisherSession.SaveChangesAsync` and
  `FisherProjectionBatch.ExecuteAsync` alike, so a projection or subscription that enlists one does not
  have to know which it is running under. The batch gathers them *before* the resilience pipeline for
  fisher#12's reason — everything the delegate consumes has to survive being read twice.
- **`BeforeCommitAsync` can be called more than once for one unit of work, and that was a real silent
  bug.** A retried `SQLITE_BUSY` re-executes the whole write delegate. EF Core's `SaveChangesAsync`
  accepts its changes when *its own command* succeeds, not when Fisher commits — probed directly: an
  entity goes `Added` → `Unchanged` at the save and stays `Unchanged` through a rollback of the
  enclosing transaction. So attempt two found a `DbContext` that believed it had already saved, wrote
  nothing, and let Fisher commit without EF's rows, invisibly, because Fisher's own work committed
  either way. The factory form was safe by construction (a retry runs the factory again, so the
  caller's lambda rebuilds and re-adds); only an already-built context was exposed, and it now saves
  with `acceptAllChangesOnSuccess: false`.
- **`AfterCommitAsync` is the other half of that, and it is *not* a post-commit side-effect hook.**
  `IDocumentSessionListener` (fisher#32) is still the seam for those. This one exists for the narrower
  job the retry rule creates: a participant holding its writes replayable across attempts needs one
  place to stop, and only Fisher knows when the commit happened. Default-implemented, so a participant
  that does not need it declares one member as before. Runs outside the resilience pipeline in both
  paths, and **not at all for an enlisted session** — where the commit is the caller's and there is no
  retry either, so there is nothing to reconcile until they commit.
- A participant added through a tenant scope lands on the parent, for the same reason its boundaries
  and metadata do: there is one transaction.

### Runtime tenants

`Storage/ITenantSource.cs` and `DynamicTenancy` (fisher#58) — `MultiTenantedDatabasesInDirectory(...)`
or `MultiTenantedDatabasesFrom(source)`, so the set of tenants is *asked for* rather than declared.
`SeparateDatabaseTenancy` (fisher#47) stays for a fixed set; the two are alternatives.

**Viable here in a way it is not on either sibling.** Provisioning a tenant is a file plus a
migration, cheap enough to do on first use — which is what makes "a tenant appears without a restart"
a reasonable offer rather than an operational event. On PostgreSQL or SQL Server the same act is a
`CREATE DATABASE`, which is why Marten's and Polecat's equivalents lean on a master table an operator
populates deliberately.

- **The first-use migration hangs off `OpenConnectionAsync`, not off tenant resolution, and that is
  forced.** `ITenancy.DatabaseFor` is reached from `OpenSession`, which is synchronous and has no
  `await` to offer; a migration is asynchronous. Opening a connection is the first genuinely
  asynchronous thing that happens to a new tenant's file, and it happens before any statement can run
  against it. Blocking inside `DatabaseFor` would have been sync-over-async on the session path.
  Re-entrancy is guarded with a `[ThreadStatic]`, because the migration opens connections of its own;
  the semaphore beside it is for two callers racing, which is a different question. **The result is
  not cached until it succeeds** — a transient failure remembered as done would leave the tenant
  permanently unusable with nothing to say why.
- **`ITenantSource.TryFind` is synchronous and `AllAsync` is not**, for the same reason. The hot path
  has to answer without I/O, which the directory convention manages trivially (a tenant id maps to a
  path); enumerating every tenant is a startup and daemon concern where an `await` is available.
- **`DirectoryTenantSource` resolves *any* tenant id**, whether its file exists yet or not, which is
  what makes a new tenant work with no registration step. Enumeration reports only the files that are
  there. `InMemoryTenantSource` is the opposite — it refuses what it was not told about, which is the
  difference an application pushing its own tenants table wants.
- **A source has to answer for the default tenant**, read while the store is being built rather than
  lazily, so a source that cannot says so at construction instead of at the first store-level
  operation. There is no store-level file under this tenancy for it to fall back on.
- **Suspension, never deletion**, and this is the decision fisher#58 asked to be made rather than
  defaulted. Deleting a tenant here means deleting a file — the cheapest deprovisioning of any Critter
  Stack store, and the most irreversible — and Fisher cannot know whether that file is backed up. So
  the API suspends or forgets and an operator removes the file themselves.
  **`DisabledTenantException` is distinct from `UnknownTenantException`** because "switched off" and
  "never heard of it" are different operational situations and an application handling one should not
  have to guess which it got.
- **The daemon polls for new tenants**, at `FisherDaemonHostedService.TenantPollingInterval` (one
  minute), and only under `DynamicMultiple`. Polling rather than notification because the set of
  tenants belongs to the application and Fisher is never pushed to. A new tenant's *sessions* work
  immediately either way — resolution does not go through the hosted service. Daemons are keyed by
  database identifier, since a second daemon over one file is two writers contending for one write
  lock.
- **Databases are cached and never evicted, and the measurement says that is right** (fisher#59). A
  tenant resolved but never used costs no measurable memory and no file handles at all, so there is
  nothing for eviction to reclaim. What costs is a tenant that has been *used* — see "Releasing pooled
  connections" below. `ForgetTenantAsync` is the explicit release for a tenant a process is finished
  with.

### Releasing pooled connections

`FisherDatabase.ReleasePooledConnections` and `DynamicTenancy.ForgetTenantAsync` (fisher#59).

**The issue was filed about caching `FisherDatabase` objects, and measuring said that was the wrong
thing to worry about.** 200 tenant databases resolved but never used cost no measurable memory and
opened no files — a `SqliteDataSource` is a *factory*, not a pool, building a fresh `SqliteConnection`
on every open. What costs is a tenant that has been **used**: Microsoft.Data.Sqlite keeps a pooled
connection per connection string, worth three file handles (`.db`, `-wal`, `-shm`), in a
**process-wide** registry that nothing Fisher disposed ever touched. 50 used tenants held 3.4 handles
apiece and still held them after the store was disposed.

So the fix is not eviction, and **it was leaking for every store rather than only a multi-tenant
one**: disposing a Fisher store left its pooled connections behind.

- **This is `SqliteConnection.ClearPool(connection)`, not `ClearAllPools()`, and the distinction is
  the whole reason it is safe.** The banned one disposes every pooled connection in the process, so
  one store's cleanup takes out another's — which is why the conventions forbid it and why
  `TemporaryDatabase.Dispose` already uses the targeted form. This one names a connection string and
  touches only that pool.
- **A connection currently checked out is unharmed.** Verified against Microsoft.Data.Sqlite 10.0.9:
  it goes on reading and writing after its pool is cleared, and is discarded rather than re-pooled
  when it closes. That is what makes forgetting a tenant safe while a session is mid-request, and it
  is the property whose absence would have made this an `ObjectDisposedException` generator.
- **Eviction on idleness is deliberately absent.** A timer cannot tell a tenant that is finished from
  one that is merely quiet, re-resolving one is nearly free, and the thing it would reclaim costs
  nothing until the tenant is used. `ForgetTenantAsync` leaves the judgement with the caller who
  actually knows. Nothing breaks if it is never called.
- **Two stores over one file share the pool**, so disposing one releases the other's *idle*
  connections. Harmless — they reopen on demand — and pinned, because it is the bounded version of the
  thing `ClearAllPools` is forbidden for.
- **Tested through SQLite's own `-wal` and `-shm` sidecars rather than by counting file descriptors.**
  SQLite deletes them when the last connection closes, so their presence is an exact, *file-local*
  statement about whether anything still holds the database open. A `/dev/fd` count answers the same
  question process-wide, which under xUnit's parallel collections is the intermittent tracing already
  learned to avoid.

### Database-per-tenant (stage 1)

`Storage/ITenancy.cs` (fisher#47) — `DefaultTenancy`, `SeparateDatabaseTenancy`, and
`StoreOptions.MultiTenantedDatabases(...)`. **Conjoined tenancy stays and stays the default**; the two
are alternatives, as on both siblings.

**Arguably SQLite's best tenancy story rather than its worst.** The usual objection —
database-per-tenant is heavyweight to provision — inverts here: a tenant is a *file*. Creating one is
a file plus a migration, deleting one is deleting a file, backing one up is copying it, and one
tenant's data cannot leak into another's because there is no shared table to leak through. And it
answers the sharpest structural constraint: under conjoined tenancy every tenant contends for one
write lock, where under file-per-tenant they write concurrently. That makes it a **performance**
feature as much as an isolation one, which is not true on either sibling —
`two_tenants_write_at_the_same_time` pins it by holding one tenant's write lock across the other's
whole commit.

- **The session path is the whole of what changed.** `OpenSession` resolves its database from
  `Tenancy.DatabaseFor(options.TenantId)`; under `DefaultTenancy` every tenant resolves the one
  database, so nothing moves for a store that did not ask for this. `FisherDatabase` already had an
  internal constructor taking a connection string, which is the seam this needed.
- **An unknown tenant throws rather than falling back to the default.** Falling back would write one
  tenant's data into another's file — the one failure this tenancy exists to make impossible, and it
  would be silent.
- **Migration is per database with per-database status.** `ApplyAllConfiguredChangesToDatabaseAsync`
  over a hundred tenants that fails at the fortieth leaves mixed versions whatever it throws;
  `TenantMigrationException` reports *which* are current. Run sequentially, not in parallel: each
  migration takes its own file's write lock, so parallelism wins nothing on the DDL and holds N
  connections against a pool ceiling that sizes one file.
- **Both file-naming shapes, with the convention as the default.** `InDirectory(...)` + `AddTenants(...)`
  is what makes a hundred tenants one line and is what stage 3 will need, since a tenant that appears
  at runtime has no configuration line to carry a path; `AddTenant(id, connectionString)` is the
  flexible form.
- **`ATTACH` stays out of it.** It would let one connection see several tenants, but an attachment has
  per-connection lifecycle to re-establish on every pooled checkout — exactly what `FisherTableNaming`
  exists to avoid.
- **WAL, busy timeout and foreign keys are per file for free**, because `SqliteDataSource` applies the
  PRAGMAs per connection. The cost is that `MaxPoolSize` sizes *each tenant's* pool.
- **The daemon runs one instance per tenant database** (fisher#57). `BuildProjectionDaemonAsync(tenantId)`
  builds one; `BuildProjectionDaemonsAsync()` builds them all, and is what `AddAsyncDaemon` hosts —
  the single-daemon overload with no argument projects the default file and says nothing about the
  others. **N daemons over N files do not contend**, which is the same property that makes this
  tenancy a performance feature; under conjoined tenancy the same N projections queue behind one write
  lock. Runtime tenants are **fisher#58**.
- **Shard names did not have to become (projection, tenant)**, which fisher#57 expected they would.
  `fi_event_progression` lives in each tenant's own file, so every database already has its own
  high-water mark and its own progress row per shard — two tenants running one projection are two
  daemons writing the same shard name to two different tables. A second key would have drawn a
  distinction the file boundary already draws.
- **Cleaning spans every database.** `ResetAllDataAsync` and the whole `IDocumentCleaner` surface loop
  `Tenancy.AllDatabases()`; cleaning only the default would leave every other tenant's data behind
  while reporting success, and the caller most likely to hit that is a test fixture.
- `StoreOptions.ConnectionString` becomes optional under this tenancy, because there is no store-level
  file — a store-level connection string would be a database nothing writes to.

### Multi-store registration

`AddFisherStore<T>(...)`, `IConfigureFisher` and `FisherStoreRegistry` (fisher#46).

**Several stores are a *better* fit here than on either sibling.** On PostgreSQL or SQL Server a second
store usually means a second schema in one database and the isolation is the server's. On SQLite a
second store can simply be a second **file** — separately backed up, separately deletable, and with its
own write lock. That last point is the one that matters: one writer per file is SQLite's central
constraint, so splitting a workload across two files is the primary way to get two concurrent writers,
and this is the ergonomic front door to it. Both shapes already worked at the storage layer; only the
registration surface was missing.

- **The marker proxy is `System.Reflection.DispatchProxy`** — in the BCL, so no proxy library and no
  code generation. A marker interface is empty apart from what it inherits, so every call it can
  receive is one the wrapped store already implements. `TargetInvocationException` is unwrapped with
  `ExceptionDispatchInfo`, or every exception from a secondary store arrives wrapped and the proxy
  leaks into the application's catch blocks. **The proxy class cannot be `sealed`** — `DispatchProxy`
  derives from it at runtime and says so.
- **A proxy is not an `IEventStore`.** `DispatchProxy` implements the interfaces it was asked for and no
  others, and the tooling surfaces are implemented explicitly and deliberately absent from
  `IDocumentStore` (fisher#45) — so the `IEventStore` registration reaches *through* the proxy to the
  real store, or a secondary store is invisible to a monitoring console.
- **A secondary store's sessions are reached through the store, not injected.** `IDocumentSession`
  cannot be registered scoped for two stores at once; Polecat answers this the same way, and keyed
  registrations would give Fisher a shape neither sibling has for a case a property access reads
  perfectly well. Pinned, so it is a convention rather than an accident.
- **Two stores registered over one file with the same `DatabaseSchemaName` are refused.** They would
  share every table — each reading, writing and cleaning the other's rows — and it is silent. The
  registry is **scoped to the container, not the process**, deliberately: building two `DocumentStore`s
  over one file by hand is something tests and migrations legitimately do, and
  `applying_the_configuration_again_is_a_no_op` is exactly that. What is refused is *registering* two.
- **`IConfigureFisher<T>` exists because which store a contribution is about has to be sayable.** An
  untargeted contribution reaches the primary store only; without the distinction, a library's
  configuration would reach stores it has never heard of.
- **`ConfigureFisher(...)` is the lambda form, and it is what makes the three stores' integration code
  read alike** (fisher#70, over JasperFx/wolverine#3907). Four overloads mirroring
  `PolecatStoreServiceCollectionExtensions`: a primary and a targeted pair, each with and without the
  container. Wolverine's ancillary integration uses exactly this to layer its own `StoreOptions`
  contributions onto a store somebody else registered.
- **Both registration styles are swept, and the second silently did nothing.** Fisher resolves
  `IConfigureFisher` and filters by the contribution's own interfaces; Marten and Polecat resolve the
  closed `IConfigure*<T>` directly, so code ported from either registers against `IConfigureFisher<T>`
  — which `GetServices<IConfigureFisher>()` does not return, because the container matches on the
  service type a registration *named* rather than on what it implements. A contribution that compiles,
  registers and never runs. `Configured` now sweeps both and deduplicates **by reference**, which is
  what lets `ConfigureFisher<T>` register its lambda against both service types and still configure
  once. `a_contribution_registered_against_the_closed_interface_is_applied` and
  `a_targeted_lambda_configures_once` pin the two halves, each verified by reverting it.
- `StoreName` defaults to the marker's name, so two stores are distinguishable in a monitoring tool and
  in a trace with nothing said.

### Tracing

`Internal/FisherTracing.cs` (fisher#48) — an `ActivitySource` named `Fisher`, spans around
`SaveChangesAsync`, a LINQ execution and a document load, and a retry event on the enclosing span.

**The instinct is that tracing is for network calls and an embedded store has none. That is backwards
for what operators actually hit.** SQLite serialises writers per file, so the interesting question
about a slow Fisher call is almost always how long it waited for the write lock — and a request that
spent its time queued behind another writer is otherwise indistinguishable from one that was simply
slow.

- **Instrumented inside `FisherSession`, not through a decorator.** Polecat's
  `TracingSessionDecorator` means re-implementing every member of `IDocumentSession` as a pass-through
  — a cost that grows with every feature added to the interface, and one that interacts badly with the
  daemon queueing onto the concrete session type.
- **The retry event is the point, and it lives in the resilience pipeline's `OnRetry`.** Recorded
  against `Activity.Current` rather than a captured span, because the pipeline is shared by every path
  that executes SQL — a session's commit, the daemon's batch, the Hi-Lo advance. It is an *event* on
  the enclosing span rather than a span of its own: a retry is the same operation happening again.
- **A query's span covers building and executing the statement, not materializing the rows.** Every
  terminal reads rows after `ExecuteReaderAsync` returns, so covering materialization would mean a
  span per terminal — five copies of three lines, one of which would eventually be forgotten — and the
  question the span exists to answer is entirely inside the boundary drawn.
- **A failed commit marks its span `Error`.** Otherwise a trace shows a commit that never happened as
  a successful one, and the retry events under it read as noise rather than as the story.
- `SaveChangesAsync`'s counts are tagged *after* everything that can add to the unit of work has run —
  listeners, inline projections — so they describe what was written rather than what had been asked
  for when the span opened.

Two things learned while writing the tests, both worth keeping:

- **A contended write does not reach the retry the way you would expect.** The wait at
  `BEGIN IMMEDIATE` comes from the connection string's `Default Timeout` and from nowhere else — not
  `SessionOptions.Timeout`, which bounds a command, and not `PRAGMA busy_timeout`, which does not cover
  it. And `Default Timeout=0` means *no limit*, not "do not wait". So a contended save either sits for
  the full wait and succeeds with no retry, or fails while the connection is still being opened, since
  opening one applies the PRAGMAs and `journal_mode` wants the write lock. `a_busy_retry_is_recorded_on_the_span_it_contended`
  therefore drives the store's own pipeline with a planted `SQLITE_BUSY` and says so.
- **An `ActivityListener` is process-wide, and xUnit runs collections in parallel.** A test that
  asserts `Single(...)` over recorded spans is green alone and red in the full suite. Filter by a tag
  the test's own store sets.

### The document-side tooling surface

`IDocumentStoreUsageSource`, `IDocumentStoreDiagnostics` and projection step-through (fisher#44) —
three partials on `DocumentStore`, all implemented **explicitly** like the event-side ones.

Before this, Fisher answered the event half of every question a monitoring console asks and none of
the document half — which renders as "no documents" rather than "this store does not answer that".
Same outcome the standing discipline exists to prevent, reached by a different route: not a member
that throws, but an interface never implemented.

- **The usage sweep has to force the mappings into existence.** A mapping is created lazily on first
  use, so a store that has opened no session has none — exactly the state a console sees on a fresh
  boot. `MaterializeMappings` asks the schema for every projection's aggregate type first.
- **`PartitioningStrategy` is reported as null rather than omitted.** SQLite has no table
  partitioning, so the field has a value — none — rather than being unknown.
- **A DDL failure is reported as a SQL comment, not thrown.** One bad mapping should not take the whole
  store's description with it.
- **`QueryDocumentsAsync` is hand-built SQL, which makes it a fourth caller of the three implicit
  filters.** It cannot go through `Query<T>()`: a console names its type as a *string* and filters on
  *columns* (correlation id, causation id, last-modified-by) that are not document members, so there is
  no expression tree to build. The mitigation is that each filter is composed from the one place that
  owns it — `SoftDelete.NotDeletedSql`, `DocumentHierarchy.FilterSqlFor`, the tenant column — rather
  than re-spelled, and `diagnostics_reads_carry_the_implicit_filters` pins all three.
- **A table that does not exist reports an empty page.** SQLite resolves a table name when it
  *prepares* a statement, so a count against a type whose table was never created fails before any
  guard could run — the lesson rebuild teardown and `CleanAsync<T>` both learned, met again on a
  console's first click.
- **A sub-class resolves to its base's mapping plus a `doc_type` filter.** A registered sub-class has no
  mapping of its own, which is fisher#17's whole point; the name a console passes may still be one.
- **A console's id is converted through the mapping's identity type before it is bound.** Fourth
  appearance of the uppercase-Guid trap: `fi_doc_*.id` holds the lowercase canonical form under a
  case-sensitive collation, so binding the caller's string directly matches nothing.
- **Projection replay copies the aggregate at every step**, and this is the one thing Polecat's does
  not. JasperFx's aggregation mutates the aggregate in place, so a timeline built from live references
  shows the *final* state at every step — the single thing a step-through exists not to do. The copy
  goes through the store's own serializer, which also makes each captured state exactly what would have
  been persisted. Found by the test failing with `[1, 1, 1]` where `[1, 2, 1]` was expected.
- **A step's exception is recorded on the step rather than thrown**, or the first bad event would hide
  every step after it. An unknown event type is skipped, following the stream reads' policy rather than
  the daemon's: a console may be pointed at a store holding types this deployment does not know.

### `Advanced`, cleaning and the projection scenario

`DocumentStore.Advanced` gained event store statistics, per-type cleaning, DDL script generation and
the projection scenario harness (fisher#42). Four independent pieces, none large.

- **`EventStoreStatistics` has three fields rather than two, and the third is the point.**
  `EventSequenceNumber` can exceed `EventCount`, because archiving, compacting or deleting events
  leaves the sequence where it was — `fi_events.seq_id` is `AUTOINCREMENT` and SQLite never reuses a
  value it handed out. That is load-bearing rather than incidental (a reused sequence below the
  daemon's high-water mark is an event no async projection ever sees), so the gap between the two
  numbers is the count of events that once existed and no longer do.
  **`sqlite_sequence` has no row until the first `AUTOINCREMENT` insert**, so the read is a `coalesce`
  and an untouched store reports 0 rather than throwing.
- **`CleanAsync<T>` matches against the tables that exist rather than issuing a blind `delete from`.**
  A document table is created on demand at first write, and SQLite resolves a table name when it
  *prepares* a statement — so cleaning a type that has never been written would fail before any guard
  in the SQL could run. Same lesson rebuild teardown learned. It is a real delete even for a
  soft-deleted type: flagging rows would leave a "cleaned" table that still answers `MaybeDeleted()`
  and still refuses an insert on a duplicate id.
- **`ToDatabaseScript()` is Weasel's**, inherited from `DatabaseBase` — Polecat writes its own only
  because it needs `GO` separators. `the_script_creates_the_same_schema_the_migration_does` applies the
  output to a fresh file and compares `sqlite_master`, which is the assertion worth having; that the
  string contains a table name is not.
- **`ProjectionScenario` is a seam and nothing else**, the same shape as `FisherProjectionDaemon` — the
  harness lives in JasperFx. Its teardown clears the event store and the document types the registered
  projections own, **not every table**: a scenario is entitled to seed documents its projections do not
  produce, and clearing those would make the harness quietly destructive.
  `the_teardown_leaves_unrelated_documents_alone` pins it.

### `Advanced` — the daemon escape hatches, single-stream rebuild and the tenant wipe

fisher#173 — the `AdvancedOperations` members Marten carries and Fisher did not. Four independent
pieces; two of them are operational escape hatches whose value is that they exist before you need
them.

- **`AdvanceHighWaterMarkToLatestAsync`** moves the high-water mark straight to `max(seq_id)`, for
  retrofitting async projections onto a store that has never had any — otherwise the mark climbs from
  zero, which on a large store is a long read with nothing to show for it. **It advances the mark and
  not the shards**, which is the half that decides whether it is the right call: a shard with no
  progression row still starts at zero, so this is for a store whose projections are new *and* whose
  history is genuinely not wanted. Spans every database, with a tenant-scoped overload that means
  something only under database-per-tenant — a conjoined store has one global sequence however many
  tenants write into it.
- **`TryCorrectProgressInDatabaseAsync`** pulls a progression row that has advanced past the highest
  sequence back down to it. **Reachable here through an ordinary supported operation, where Marten
  carries the same method for a PostgreSQL race it believes it has closed**: `seq_id` is
  `AUTOINCREMENT`, and stream compacting and event masking both delete rows, so removing events from
  the top of the table lowers `max(seq_id)` below progress already recorded. A shard stranded above
  the ceiling never advances again and `QueryForNonStaleData` waits on it forever, with nothing saying
  why.
  - **Clamped per row, where Marten resets every row wholesale** the moment the high-water row is
    ahead. That drags a shard genuinely *behind* the head forward, past events it never applied —
    silently, and on the very store somebody is already repairing.
    `correcting_leaves_a_shard_that_is_merely_behind_alone` is the discriminating fact.
- **`AllProjectionProgress` / `ProjectionProgressFor` / `AllAsyncProjectionShardNames`** were a
  surfacing job rather than new machinery: the first two have been on `FisherDatabase` since the
  daemon landed, reachable only by casting the store to `IEventStore` and walking `AllDatabases()`. An
  omitted tenant id spans every database — concatenating for the first, taking the highest for the
  second — which under database-per-tenant means one shard name appears once per tenant, deliberately:
  collapsing them would have to pick a winner, and "at 40 for one tenant and 900 for another" is what
  an operator came to find out.
- **`RebuildSingleStreamAsync<T>`** live-aggregates one stream and stores the result, for the repair
  that does not need the daemon.
  - **A stream that folds to nothing deletes the document, where Marten's throws from inside
    `Store(null!)`.** That case is not exotic — a `ShouldDelete` that fired, an archived stream, an id
    with no events — and "no document" is exactly what a real rebuild leaves for such a stream, since
    teardown clears the rows and the replay never recreates that one. Throwing would make the method
    unusable on the streams most likely to have gone wrong.
  - **Refused by name for a type with no Fisher mapping.** A projection registered through
    `Projections.StorageProviders` (an EF Core entity) is deliberately never mapped, so `Store` would
    create a `fi_doc_*` table nothing else ever reads — a rebuild that silently wrote to the wrong
    place.
- **`DeleteAllTenantDataAsync`**, and the distinction it rests on. **Fisher refuses tenant
  *deletion*** — deprovisioning here means deleting a *file*, the cheapest deprovisioning of any
  Critter Stack store and the most irreversible, and Fisher cannot know whether that file is backed up
  (see "Runtime tenants"). Wiping a tenant's *rows* is a different operation: it destroys nothing a
  file restore would be needed to recover, it is the only way to erase a conjoined tenant at all, and
  nothing covered it.
  - **Under database-per-tenant it clears the tenant's file and keeps it**, so the tenant goes on
    working and simply has no data. Removing the file stays the operator's act.
  - **Tag rows go first, through their events.** `fi_event_tag_*` carries no tenant of its own and has
    a real foreign key to `fi_events(seq_id)`, so the delete is a subselect and it has to precede the
    events — fisher#6's ordering met again with a tenant predicate on it.
  - **Progression rows are left alone**, under either tenancy. They describe how far the daemon read,
    not what a tenant owns, and clearing them would make every shard replay a store that is now empty.
    That is also why this needs no daemon pause, unlike `ResetAllDataAsync`.
  - ⚠️ **"Has a `tenant_id` column" is not the question, and reading it as one makes the refusal
    unreachable.** `fi_events`, `fi_streams` and `fi_dead_letters` carry the column on *every* store —
    the event tables get it with a default under non-conjoined tenancy, and a dead letter records the
    failing event's tenant as ordinary data. What the guard asks is what the store was *configured* to
    slice by: a database per tenant, conjoined events, or at least one `MultiTenanted()` document type,
    which is the only thing that puts the column on a `fi_doc_*` table. A store with none of those is
    refused by name rather than reporting a successful erasure of nothing.

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

### Natural keys

`fi_natural_key_<alias>` and `Events/Storage/NaturalKey*.cs` (fisher#40) — addressing a stream by the
business identifier it was created with. **The definition, the attributes and the discovery are all
JasperFx's** (`NaturalKeyDefinition`, `[NaturalKey]`, `[NaturalKeySource]`,
`JasperFxAggregationProjectionBase`), so what Fisher supplies is the storage seam, the same division as
the async daemon. Ported in shape from Polecat's `Events/Schema` + `Events/Projections` + `Events/Fetching`
set, with four things different and each of them a decision:

- **No `is_archived` column on the lookup table.** Polecat copies the flag from `pc_streams` and keeps
  it in sync from a projection watching for the `Archived` event — which then needs a second,
  rebuild-time entry point, because a daemon rebuild replays events without appending streams and
  would otherwise leave the table empty after teardown. Fisher archives with a direct operation rather
  than an event, so there is nothing to watch; and the lookup joins `fi_streams` anyway, so reading the
  flag off the join makes the streams table the only place that knows. Removing the filter makes
  `an_archived_stream_no_longer_resolves` fail.
- **The rows are written from the session, not from an inline projection.** A natural key row is an
  index over streams, not a projection of them. Being a projection is exactly what forces Polecat's
  rebuild path; nothing here is reachable from a rebuild, so there is nothing to repopulate.
  `NaturalKeyWriter` runs beside `EventTagWriter`, inside the append's transaction, because a key
  registered outside it leaves either a stream no key resolves to or a key naming a stream that does
  not exist.
- **A second stream claiming a key is refused, where Polecat repoints.** Polecat's `MERGE` updates the
  stream id on conflict, so the newcomer silently takes the key and the original stream becomes
  unreachable by the identifier it was created with. Fisher's conflict clause carries
  `where stream_id = excluded.stream_id` and returns the row it settled on — same stream returns it, a
  new key returns it, a conflicting stream matches nothing — and "no row" becomes
  `DuplicateNaturalKeyException`. That is the same shape the optimistic document upsert reads its
  version guard with. Re-asserting the *same* mapping stays idempotent, which it has to be: every
  event carrying the key rewrites the row.
- **No foreign key to `fi_streams`, uniformly.** Polecat declares one for a single-tenant store and
  omits it under conjoined tenancy, where Weasel.SqlServer's alphabetical column sorting breaks the
  composite mapping — so its two tenancy styles behave differently. One rule beats referential
  integrity in half the configurations, and a row whose stream is gone resolves to nothing anyway
  because the join is what produces an answer.

Two behaviours the shared suite settled, both of which Fisher had wrong until it ran (fisher#184):

- **Renaming a natural key retires the previous one.** A stream has exactly one *current* key, so the
  upsert is followed by a delete of every other row naming that stream — after the duplicate guard,
  never before, so a rename refused because the new key is live elsewhere leaves the existing mapping
  alone. Fisher used to leave the old row behind, which is not merely untidy: a retired alias that
  resolves forever also occupies its slot in the lookup's primary key forever, so no other stream can
  ever claim that identifier. Both siblings had reframed the same behaviour as a defect
  (polecat#435 / marten#5041) and Fisher was the third.
- **`FetchLatestByNaturalKey` answers a miss with null, where `FetchForWritingByNaturalKey` throws.**
  The asymmetry is the contract rather than an oversight: `FetchForWriting` is the read half of a
  read-modify-write and has to say what it would be writing to, where `FetchLatest(...) is null` is
  the idiomatic "does this aggregate exist?" probe — the same probe fisher#88 made honest for the
  by-id overload. Throwing made the key-shaped spelling the one member of the family that could not
  answer its own question. The `FetchForWriting` miss is deliberately outside shared scope
  (jasperfx#764), because that is the one place the three stores genuinely disagree: Marten hands back
  a null aggregate, Polecat throws `InvalidOperationException`, Fisher throws
  `UnknownNaturalKeyException`.

Two more things:

- **Resolving outside the write transaction is safe, and it is the same argument the optimistic append
  rests on.** The version guard runs inside the write transaction regardless, so a stale resolution
  fails the commit rather than writing a wrong version. A lock would only buy the loser waiting instead
  of failing — the trade Fisher already makes everywhere.
- **A Guid stream id binds as lowercase canonical text.** Third table where getting that wrong is
  silent, after documents and tag rows, and the failure mode is identical: every lookup returns
  nothing. Verified by binding the uppercase form, which makes the round-trip test throw
  `UnknownNaturalKeyException`.

`DeleteAllEventDataAsync` clears the lookup tables with the rest. Leaving them behind is not cosmetic:
the duplicate guard would then fire on data that no longer exists, and the compliance fixture cleans
before every test.

### The `IEventStoreOperations` surface

`EventOperations` implements JasperFx's `IEventStoreOperations` in full — the interface the
cross-store compliance suites route everything through, so declaring it is what makes
`EventStoreComplianceFixture.EventsFor(session)` possible at all.

Everything reachable without document storage is real: `FetchForWriting` rebuilds the aggregate by
live aggregation, `WriteToAggregate` is fetch + callback + `SaveChangesAsync`, and `ProjectLatest`
folds the session's pending events on top of the committed state.

**`FetchLatest` is the exception, and it reads a document rather than folding** — see below.

#### `FetchLatest` reads an Inline aggregate's document

`EventOperations.CanReadInlineDocument` (fisher#88, the polecat#463 class). `FetchLatest<T>` used to
live-aggregate the stream for *every* `T`, whatever its lifecycle, and the doc comment said so —
adding that Fisher had no snapshot storage to read instead, which stopped being true when
`Projections.Snapshot<T>` landed. So the claim aged into a bug: JasperFx's `InlineFetchPlanner` routes
an Inline aggregate to its projected document, and Fisher kept folding.

**The visible difference is a stream that exists but holds nothing `T` owns.** Marten and Polecat find
no document and return null; Fisher folded the foreign events and handed back whatever the aggregator
constructed. That matters because `FetchLatest<T>(id) is null` is the idiomatic "does this aggregate
exist?" probe — so the probe was satisfied by any stream id holding events at all, and the answer
depended on whether some other aggregate happened to share the id space.

- **A default-constructed aggregate is not neutral**, which is what makes this worse than an empty
  answer. A conventional `Create`/`Apply` aggregate came out null anyway, because nothing built an
  instance; the shape that surfaces it is a catch-all `Evolve(IEvent)`, which accepts every event type
  by construction. The reported case had `bool IsActive` defaulting to `true`, so the phantom read as
  an **active alert** for a service that had none.
- **Reading the document is what the write side already believed.** The inline projection screens out
  streams it does not own, which is exactly why no row was ever written for them; the read path now
  agrees. `no_document_is_written_for_a_stream_the_aggregate_does_not_handle` pins both halves
  together — it passed before the fix, and keeping it is the point.
- **Inline only**, mirroring `InlineFetchPlanner`. A Live aggregate has no document to read, and Marten
  routes an Async one to the document only when the mapping is revisioned.
- **Gated on the type having a Fisher mapping**, which is what keeps an EF Core-backed projection on
  the old path: a type registered with `Projections.StorageProviders` is deliberately never mapped, so
  its rows are in no `fi_doc_*` table and `LoadAsync` would answer about a table nothing writes to.
  `HasMappingFor` is the same question the storage registry is itself constructed with, and unlike
  `MappingFor` it does not create a mapping as a side effect of asking.
- **And gated on the key type**, because a stream identity and a document identity are not always the
  same type — a natural key resolves to a stream *key* (string) for an aggregate whose document id is a
  Guid, and that key cannot address the document at all. Those fall back to live aggregation exactly as
  before.
- **`IdType` rather than `StoredIdType`, which is the one place Polecat's version does not port.**
  Polecat compares the *inner* type so a strong-typed id matches on the value it wraps, because its
  load path re-wraps on the way through; Fisher's does not. `LoadAsync<T>(Guid)` resolves storage by
  hard-casting to `IDocumentStorage<T, Guid>`, and a strong-typed aggregate's storage is keyed on the
  wrapper — so unwrapping passes the gate and then throws `InvalidCastException` from inside the load.
  **`StrongTypedIdentityCompliance` is what caught it**, which is the argument for the suites in one
  line: the gate looked right, matched the sibling it was ported from, and was wrong. Comparing
  `IdType` leaves a strong-typed aggregate folding the stream exactly as before, so the phantom
  survives for that shape alone; fisher#89's `LoadAsync<T>(object)` is the entry point that resolves a
  canonical and a wrapped identity alike, and widening this is what it makes possible.
- **`FetchForWriting` deliberately still folds**, as it does on Polecat. The two ask different
  questions: `FetchLatest` reports current state, where `FetchForWriting` is the read half of a
  read-modify-write whose guard is the stream's version — so the fold is what the version it hands back
  has to agree with.

**Nothing on `IEventStoreOperations` throws any more.** What did lived in
`EventOperations.Unsupported.cs`, one file on purpose so that file shrinking was the progress
measure; it reached zero members and was deleted. Reintroduce it, rather than scattering throws, if a
future JasperFx release widens the interface past what Fisher implements.
**`FetchForWriting<T, TId>` and `FetchLatest<T, TId>` are whole now too**, which they were not for a
long time: in the siblings that overload is the natural-key and strong-typed-id entry point, fisher#14
closed the second half and fisher#40 the first. **The stream identity type wins where the two
coincide** — a string id on a string-identity store is read as the stream key — because which reading
applies must not depend on whichever aggregate types happen to declare a key.
`FetchForWritingByNaturalKey` / `FetchLatestByNaturalKey` are the unambiguous spellings.

One Fisher-specific hazard in this area: pending streams are tracked in a **dictionary keyed by
identity**, where Polecat uses a list. `FetchForWriting` must therefore reuse an already-tracked
`StreamAction` rather than construct a fresh one — replacing the dictionary entry would silently drop
events an earlier `Append` had queued for the same stream in the same session.

#### The aggregate write cache

`Events.CacheAggregatesForWriting<T>()` and `EventOperations.AggregateForWritingAsync` (fisher#97 /
jasperfx#674) — a node-local, opt-in, second-level cache of aggregate snapshots between
`FetchForWriting` calls. The contract, the key, the default bounded implementation and the registration
surface are all JasperFx's; what Fisher supplies is the fetch path, which is the same division as the
async daemon.

**Grade 1 and only grade 1.** The cached snapshot is a *baseline*: the stream version is read on every
call whether the take hit or missed, the events after the baseline are folded on, and the optimistic
guard still runs inside the write transaction. A stale entry costs a larger fold — never a wrong
aggregate, never a suppressed concurrency failure. The "trusted" variant that also skips the version
read is retired upstream against a measurement (0.19 ms of a 13.2 ms round); **do not reintroduce it**,
and if `a_cached_baseline_cannot_suppress_a_concurrency_exception` ever fails, that is what has
happened.

- **What a hit removes here is bigger than on either sibling, and it is a different thing.** Marten and
  Polecat load a stored snapshot and fold what follows, so the cache removes a *document read*. Fisher's
  `FetchForWriting` deliberately folds the whole stream on every call — see `CanReadInlineDocument` for
  why it does not read the Inline document the way `FetchLatest` does — so a hit removes *the fold of
  the history*. The measurements behind jasperfx#674 are PostgreSQL round trips and do not transfer;
  the saving is real here for a different reason.
- **No enabled/disabled branch anywhere.** `ResolveCache(Type)` hands back `NulloAggregateWriteCache`
  for an unenrolled type, so the ordinary path is unchanged: every take misses and every store is
  dropped. `RecordAggregateCacheWriteBack` returns early on the nullo cache, so an unenrolled fetch also
  allocates nothing.
- **Nothing is stored at fetch time, and the reason is take-on-read rather than poisoning.** An entry
  written while the caller still holds the instance can be claimed by a second session, which folds
  *its* delta onto the very object the first caller is still reading — the aggregate would silently gain
  state nobody in that session appended. Since the whole subject is that caching is unobservable except
  in latency, that is disqualifying. The write-back is deferred to the end of the unit of work, as
  Marten's is.
- **The version stored is the one read *before* the unit of work appended anything**, which is where
  Fisher diverges from the issue's advice to take it from the committed `StreamAction`. That advice is
  for a store whose inline projection mutates the very instance `FetchForWriting` handed out; **Fisher's
  does not** — it loads the snapshot document and builds its own — so labelling the instance with the
  committed version would claim events it has not applied, and the next fetch would fold them twice.
  `aggregate_write_cache.the_inline_projection_leaves_the_fetched_aggregate_alone` pins the premise,
  because it is a fact about Fisher rather than a choice, and the write-back is wrong the moment it
  stops holding.
- **A failed commit therefore needs no compensation.** The baseline describes committed state that
  existed either way, so there is no poisoned entry — which is also why the flush runs for an enlisted
  session, where the post-commit hooks do not. It runs outside the resilience pipeline for fisher#12's
  reason.
- **A fetch that never commits leaves nothing behind**, having consumed the entry it claimed. The
  contract expects that: an implementation may evict whenever it likes, and dropping an entry is always
  sound.
- **The key's database identifier carries the logical store as well as the file.** Under
  database-per-tenant `FisherDatabase.Identifier` already separates two files; within one file two
  logical stores are separated by the table prefix rather than by a schema, so `DatabaseSchemaName` is
  folded in. `AggregateWriteCacheOptions.Cache` names exactly that collision as the one its key cannot
  close. The tenant component is always the session's — Fisher has no aggregate registered as global,
  and under conjoined tenancy two tenants share a stream id space.
- **A rewritten event below a baseline is the one real hole, and it is not closable here.** A cached
  baseline is derived state, so masking or overwriting an event body leaves it holding what the old body
  produced — the same caveat a snapshot, document or flat table already carries, and the caveat masking
  is documented under. Evicting on rewrite would close it only within the rewriting process: the cache
  is node-local by construction, so another node's entry is unreachable. Documented as a reason to leave
  a rewritten aggregate unenrolled rather than half-closed with a guarantee the design cannot make.
- **Compacting needs nothing**, and that is worth knowing rather than assuming: JasperFx's aggregator
  calls `Compacted<T>.MaybeFastForward` before folding, so a delta that reaches the snapshot event
  *replaces* the baseline outright. A baseline below a compaction point heals on the next fetch for
  free.

`AggregateWriteCacheCompliance` (14 tests) is the definition — its subject is that a hit is
indistinguishable from a miss, including when the baseline is stale, ahead of the stream, or evicted.
Every one of those facts is vacuously true of a store that ignored the opt-in, which is why the suite
brings its own recording cache and asserts a nonzero hit count. Verified by disabling the take: exactly
the two hit-count facts fail. `aggregate_write_cache` covers what the suite cannot see — the premise
above, the write-back's timing and version, the tenant and logical-store components of the key, and
that an unenrolled type never reaches the cache at all.

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

#### What `TryCreateUsage` puts on the wire — fisher#120

`projections list` rendered "No projections in this store." for a store with twenty registered, and
`projections rebuild` matched none of them. The descriptor is where both of those answers come from,
and Fisher never populated `EventStoreUsage.Subscriptions`.

**The fix is one line — `Options.Projections.Describe(usage, this)` — and that is the uncomfortable
part rather than the reassuring one.** Everything it fills was already built: `ProjectionGraph.Describe`
is the shared implementation, and Fisher's two projection source types of its own
(`CompositeIProjectionSource`, `FlatTableProjection`) already implemented `Describe`. Nothing about
the gap was Fisher-specific, which is exactly why it survived — there was no dialect decision to
prompt anybody to look.

**The general rule this establishes: an unfilled slot on `EventStoreUsage` does not read as "this
store does not describe that", it reads as *the store has none*.** Every member is a list or a
nullable that starts empty, so the failure is silent in the one direction that matters — no
exception, no warning, and a console rendering a confident, wrong answer. That is why the whole
descriptor was audited alongside the reported member rather than the one line being added:

- **Both event-type collections.** `Events` and `RegisteredEventTypes` are the same registry twice and
  a consumer may read either. Filling one alone is polecat#411.
- **`TagTypes` and `DcbTagTypes`**, off `EventGraph.TagTypes`. Fisher has DCB; the console had no way
  to know.
- **`ProjectionErrors` and `ProjectionRebuildErrors`** separately, because they differ — a rebuild
  stops on an error a normal run skips, and a console reading one for the other offers "view related
  dead letters" to a store that halts.
- **`EventMetadata`**, whose four event flags are the opt-in `Enable*` options; every *stream* facet
  is universal in Fisher, so those keep the capability defaults.
- **`GlobalAggregates` and `DiscoveredDcbAggregates` stay empty, and that is the correct answer** —
  Fisher registers no aggregate as global and has no `IDcbAggregateRegistry`. Worth saying, because
  under the rule above an empty list is a claim rather than an omission.

**`MaxEventSequence` is the one entry that means something different here than on either sibling.** It
is the physical `max(seq_id)`, and the gap between it and the high-water mark is what CritterWatch#150's
second signal renders. **On Fisher there can never be a gap** — one writer per file plus
`BEGIN IMMEDIATE` makes committed sequences contiguous, which is the same fact that lets
`FisherHighWaterDetector` skip gap detection entirely. So the signal cannot fire, and reporting the
number is what lets a console establish that; leaving it null renders as "n/a" and says nothing.

Two decisions in how it is read:

- **A failed read costs the number, not the description.** The likeliest reason it fails is that the
  schema does not exist yet, which is precisely when a monitoring tool is most likely to be pointed at
  the store. `a_store_with_no_schema_still_describes_itself` pins that the method stays useful there.
- **`Describe` is called last, as Polecat calls it.** It registers each subscription's included event
  types on the event graph as a side effect, so calling it earlier would let those types into
  `usage.Events` and make a diagnostics call's output depend on whether anything had asked before.

**The shared suite cannot catch this class of gap for any store**, which is worth knowing before
assuming compliance covers the descriptor: `EventStoreExplorerCompliance` asserts on `usage.Events`
alone and returns early when the usage is null. `event_store_usage` is Fisher's own until that grows a
sibling, and it asserts presence rather than shape for the reason above — all nine of its tests fail
against the shipped 1.0.2 behaviour.

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

### Bulk insert

`Advanced.BulkInsertAsync` (fisher#36). **There is no `SqlBulkCopy` to reach for and none is needed**:
on SQLite the cost of an insert is dominated by the transaction rather than by the statement, so a
prepared statement re-executed with rebound parameters inside one transaction is already the fast path.
The statements are the ones `SqliteDocumentStorageDescriptorBuilder` already builds, reached through a
session — a second set of write SQL is exactly where the positional `?` contract would drift apart
unnoticed.

- **`batchSize` is a ceiling on how long the write lock is held, not a throughput knob.** One writer
  per file means a single transaction over a very large set blocks every other writer for its whole
  duration. The trade is that **bulk insert is not atomic across batches** — a failure part way leaves
  earlier batches committed, which `a_failure_part_way_leaves_earlier_batches_committed` pins so it
  reads as a decision rather than being discovered.
- **`IgnoreDuplicates` filters, where both siblings use a statement** (fisher#53). Marten has
  `on conflict do nothing` and Polecat a temp table and a `MERGE`; Fisher's four write statements are
  consumed by Weasel's shared closed-shape operations *by name*, so a fifth would need a slot on
  Weasel's own `DocumentStorageDescriptor`. Each batch instead reads which of its ids are already
  stored and queues only the rest. Three things in that read:
  - **It deliberately ignores the soft-delete and hierarchy filters** an ordinary load applies. The
    question is not "can I read this" but "would inserting this collide", and a soft-deleted row still
    holds the primary key — which is why it is hand-built rather than routed through `LoadManyAsync`
    or `Query<T>()`, the one place in the LINQ-adjacent code that going around them is correct. It
    *does* scope by tenant, because a conjoined table keys on `(tenant_id, id)`. Adding the
    soft-delete term makes `a_soft_deleted_row_is_still_a_duplicate` fail with `UNIQUE constraint
    failed`, which was verified.
  - **Both sides compare as invariant strings.** Microsoft.Data.Sqlite hands an INTEGER column back as
    `long` while an `int` identity's raw value is an `int`, and boxed to `object` those never compare
    equal — so an int-keyed type finds nothing and fails on the constraint the mode exists to avoid.
    `ignore_duplicates_over_an_integer_identity` fails with exactly that without the normalisation.
  - **The probe is outside the write transaction, and the window is not silent.** A concurrent writer
    inserting one of the same ids in between makes the insert fail with its unique-constraint
    violation rather than being skipped. Closing it would mean holding `BEGIN IMMEDIATE` across the
    probe through an enlisted session, which forfeits the `SQLITE_BUSY` retry — a worse trade for the
    operation most likely to contend for the write lock.

### Patching

`session.Patch<T>(id)` / `Patch<T>(predicate)` (fisher#35) — changing part of a stored document
without loading it. Every operation is one json1 function inside a single
`update … set data = …`, and a chain nests into one statement.

**This is the strongest single case for SQLite in the backlog.** No server function to install (Marten
needs a PL/pgSQL patch function), no `JSON_MODIFY` shape differences, and it composes. And **a
duplicated field follows a patch with nothing to refresh**, because fisher#2 made duplicated fields
`VIRTUAL` generated columns over `data` — both siblings must update theirs inside the patch SQL. That
is the clearest dividend of that decision.

**What a patch costs, said plainly:** `json_set` re-renders the document, so a patched row is no longer
byte-identical to what the serializer would have written and a new or renamed key lands at the end. It
avoids the deserialize/mutate/serialize round trip, *not* the row rewrite. Do not let "patching avoids
the round trip" imply "patching is cheap" — and note it breaks the byte-exactness fisher#28 promises.

- **Values go in through the store's serializer, not `SqliteParameterValue`.** fisher#34's conversions
  exist to match *columns*; a patched value lands inside `data`, so it must match what a full write
  would have produced — a timestamp in System.Text.Json's format rather than `SqliteTimestamp`'s.
  Wrapping in `json(?)` then makes a string a JSON string, a number a JSON number and an object a JSON
  object with no per-type branching.
- **`Increment` needs `coalesce(…, 0)`.** `json_extract` of an absent *or null* key is SQL NULL and
  `NULL + n` is NULL, so without it the member silently becomes null instead of the increment.
  `increment_an_absent_member` uses an `int?` member deliberately: a non-nullable `int` serializes as 0
  rather than being absent, and the first version of that test passed with and without the coalesce.
- **Steps that read what they change read the accumulated expression**, not the bare `data` column, so
  a chain sees its own earlier work. The cost is that the text grows with the chain.
- **The value placeholders are indexed.** `ICommandBuilder.AppendParameter` writes its marker into the
  SQL *at the point it is called*, so the expression cannot bind while composing — it would put `@p0`
  in front of `update`. It is built with placeholders and split at render time; the index rather than a
  bare marker is needed because `AppendIfNotExists` embeds the accumulated expression twice, and a
  positional placeholder would be counted twice over.
- **The version and timestamp columns are assigned explicitly.** They are not in the JSON, so nothing
  about the json1 expression moves them — and without it an optimistic-concurrency type would silently
  stop seeing patched writes and `ModifiedSince` would miss them.
- **A patch does not reach a soft-deleted row**, the same rule the load SQL and the LINQ default filter
  follow. Verified by removing the guard.
- **The by-name overloads take the *stored* key**, not a CLR member name — `"name"`, not `"Name"`. That
  is the point of them: reaching a key the type no longer has a member for, which is exactly what
  `Rename` is for. They are deliberately not routed through `MemberFactory`, which would refuse the
  case they exist for.
- **`Insert` at an index rebuilds the array** (fisher#52). `json_insert` only inserts where the path
  does not exist, so at an occupied index it is a silent no-op, and `json_replace` overwrites rather
  than shifting. The rebuild does not lean on `json_each`'s row order, which is not a documented
  guarantee: it computes an explicit ordinal from `json_each.key` and orders by it — an existing
  element keeps `2k` below the insertion point and takes `2k+2` at or above it, and the new element is
  `2*index+1`, so it lands strictly between two neighbours. An index past the end sorts above
  everything and therefore appends, which is why that case needs no length to check.
- **The rebuilt element must be keyed on `json_each.type`, not on its value**, and this is where the
  original `Remove` was wrong twice. SQLite has no boolean, so a JSON `true` arrives as the integer 1
  and `json_quote` writes it back as `1` — every rebuild silently turned an array's booleans into
  numbers. And a JSON `null` element reads back as SQL NULL, so `Remove`'s `where value <> ?` was NULL
  rather than true for it and **every removal dropped every null in the array**. `ElementSql` and an
  `is not` comparison fix both; `the_rebuild_keeps_booleans_and_nulls` covers them, and each half was
  verified by reverting it.
- **json1's JSON subtype does not survive a subquery.** `Insert` projects its elements through one, so
  `json_group_array(v)` writes every element as a quoted string; the aggregate has to re-parse with
  `json_group_array(json(v))`. `Remove` never meets this because its rebuild is a single flat select.

### JSON-returning reads

`LoadJsonAsync`, `ToJsonArrayAsync`, `ToJsonFirstWithVersionAsync` and `StreamJsonArrayAsync`
(fisher#28). They skip the deserialize-then-reserialize round trip when the caller is going to write
the document to a response anyway.

**The saving is larger here than on either sibling**, and the reason is structural: on Marten and
Polecat it saves CPU for data that already crossed a network from a database server; in Fisher the
database *is* the caller's process, so the round trip is the whole cost rather than a fraction.

- **`data` is TEXT holding exactly what System.Text.Json wrote**, so the read is byte-exact.
  PostgreSQL's `jsonb` normalises whitespace and key order and SQL Server's `nvarchar` needs an
  encoding decision — neither sibling can promise this, and `load_json_is_byte_exact_against_what_the_serializer_wrote`
  pins it against the serializer's own output rather than a hand-written literal.
- **Concatenated in .NET, not with `json_group_array`.** That function re-parses and re-renders every
  document — discarding the whole saving and reordering object keys on the way.
- **Every one goes through the ordinary statement path**, so the tenant, soft-delete and hierarchy
  filters apply without being restated. A JSON read composing its own `select data from …` would be
  one more caller having to remember all three, which is how fisher#51 happened.
- **`ToJsonFirstWithVersionAsync` asks for `guid_version` explicitly** — a query-only read normally
  drops it, having no version tracker to feed — and is refused for a type without optimistic
  concurrency, since the column does not exist. Named plainly rather than surfacing as
  `no such column`.
- **`StreamJsonArrayAsync` materializes before writing, deliberately.** A retried `SQLITE_BUSY`
  re-executes the whole delegate, so streaming a live reader to the caller's stream would resume
  against a disposed reader *and* a half-written response body. This is the one place the retry
  semantics and the streaming goal genuinely conflict; buffering is the resolution, because the saving
  being chased is the serializer round trip rather than the buffer.

### Batched queries, query plans, `CheckExistsAsync` and `ToSql`

fisher#37 widened the DCB-only batch into a general one and moved it from `Fisher.Events.Tags` to
`Fisher.Batching`, since it is no longer tag-specific. **The framing in its doc comment still stands
and should not be softened**: a batch elsewhere collapses network round trips, SQLite is embedded, and
there are none to collapse. It exists so DCB and document code ports between the stores unchanged. The
one property that does hold is ordering — the reads run back to back on one connection with nothing
interleaved.

- **A failing item neither stops the batch nor vanishes.** Every item runs, each task is completed or
  faulted, and `Execute` then throws for what failed. Both halves are load-bearing: stopping at the
  first failure leaves later items' tasks uncompleted, so a caller awaiting one *hangs* rather than
  seeing an error; faulting only the item's task lets a caller who never awaits that particular item
  conclude the batch succeeded. One failure rethrows as itself, several become an
  `AggregateException` — the same rule the session's batch executor follows.
- **`CheckExistsAsync` routes through the LINQ path**, not through a hand-written
  `select 1 from … where id = ?`. That is what makes it carry the tenant filter, the soft-delete filter
  and a hierarchy discriminator without restating any of them — it is a fourth caller that would
  otherwise have to remember all three, which is exactly how fisher#51 happened.
- **`ToSql` renders parameter names, not values**, so the text is readable rather than executable. It
  is the cheapest way to assert that an implicit filter is actually present, which is what
  `to_sql_shows_the_filters_fisher_adds` uses it for.
- `QueryListPlan<T>` implements both `IQueryPlan` and `IBatchQueryPlan` from one `Query` method, which
  is what keeps the batched and unbatched paths from drifting into two different queries with one name.

### Binary event bodies

`JasperFx.Events.BinaryEventAttribute`, `StoreOptions.Events.DefaultBinarySerializer` and
`StoreOptions.Events.UseBinarySerializer<TEvent>(…)` (fisher#43, reshaped by fisher#93) — an event
body stored as a BLOB in `fi_events.data_binary` rather than as JSON text in `data`.

⚠️ **The interface and the attribute live in `JasperFx.Events`, not in Fisher.** fisher#43 declared
Fisher-native copies; jasperfx#669 promoted the pair into the core in 2.50.0 and fisher#93 deleted
Fisher's, so one serializer implementation serves Fisher, Marten and Polecat. The core signature is
`Serialize(Type type, object data)` / `Deserialize(Type type, byte[] data)` — **argument order
reversed** from Fisher's old one, which is a silent break for an implementation that used positional
names, so it is a compile error only because the parameter *types* also swap. Do not re-add a
Fisher-local copy: two `BinaryEventAttribute`s in scope is CS0104 in every file importing both
namespaces.

**Worth more here than the same feature is on Marten**, and for a structural reason: Fisher is
embedded, so the store's disk footprint *is* the application's, and SQLite has no `jsonb` — where
PostgreSQL keeps a compact binary form for free, Fisher stores the literal JSON text of every event
forever, property names included.

- **A separate nullable BLOB column, not BLOBs mixed into `data`.** SQLite would tolerate the mixture,
  since affinity is a preference rather than a constraint — but then `typeof(data)` is the only way to
  tell an encoding apart, and `json_extract` over the column silently stops meaning anything for the
  rows that are binary. One nullable column per row buys an unambiguous shape.
- ⚠️ **`data_binary` is UNCONDITIONAL, and `data` keeps its NOT NULL constraint** — fisher#93 reversed
  both halves of fisher#43's gate, and the reversal is the point of the issue rather than a tidy-up. A
  binary row writes the placeholder `EventsTable.JsonPlaceholder` (`{}`) into `data`. Two consequences,
  both load-bearing: an existing store upgrades with a plain `ALTER TABLE ADD COLUMN` instead of the
  table rebuild SQLite demands to relax a NOT NULL, and there is no longer a schema decision to take
  before the store is created. Appending a `[BinaryEvent]` type with no serializer configured is still
  refused by name — it is a configuration error, not a silent reversion to JSON.
- **`data_binary` is composed last in the SELECT and gets the last `MetadataSlots` ordinal**, so every
  ordinal above it is unmoved whether or not the store has one. Same reason fisher#29's session
  metadata binders were appended rather than inserted.
- ⚠️ **Which column a row's body is in is decided PER ROW, on `data_binary IS NULL` — never off the
  event type.** fisher#43 dispatched on the type and fisher#93 reversed that too. It is what makes
  marking a type `[BinaryEvent]` an in-place change on a live file: rows written before the change are
  still JSON and still read. A type-based dispatch sends every one of them down the binary path, where
  a null BLOB is either an exception or an event with every member at its default — silent, since the
  row and the stream are otherwise intact. `turning_a_type_binary_on_an_existing_file_needs_no_migration`
  pins it, as does the shared `BinaryEventSerializationCompliance`.
- ⚠️ **The BLOB is bound as a `byte[]` parameter, never routed through a text encoding.** Arbitrary
  bytes — gzip output, MessagePack — are exactly what a text round trip corrupts, and nothing else is.
  The compliance suite's serializer gzips deliberately for this reason.
- ⚠️ **The column list and the bind order are one contract, and fisher#43 broke it.** `data_binary` was
  named ninth in the INSERT while being bound last, so a store with any of the four `Enable*` metadata
  columns on wrote a binary event's BLOB into `correlation_id`. Nothing caught it: the binary tests
  enabled no metadata and the metadata tests appended no binary event. It is last in both now.
- **Both rewrite operations refuse a binary event by name, and that was the likeliest way this feature
  could corrupt data.** They write the JSON `data` column; against a binary row that leaves a JSON body
  *and* a BLOB body, and every reader resolves by event type — so the JSON would be invisible and the
  row quietly wrong. `QueryEventDataAsync<T>` (fisher#41) refuses one too, because `data` holds only the
  placeholder for those rows: it would match nothing and report that as an answer.
- **Compacting works and clears the BLOB.** The snapshot it writes is a JSON `Compacted<T>`, so the
  replace is permitted; `ReplaceEventOperation` nulls `data_binary` as well, or the row keeps a body no
  reader will ever look at.
- **Fisher ships no `IEventBinarySerializer`, and that is the end state** — the position `IMessageOutbox`
  holds. A binary encoding is a choice with real consequences for schema evolution (MessagePack,
  protobuf and compressed JSON fail differently when an event type gains a member), and picking one
  would be Fisher deciding how an application's data ages.

Everything that reads the row's *columns* is unaffected — stream reads, the daemon's loader, DCB tag
queries, `QueryEventsAsync`'s metadata filters — which is why a stream can mix the two encodings
freely, and why the daemon needed no change at all.

### Event upcasting

`StoreOptions.Events.Upcasters` (fisher#191) — how an old stored event schema is reinterpreted as the
current CLR event type on every read path. **The registry, the transformation shape and the
`IEventUpcaster` bases are all JasperFx's** (`JasperFx.Events.Upcasting`, jasperfx#752); Fisher
supplies the read path and one `IUpcastPayload` adapter, the same division as the async daemon.

The whole read-side integration is **one call in `FisherEventsRowReader.ReadEventCore`**, which is
what having a single hydration point buys — every stream read, live aggregation, `FetchForWriting`,
DCB tag query and daemon page already converges there.

- ⚠️ **The registry is consulted BEFORE `ResolveEventType`, and that ordering is the marten#4680
  authority rule** rather than an optimisation. A registered transformation is the authoritative
  interpretation of its source event type name, so the stored `dotnet_type` hint does not get a vote.
  The case it exists for is a store that still has the old CLR type in its codebase: a typed append
  of it writes both the source name and a hint pointing at the old type, and letting the hint win
  would read *those* rows as the old schema while every row the previous deployment wrote upcast
  correctly — one store, one event type name, two answers.
- ⚠️ **Hydration became asynchronous for this** (`ValueTask<IEvent?>` throughout). The shared contract
  lets a transformation be registered async-only, whose synchronous delegate throws by design — so a
  store hydrating synchronously could not honour one at all. Every Fisher read path was already
  inside an `await reader.ReadAsync(...)` loop, so the cost is a `ValueTask` per row, and the ordinary
  path awaits nothing and completes synchronously. `TryUpcast` is guarded on `Upcasters.HasAny`
  first, so a store with no upcasts pays one boolean field read per row.
- **`FisherUpcastPayload` is a `readonly struct`**, per-row and never cached, which is what the
  contract expects — a transformation calls exactly one accessor exactly once. The ordinary path never
  constructs one.
- **The raw-`JsonDocument` accessor is unconditional here**, where the contract permits a store to
  refuse: Fisher is System.Text.Json-only and `data` holds exactly the text it wrote. Marten's is
  optional because its serializer is configurable. **A binary body (fisher#93) is the one exception** —
  its `data` column holds only the `{}` placeholder, so a raw-JSON transformation over one is refused
  by name rather than handed an empty object, which would upcast to an event with every member at its
  default. A *typed* transformation reads it through the old type's own `IEventBinarySerializer`.
- ⚠️ **The daemon's server-side type filter is widened with every transformation's SOURCE name**, or a
  shard filtered on the new types reads nothing at all from the history it was pointed at — silently,
  reporting itself caught up. The filter is pushed into SQLite precisely so non-matching rows never
  leave it, which is why the loader's in-memory check (which does see the hydrated, upcast event)
  cannot rescue it. **`UpcastingCompliance` does not reach this**: its daemon fact registers a
  snapshot projection, whose `IncludedEventTypes` allow list is empty, so no SQL filter is composed
  at all. `upcasting.a_subscription_filtered_on_the_new_type_receives_the_upcast_old_rows` is what
  pins it, and it fails by delivering nothing when the widening is removed.
- **The target event types are pre-registered in `DocumentStore`'s constructor**, from
  `Upcasters.AllTransformations`. Nothing else would: dropping the old type is the point of an
  upcast, so no `AddEventType`, projection registration or append ever mentions the new type either.
  Done at construction rather than at registration because a transformation may be registered through
  the shared JasperFx surface, which Fisher owns no hook in.
- **The envelope keeps the row's stored `type` and `dotnet_type`**, not the transformation's. It is a
  claim about the row, and nothing dispatches on it — `IEvent.EventType` is `Event<T>`'s `T`, which is
  the new type.
- **Upcasting does not reach a projection that has already run.** The high-water mark is a sequence
  and registering a transformation does not move it, so a document built from the old schema keeps
  what it derived until that projection is rebuilt. Same caveat as masking, and the same reason.

`UpcastingCompliance` is the definition, and it is **the first suite in the library written ahead of
any store implementing the behaviour** — its gate ships closed and Fisher is the first to flip it, so
all seven facts ran for the first time anywhere here. Enrolling it found one suite bug: its
raw-JSON fact reached the stored body with a case-sensitive `GetProperty("CartId")`, which is
Marten's default casing and not Polecat's or Fisher's — fixed upstream in jasperfx#787.
`Events/upcasting.cs` covers the two things the suite structurally cannot see: the server-side filter
above, and binary bodies, which the shared upcasting fixture knows nothing about.

### Querying event bodies

`EventOperations.QueryEventDataAsync<T>(predicate)` (fisher#41) — the counterpart to
`QueryEventsAsync`, which queries an event's *metadata*. That method's doc comment says a body member
is unreachable "because the body is JSON of a type the row only names". **That is true of `IEvent` in
general and false once the caller names the type**, which is the whole of this feature.

**It needed no new SQL machinery.** An event body is a JSON document in a TEXT column called `data` —
structurally identical to a document — so `MemberFactory`'s locators apply verbatim against
`fi_events`, including fisher#1's `strftime` wrapper for a timestamp inside a body.

- **There is no `DocumentMapping` involved, and that is not laziness.** Most event types have no
  identity member and `DocumentMapping` refuses a type without one; and asking for a mapping would
  *register* the event type as a document, giving it a table in the next migration.
  `MemberFactory` therefore has a mapping-free constructor — the mapping is only ever consulted for
  the identity member and for duplicated fields, and an event body has neither.
- **A body member called `Id` must not resolve to `fi_events.id`.** That column is the *event's*
  identity, so resolving to it would compare against the wrong column and return rows rather than an
  error. `a_body_member_called_id_is_not_the_events_own_id` pins it.
- **The type filter is `type`, the alias — not `dotnet_type`.** Short and stable where the other is
  assembly-qualified and brittle across a rename; the same reasoning as fisher#17's `doc_type`.
- **It is a scan**, and honestly so: there is no index over `fi_events.data`. fisher#16's expression
  indexes are the mechanism if one ever needs to be fast, and they would apply here unchanged.

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

**A boundary aggregate needs no single-stream identity** (fisher#135). `AggregateIdentity.ResolveIdType`
treats `JasperFx.Events.Aggregation.BoundaryAggregateAttribute` as an explicit opt-out and answers
`typeof(string)` — the vestigial `TId` the source generator already keys a marked identity-less type's
evolver on, so it is the only answer that finds the dispatcher.

**Fisher is the first store to actually honour it, which is not what fisher#135 assumed.** The issue
filed this as a Fisher-only divergence on the strength of Polecat's DCB page, which documents the
marker as the answer across the stack. Polecat's *source* has no mention of it: its
`IAggregationSourceFactory.Build<TDoc>()` resolves identity through `DocumentMapping`, whose
constructor throws for a type with no `Id`. Verified by running it rather than by reading — a
`[BoundaryAggregate]` aggregate with no identity failed there with *"must have a public property named
'Id'"*, from `DocumentMapping..ctor`. So the divergence closed here was between Fisher and the
attribute's documented contract rather than against a sibling's behaviour. **Polecat has since
followed** — polecat#521 shipped in Polecat 5.21.0, one day after Fisher 1.0.5 — so the marker now
means the same thing on both, and the docs caveat that said otherwise is gone. **The shared suite catches
neither, because every DCB aggregate in it happens to carry an identity** — jasperfx#718 is the request
to add an identity-less one, which has to fold *with events present* or it passes on a broken store.

- **The marker is the whole exemption, and the message names it.** An unmarked identity-less aggregate
  is still refused, because it is far more often a forgotten `Id` than a deliberate boundary aggregate
  — the same reason the generator emits nothing for one. What changed beside the exemption is that the
  refusal now mentions `[BoundaryAggregate]`, since the old text ("single stream aggregates need an
  `Id`") was accurate for the aggregate it was written about and misleading for this one.
- **Not inherited.** The generator reads the attribute off the declaring type in its own compilation,
  so a subclass inheriting the marker here would resolve to `string` and then fail to find a dispatcher
  of its own.
- **The empty boundary is why this bites late, and why the coverage has to have events.**
  `FetchForWritingByTags` folds only when the query finds something, so a boundary over an empty result
  — the ordinary "this must not exist yet" assertion — succeeded before this was honoured. A suite
  exercising only that path is green over a model that throws on first real use.
  `boundary_aggregates.fetch_for_writing_by_tags_folds_an_identity_less_aggregate` is the discriminating
  test; `concurrent_boundary_appends` dropped the unused `Id` it carried as the workaround.

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

**Fisher enrolls 50 of the 52 suites `JasperFx.Events.ComplianceTests` 2.65.0 ships — 516 tests.**
`JasperFx.Events.ComplianceTests` is referenced unconditionally — the old `$(EnableComplianceTests)`
gate is gone. See HANDOFF.md for the live scoreboard, which is machine-checked against a real run by
`scripts/check_scoreboard.py`; what follows is the history and the mechanics.

### Wave 13 — the first suites to run anywhere (fisher#184)

**2.64.0's eight new event suites had never been executed against a real event store.** The JasperFx
repository enrols only the document suites, so the whole wave arrived compile-checked and
design-reasoned. That makes Fisher's enrollment first-contact runtime validation, and it changes how
a red fact should be read: as likely an over-tight assertion as a store bug. Nineteen were red on the
first run and every one was classified — five genuine Fisher bugs (all fixed), two upstream bugs
(jasperfx#778 product, jasperfx#779 suite; both fixed upstream, and #779 is already released), three
suites gated off for a LINQ surface Fisher does not have, one suite deferred to the upcasting node.

**jasperfx#778 is the one worth knowing**, because it is the shape a store-side reader would not
suspect: an `Archived` event the aggregate applies nothing for did not archive its stream, on any
store. `Archived` carries no state, so an aggregate has no reason to declare an `Apply` for it, which
left it out of the projection's `AllEventTypes` — and `ApplyInline`'s opening
`streams.Where(AppliesTo(...))` then screened the whole stream out before reading anything. Marten's
own archiving tests all append a handled event beside the marker, which is why it survived until a
store ran the suite. Two facts stay red on Fisher until the fix ships; see HANDOFF.md.

The five Fisher bugs are worth knowing as a set, because they share a shape — a member that looked
right, that nothing local had a reason to question:

- **`AlwaysEnforceConsistency` did nothing.** See "Session metadata on appended events" and the
  append planner: `CollectActionableStreams` kept only streams with at least one event, so the flag's
  entire subject — a stream fetched, flagged, and then left alone — was dropped along with its guard.
- **Appending to an archived stream landed.** Now `Exceptions.ArchivedStreamException`, raised from
  `AppendPlanner.PlanStream` — and deliberately not for a `StartStream`, where an archived id is
  still an id in use and the collision is the more useful answer.
- **`FisherProjectionStorage.ArchiveStream` was empty**, with a comment answering a different
  question. The seam means *archive the stream*, and both siblings queue their archive operation
  there.
- **`FetchLatest` by natural key threw on a miss** where the contract is null.
- **Renaming a natural key left the old row behind**, so a superseded identifier resolved forever
  and its slot in the lookup's primary key could never be reused.

Two features were built to enrol rather than to gate: `Fisher.Batching.FetchStreamStatePlan` /
`FetchStreamPlan` (parity with polecat#370), and the compliance registrar's `UseMessageOutbox`.

**The three gated-off suites decline a LINQ surface, not a behaviour.** `DcbHasTagLinqCompliance`,
`AggregateToLinqOperatorCompliance` and `AggregateToManyCompliance` all terminate a cross-stream
`QueryAllRawEvents()` returning `IQueryable<IEvent>`. Fisher's LINQ provider is built over *document*
storage — statements, selectors and member factories all resolve against a `fi_doc_*` table — so an
event queryable would be a parallel provider serving one caller, which is why
`EventOperations.QueryEventsAsync` takes a predicate and `AssignTagWhere` reaches the same parser
through `EventMemberFactory` instead. The capabilities themselves are covered and green through
`DcbTagQueryAndConsistencyCompliance`, `AssignTagWhereCompliance` and `AggregateByTagsAsync`.

### History

**Fisher was enrolled in full — all 37 suites, 320 tests — as of 2.56.0.** The most recently enrolled are `BinaryEventSerializationCompliance` (6, the event half's
twenty-ninth) and `DocumentSessionEventsCompliance` (5) from 2.50.0, and 2.51.0's two:
`PendingStreamActionsCompliance` (9, fisher#96) and `AggregateWriteCacheCompliance` (14, fisher#97).
All four are **opt-in** — their contract members carry throwing defaults, so enrolling is a deliberate
line rather than something a bump does to you.

**2.52.0 added the 37th suite and 2.56.0 the 320th test, and nothing between them moved.**
`DocumentCommitListenerCompliance` (10) is the document half's seventh; 2.52.1, 2.53.0, 2.54.0 and
2.55.0 changed no suite file at all. 2.56.0 added one test —
`EventStoreExplorerCompliance.usage_describes_the_registered_projections`, jasperfx#700 — which is
**fisher#120's regression guard, and shared rather than Fisher-local on purpose**: `TryCreateUsage`
had one shared test asserting on `usage.Events` alone, so a store could fill that slot, leave the
rest of `EventStoreUsage` empty and still pass the suite. Nothing about the omission was
dialect-specific, so no store-level decision existed to prompt anybody to look. Fisher passed it
unchanged on the bump — see "What `TryCreateUsage` puts on the wire" for the one-line fix it holds
in place.

2.51.0's third change is fisher#98 and is fixture-side: `DocumentComplianceConfig.StreamIdentity`
(jasperfx#672) replaced an inference `FisherDocumentComplianceFixture` was making — string identity
whenever the config declared event types, right only for as long as `DocumentSessionEventsCompliance`
was the sole suite populating `EventTypes`.
The event sourcing half is 28 suites and 230 tests and has been the whole library since 2.45.0
emptied the upstream event sourcing backlog; **2.46.0 added no suite and no test** (fisher#64), and
**2.48.0 added neither, and changed no existing suite file** — the counts were re-verified against a
real run on each rather than carried over, since "still 32" is exactly the claim a bump can quietly
falsify. **2.47.0 added a second half rather than another event suite** — four *document* suites,
over the store-agnostic contract described below.

**2.49.0 is the first bump to widen an existing suite rather than add one** (fisher#89 / jasperfx#665).
No new suite file; `DocumentLoadAndStoreCompliance` gained three tests, taking the document half from
42 to 45, and `DocumentComplianceConfig` gained `ValueTypes` / `RegisterValueType<T>()` because the
strong-typed facts need the identity type registered and the document contract carries no identity
configuration. **That shape is worth noticing**: a bump whose suite *list* is unchanged can still
demand production work, so diffing the file list is not enough — diff the contents. The three tests
are two strong-typed facts and one guarding the canonical case, which is the one an override gets
wrong; see "The store-agnostic document contract".
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

The four **document** suites — `DocumentSessionCompliance`, `DocumentLoadAndStoreCompliance`,
`DocumentDeleteCompliance`, `DocumentQueryCompliance` — arrived in 2.47.0 and are a second half rather
than a fifth wave of the same thing: they hold the *document* store to a shared definition, which
nothing cross-store did before. See "The store-agnostic document contract" for the binding and for the
two Fisher-specific fixture notes. Two things worth knowing here:

- **`DocumentQueryCompliance` is not a LINQ conformance suite and upstream is emphatic that it must
  not become one.** The standing Critter Stack position — LINQ is out of shared-compliance scope
  permanently, not pending a contract — is unchanged. What it pins is narrower and different in kind:
  the *minimum translatable set* `Query<T>()` promises, since a consumer holding only `IQueryable<T>`
  cannot discover whether `OrderBy` is translated or silently unsupported. Fisher supports far more and
  is deliberately not held to any of it here.
- **This is what replaces the hand-comparison sweep.** Fisher's document parity with Polecat was
  established by the file-by-file comparison that filed fisher#22–#50, which is a one-time act;
  enrolling converts it into a standing definition three stores are held to.

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

**A stream carrying no events is dropped from the unit of work — unless it asked not to be.**
`CollectActionableStreams` keeps only streams with at least one event, and `CollectGuardOnlyStreams`
beside it keeps the ones flagged `IEventStream.AlwaysEnforceConsistency` with an
`ExpectedVersionOnServer` set, so their version is re-read and checked under the write lock with no
append written. Until fisher#184 the flag did nothing at all: it is on the shared `IEventStream`,
Fisher forwarded it to the `StreamAction` faithfully, and nothing else in the store ever read it.
That is invisible to any ordinary append test, because appending a single event brings the ordinary
guard back — the flag's whole subject is the *empty* case, "I read this stream, decided on the
strength of what I read to write nothing, and that decision is only sound if the stream has not
moved". `AlwaysEnforceConsistencyCompliance` is what found it.

This duplicates the private `StreamAction.ProcessMetadata`, which is normally reached through
`StreamAction.PrepareEvents` — and Fisher **cannot** use `PrepareEvents`. In Quick mode it numbers
events only when `ExpectedVersionOnServer` is already set, because Marten and Polecat let the database
assign versions while Fisher numbers them client-side from the version it just read. Pre-setting
`ExpectedVersionOnServer` to make it number them would make the optimistic-concurrency check inside
the same method compare that value against itself and pass unconditionally. Keeping version
assignment and metadata application apart is what keeps the guard real; the cost is that a new
metadata field in JasperFx will not reach Fisher's events until this method learns about it.

#### `Timestamp`, and that cost coming due — fisher#119

**The cost named above is not hypothetical, and `Timestamp` is the field it came due on.**
`PrepareEvents` stamps it (as `EventSlice` does for a projection's raised events) and
`ApplySessionMetadata` did not — so an event reached an inline projection carrying
`DateTimeOffset.MinValue`, and every read model recording `e.Timestamp` baked in a year-0001 date.
Worse than merely wrong: **the same projection produced different documents inline and rebuilt**,
because replay reads the column, so the divergence was invisible until somebody looked at a date.

- **One reading per stream, not per event** — the events of one append share a moment the way they
  share a transaction — and **round-tripped through `SqliteTimestamp` before it is assigned**. The
  column keeps milliseconds, so stamping raw ticks would leave the inline view sub-millisecond ahead
  of every later read of the same event: the same divergence, one digit further down.
- **`FisherQuickAppendEventsOperation` persists the stamped value instead of `NowExpression`**
  whenever it is set. That is the half that makes the two views agree; stamping alone would have
  fixed the year-0001 symptom and left inline and replay a clock apart.
- **Guarded on `== default`**, so a caller's own value survives — and so does the first attempt's
  when the resilience pipeline re-executes the write delegate. A retried commit therefore persists
  the moment the append was made rather than the moment it finally won the write lock, which is
  exactly what the inline projection already folded on attempt one.
- **Raised events are stamped by JasperFx before they ever reach here**, so the guard leaves them
  alone and the operation now persists their value too. They previously took the column default; the
  two paths are consistent for free.

**The trade, which is real and is not closable.** With inline projections registered the reading is
taken in `AssignVersionsAheadOfProjectionsAsync` — *outside* the write lock, because running before
it is that pass's whole purpose — so `fi_events.timestamp` is no longer strictly monotonic with
`seq_id`. Two writers on different streams can stamp in one order and commit in the other, skewed by
at most the write-lock wait. **Same-stream ordering is untouched**: the loser of a concurrent append
to one stream fails the optimistic guard rather than interleaving, so `FetchStreamAsync`'s timestamp
bound and the rewrite operations' assumption both still hold where they are actually read. The one
consumer reading that order *across* streams is `FindEventStoreFloorAtTimeAsync`, whose
`max(seq_id) where timestamp <= ?` can floor a rebuild-from-timestamp one skew window off.

It cannot be had both ways — an inline projection cannot see the final timestamp before the write
lock is taken *and* have it assigned under that lock. Marten resolves it identically, and
`EventAppendMode.Quick`'s own doc comment says timestamps come from `TimeProvider`, so this is the
family's behaviour rather than a Fisher concession, and ported projection code now behaves the same
here as it does there. **Without inline projections nothing moves at all**: the stamp is taken in
`PlanAsync`, which runs inside the write transaction.

#### Closed upstream gap — jasperfx#663

Historical, and the second completed cycle of the "workaround, filed upstream, removed when the fix
ships" rule after `FisherCommandBuilder`. This method used to stamp the stream's own identity onto
each event as well (fisher#72). `StreamAction.AddEvent` stamps `StreamId`/`StreamKey`/`TenantId` and
the `Guid` append factory went through it — but `StreamAction.Append(graph, string, …)` appended
straight to the backing list and did not, and `PrepareEvents` did not close the gap either (it sets
`TenantId`, `Timestamp`, `Version` and `Sequence`, never the stream identity). So **every event
appended to a string-identified stream reached an inline projection with an empty `StreamKey`** —
which is exactly how such a projection learns which entity it is projecting. Nothing threw: the
projection ran and wrote a document with a blank field, and the first visible symptom was a query
returning nothing.

Fixed upstream by [jasperfx#663](https://github.com/JasperFx/jasperfx/issues/663) and shipped in
**JasperFx 2.48.0**, which routes both string overloads through `AddEvents`. **The workaround is gone;
do not reintroduce it.** Verified by deleting it and running the full suite against 2.48.0 — 1228
green, including the test written to fail without it. That removal is also what makes 2.48.0 a real
floor for Fisher rather than a preference.

**The async daemon was never affected** — `FisherEventLoader` hydrates through
`FisherEventsRowReader.ReadEventAcrossStreams`, which takes the identity off the row.
`event_envelope_metadata_in_projections` now guards the upstream fix rather than a Fisher workaround,
and still pins **both** identities: the asymmetry was upstream's, so a later release must not silently
change which half is covered, and the Guid half passing is what tells a regression apart from a broken
test.

`ComplianceEventProjection` binds to `Fisher.Projections.EventProjection`. Its one required member,
`storeEntity`, is now an ordinary `IDocumentSession.Store` onto the session the events are committing
in, so a `Create`/`Project` method's return value lands in the same transaction as the event that
produced it. `inline_event_projections` covers it directly — note that a conventional-method
projection class must be declared `partial`, because the dispatcher is source-generated into it and
there is no runtime fallback.

### What the nupkg carries beyond Fisher.dll

**`JasperFx.Events.SourceGenerator` is bundled into the package as an analyzer** (fisher#73), the way
Marten (marten#4557) and Polecat (polecat#196) both bundle it into theirs. Projection dispatch is
source-generated with no runtime reflection fallback, and the generator ships as a development
dependency that does **not** flow transitively — so a consumer referencing only the Fisher package
never ran it.

**The absence is silent in the worst direction.** It is not a build failure: removing the generator
removes generated partials that nothing hand-written references, so the consuming assembly *compiles
clean* and then throws `No source-generated dispatcher found for EventProjection …` on its first
projected event. In a service that is a clean deploy followed by a crash on the first message.

Two things hold it in place, and neither is a test — the test projects reference the generator
directly, so nothing inside this repository can observe the packaged shape:

- `_BundleEventsSourceGeneratorAnalyzer` in `Fisher.csproj` contributes the analyzer DLL to
  `analyzers/dotnet/cs`. It runs **per-TFM**, because `$(PkgJasperFx_Events_SourceGenerator)` is only
  resolved there, and is **conditioned on one target framework**, because the analyzer is
  TFM-agnostic and both passes claiming the same package path fails pack on a duplicate file.
- The `package` job in `.github/workflows/fisher.yml` packs and asserts the DLL is present **and**
  that the nuspec does not declare the generator as a dependency. `PrivateAssets=all` is load-bearing
  for the second half: a generator that flows downstream is double-loaded by a project referencing
  two Critter Stack stores, which emits each `.Evolver` partial twice and fails with CS0111.

## The companion packages

Two, both modelled on Polecat's and Marten's, both multi-targeting net9.0/net10.0 as the core does.

### `Fisher.AspNetCore` (fisher#49)

Streaming `IResult` types, ETag handling, event-stream results and a high-water health check.

**The streaming results are worth more here than on either sibling, and the reason is structural.**
They exist to skip a deserialize-then-reserialize round trip. On Marten and Polecat that saves CPU for
data that already crossed a network from a database server, so it is a fraction of the cost. **Fisher's
database is the web process**, so the round trip *is* the cost — an endpoint reading a document and
returning it goes from "parse JSON, build an object, serialize an object" to "copy bytes".

- **`StreamMany<T>` diverges from Polecat's, and that is the point.** Polecat's materializes objects
  and calls `JsonSerializer.SerializeToUtf8Bytes`, which throws away the saving the type exists for.
  Fisher's uses fisher#28's `ToJsonArrayAsync`, which concatenates the stored `data` columns in .NET —
  nothing parsed, nothing re-rendered, and no `json_group_array` (that function re-parses and reorders
  object keys).
- **The bytes are exactly what was stored**, and `stream_one_writes_the_stored_bytes` asserts against
  the serializer's own output rather than a literal. Neither sibling can promise this: `jsonb`
  normalises whitespace and key order, `nvarchar` needs an encoding decision.
- **`StreamPaged`'s total is a header and a second statement**, not an envelope and not
  `count(*) over ()` — the window function returns no row for a page past the end, which is when a
  pager most needs the total. Same reasoning fisher#27 and the explorer's paging both record.
- **`StreamAggregate` reads the ETag before folding.** A stream's version moves if and only if an
  event was appended, so a matching `If-None-Match` answers `304` having read one row of `fi_streams`
  and folded nothing. For a long stream that is the whole value.
- **`ToJsonCursorPageAsync` shares its preparation with the typed `ToCursorPageAsync`** rather than
  repeating it. The ordering validation, the decode and the seek predicate are subtle enough that two
  copies would drift, and a drift there is a pager that silently skips or repeats rows.
- **`IQuerySession` gained `Events`.** An endpoint or a report taking a read session could not read
  streams before. Marten and Polecat narrow theirs to a read-only event surface; Fisher does not, for
  the same reason `IQuerySession` is itself a convention rather than a guarantee.
- **The health check has an argument of its own.** Fisher's daemon *warns* rather than refuses when
  the journal mode is not WAL, because that misconfiguration presents as a slow projection; this is how
  an operator finds out the warning mattered. Its stuck-mark message says so. What it *reads* is the
  poll-cycle age, with the gap heuristic as the secondary — see the async daemon's
  `HighWaterLivenessInterval` note for why the heartbeat column is not an option (fisher#60).
- **`StreamOne` serves a numeric-revisioned document from its `revision`** (fisher#62, the
  marten#5120 class). The two concurrency styles are alternatives, and a revision validates a cached
  representation exactly as well as a Guid version — refusing one of them left the whole revisioned
  half of a store unable to emit an ETag, with a message recommending the wrong setting.
  **Two read methods where Marten widened one**, because the flavors are two physical columns here
  (`guid_version` and `revision`) rather than one `mt_version` read at either width; `VersionSourceFor<T>()`
  is how a caller asks which applies, and no fail-fast guard against both was needed —
  `AssertConcurrencyIsCoherent` already refuses that pair at configuration time.
- **Three of marten#5157/#5158/#5166's hardening did not reproduce, for structural reasons rather
  than luck**, and `streaming_hardening` pins each so it stays that way. A 304 already returned before
  the write; a JSON read names its columns (`data, guid_version`) instead of aliasing whatever the
  storage selected, so neither a `Select` projection nor a tracking session can move the payload —
  marten#5166 was a 200 whose body was the document's id, through exactly that positional assumption.
- **A cursor whose key does not bind to its ordering member is an `ArgumentException`** (fisher#62,
  the marten#5029 class), not the `InvalidOperationException` `JsonElement` raises. The payload's
  shape was already checked; the per-key *bind* was not, so one way of malforming a client-supplied
  cursor produced a 400 and another a 500.
- **MCP endpoints are deliberately not ported.** That surface is moving upstream, and porting it
  speculatively would mean maintaining a copy of something about to change.
- Tested against a `DefaultHttpContext` with a `MemoryStream` body rather than through a test host: an
  `IResult`'s whole job is what it writes to a response, and a host would add a pipeline none of the
  assertions are about.

### `Fisher.EntityFrameworkCore` (fisher#50)

`DbContextTransactionParticipant<TContext>` over the `ITransactionParticipant` seam in the core.

**Verified before anything was built on it**, the discipline fisher#38 and fisher#2 both followed.
Against EF Core 9.0.14 and Microsoft.Data.Sqlite 10.0.9: `Database.UseTransaction` enlists,
`SaveChangesAsync` writes inside the transaction, another connection sees nothing until the commit,
and a rollback takes EF's write with it. All four are what the seam needs and none was safe to assume.

- **The safe constructor takes a factory over the connection Fisher supplies**, so the trap is not
  expressible. The one taking a built context checks `Database.GetDbConnection()` **by reference** —
  two connections to one file have the same connection string and are still two writers, so comparing
  strings would pass the exact case the check exists to catch.
- **That trap is a self-deadlock, not a slow path.** EF's write on a second connection blocks on
  Fisher's write lock *from inside Fisher's own transaction*, so it hangs rather than failing and
  nothing ever reports it. Refusing before the write beats a timeout, which beats a deadlock.
- **The package references `Microsoft.EntityFrameworkCore.Relational` only.** Which EF provider a
  `DbContext` uses is the application's decision; referencing the Sqlite provider would be Fisher
  making it for them, even though on SQLite it is nearly always the right one.
- **Pinned to EF Core 9.x, not 10.x**, because the package multi-targets net9.0 and net10.0 and EF
  Core 10 is net10-only. 9.x targets net8.0, so it loads on both.
- **`CompletelyRemoveAllAsync` leaves EF's tables alone**, because it filters by the `fi_` prefix.
  Correct — Fisher owning the file does not make it Fisher's to clear — and pinned so nobody "fixes" it.

**Projections whose documents are EF entities** — `options.ProjectToEfCore<TDoc, TId, TContext>(table,
factory)`, over `Projections.StorageProviders` in the core.

- **The registration is the whole seam, and that is the divergence from Polecat.** An ordinary
  `SingleStreamProjection<TDoc, TId>` or `MultiStreamProjection<TDoc, TId>` with conventional `Apply`
  methods writes into EF without knowing it does; the projection never mentions EF. Polecat's EF path
  is reachable only by deriving from `EfCoreSingleStreamProjection`/`EfCoreMultiStreamProjection`,
  which makes every EF-backed projection a different *kind* of projection. Fisher's one base class,
  `EfCoreEventProjection<TContext>`, exists for the per-event shape — which genuinely needs one,
  having no storage indirection to swap — and `EfCoreContext<TDoc, TId, TContext>()` on the identity
  setter is the escape hatch for an `Apply` that wants the context.
- **The context reads on its own connection and is moved onto Fisher's to write**, and both halves are
  forced rather than chosen. A projection's storage is resolved *before* the batch has opened the
  connection it will commit on, so there is nothing to build against; and the storage reads — every
  slice loads its current aggregate — long before there is a transaction to read in. **Verified
  against EF Core 9.0.14 and Microsoft.Data.Sqlite 10.0.9 before anything was built on it**, the
  discipline fisher#38 and fisher#2 both followed: a context that has already queried through its own
  connection accepts `SetDbConnection` onto another and writes through it, and a read on EF's own
  connection does not block against a `BEGIN IMMEDIATE` held elsewhere on the file. The second is the
  one that would have turned an EF-backed projection into a hang.
- **Nothing is written until the batch commits**, which is the same discipline `FisherProjectionStorage`
  follows by queueing operations. Here it is not merely tidy: writing as it went would be a second
  connection writing while the batch holds the file's one write lock.
- **The batch disposes the participants it was given**, because an EF-backed projection's `DbContext`
  is created per batch and cannot dispose itself — it has to outlive the apply that created it *and*
  survive a retried commit. Disposing at the batch boundary covers the failed batch too, which is the
  case that would otherwise leak a context per attempt behind a persistently failing shard.
- **A registered type is deliberately not mapped**, so registering the projection skips its mapping
  and the type gets no `fi_doc_*` table. That is what makes registration-before-projection
  load-bearing, and it is checked rather than documented — the same "this line has to come first"
  shape fisher#39 gave `SeedInitialDataOnStartup`.
  - **Both registration doors skip it, which was fisher#111**: only `Snapshot<T>` carried the
    `HasProviderFor` guard, so `Projections.Add(projection, lifecycle)` mapped the type anyway and
    left a stray, empty table in the migration. `Add` is the only door for a multi-stream projection,
    so that was *every* EF-backed one. Silent in both directions — the projection works, because
    storage resolution checks the registry first, and the table sits in the schema forever. The guard
    is per published type rather than per projection, since a projection may publish several and only
    some of them be registered.
- **Rebuild teardown reads the table name off the registry**, because the sweep that finds a
  projection's tables looks at *mapped* types. **This is the flat-table lesson one layer over** — the
  same gap `IPublishesTables` closes, reached from the other direction, and without it a rebuild
  replays onto the rows the previous run left. Pinned with a row the replay cannot recreate.
- **Fisher does not create the EF table.** Fisher owns the shape of tables it prefixes `fi_`; an
  entity's shape is the `DbContext`'s, so creating it is EF's job. Same reasoning that keeps
  `CompletelyRemoveAllAsync` from dropping them.
- `LoadManyAsync` is `FindAsync` per id rather than one `Contains` query, because Find answers from the
  change tracker first — so a slice whose entity this batch already touched comes back as the **same
  instance**, where a query would materialise a second one and the two would race at commit.
- Registering a bare `IProjection` now wraps it in JasperFx's `ProjectionWrapper`, which Fisher had no
  path for before: `Projections.Add(ProjectionBase, ...)` assumed every projection base was also an
  `IProjectionSource`. Polecat wraps in the same place.
- **`EfCoreProjectionStorage.IsThreadSafe` is `false`, and the `SemaphoreSlim` beside it is not an
  alternative to that** (fisher#108, over jasperfx#683). `AggregationRunner` posts a range's slices
  into a fixed ten-wide `Block` — ten real reader tasks — against one storage instance, and a
  `DbContext` is not thread-safe. The lock serializes each individual *call*; what it cannot close is
  the window *between* two, where one thread's aggregation mutates entities that another thread's
  `Entry()` is running `DetectChanges` over. Nothing reachable from inside the storage can stop the
  fan-out, which is why the seam had to be upstream. `FisherProjectionStorage` keeps the default of
  `true`: it queues onto the session, whose queue fisher#13 made thread-safe for this exact shape.
  - **Fisher was measurably harder to break than Marten, and that is the semaphore's doing rather than
    luck.** Marten reported real corruption (marten#5266 — `Dictionary.TryInsert`,
    `ChangeDetector.DetectChanges`); forcing Fisher back to ten-wide over 15,000 slice applications
    produced no exception at all. So the lock stays: it is what makes the residual window narrow, and
    the declaration is what closes it.
  - **The test asserts the fan-out stopped, not that a crash stopped**, which is the only assertion
    worth making about a data race — a test waiting for corruption is probabilistic in the direction
    that fails you, so green would prove nothing. `slices_are_applied_one_at_a_time` counts concurrent
    `Apply` calls: one with the declaration, **8 measured without it**. The counters live in the test's
    projection rather than in the storage, so production carries no instrumentation.

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
- **Raw data access goes through Weasel.Sqlite first** — its data source, its `CommandBuilder`, its
  table definitions and migrations. Deviate only where it cannot express what is needed, comment the
  site, and file the gap upstream so the workaround has something to be removed by. A helper that is
  really "how you do X against SQLite" rather than "how Fisher stores X" should be written to be moved
  into Weasel.Sqlite later. See "Raw data access goes through Weasel.Sqlite first".
- Database execution should go through `StoreOptions.ResiliencePipeline`.
- **Never call `SqliteConnection.ClearAllPools()`.** It disposes every pooled connection in the
  process, and xUnit runs test collections in parallel — one test's cleanup will take out another
  with `ObjectDisposedException: SQLitePCL.sqlite3`, intermittently enough to look like a flake.
  `TemporaryDatabase.Dispose` clears only its own connection string's pool.

## The documentation site

`docs/` is a [VitePress](https://vitepress.dev) site, published to
[fisher.jasperfx.net](https://fisher.jasperfx.net/) by `.github/workflows/docs.yml`. Set up the way
Polecat's is: same config shape, same sidebar/nav/footer, `vitepress-plugin-llms`, and the aside info
boxes.

```bash
npm install
npm run docs          # mdsnippets + dev server on :5173
npm run docs-build    # mdsnippets + build; a dead internal link fails the build
```

`docs/.vitepress/theme/custom.css` maps the logo's palette — `#2F4739` ground, `#9CBE84` accent,
`#F2F1ED` paper, `#3A3A38` ink, `#4A7343` for links on light — onto VitePress's own tokens **and
nothing else**. Hand-painting the nav bar and the code blocks in the ground colour was tried and
reverted: the nav's background does not span the sidebar column, and a dark code background leaves
the light theme's syntax colours unreadable against it. The file says so, so nobody retries it.

### Documentation samples come from compiled code

**Every code sample a reader would copy lives in a `#region sample_*` in real, compiled source and is
pulled into the markdown by [mdsnippets](https://github.com/SimonCropp/MarkdownSnippets).** A sample
that stops compiling then fails the build, rather than going stale in a page nobody rebuilds. This is
Marten's and Polecat's convention and Fisher follows it.

The mechanics:

- Samples live in `src/Fisher.Tests/Documentation/*_samples.cs`, one file per docs page or area, and
  are compiled by `dotnet build fisher.slnx` like any other test code. `mdsnippets.json` at the repo
  root configures the scan.
- Markdown carries `<!-- snippet: sample_name -->` / `<!-- endSnippet -->`; the tool fills the block
  **in place** and appends a source link back to the file and line on GitHub.
- `mdsnippets` runs as part of both `npm run docs` and `npm run docs-build`, and CI installs
  `MarkdownSnippets.Tool` before building the site. Committing the filled markdown is normal — the
  tool rewrites it either way.
- **Both scripts chain with `&&`, not through `concurrently` as Polecat's do.** Polecat runs
  `concurrently --group mdsnippets "vitepress build docs"`, which starts the fill and the build at the
  same time — so whether a snippet edit reaches the rendered page is a race it happens to win on a
  warm machine. Sequential costs nothing here and removes the question, along with the dependency.

**What stays an inline fence**, deliberately:

- Single-expression fragments shown in a list, where the surrounding method would be noise —
  `.Where(x => x.MaybeDeleted())` in the LINQ operator tables is the archetype.
- SQL, shell, and rendered DDL. There is nothing to compile.
- A contract Fisher does not own, quoted to show its shape.

**A contract Fisher *does* own is snipped from the real source file**, not retyped. `src/Fisher` is in
the mdsnippets scan for exactly this: put a `#region sample_*` around the interface and reference it,
so an interface that gains a member cannot silently disagree with the page that documents it. That is
the case where inline quoting rots most quietly, because nothing fails.

**This is worth more than it looks.** The first sample converted — the getting-started event round
trip — did not compile: `StartStream` returns a `StreamAction`, not the stream's `Guid`, and the
hand-written version had been copied into five more pages and the README before anything checked it.
That is the whole argument for the convention, and it was found within a minute of turning it on.

## Related codebases

### Repository layout

`src/Fisher` is the store. `src/Fisher.AspNetCore` and `src/Fisher.EntityFrameworkCore` are the
companion packages, each with its own test project; `Fisher.Tests` covers the core. All six build for
both TFMs and `dotnet test fisher.slnx` runs all of them.

| Codebase | Path | Use |
|---|---|---|
| Polecat | `~/code/polecat` | **The closest template** — SQL Server sibling; mirror its structure |
| Marten | `~/code/marten` | PostgreSQL reference implementation |
| Weasel | `~/code/weasel` | `Weasel.Sqlite` + `Weasel.Storage` sources |
| JasperFx | `~/code/jasperfx` | Core + Events framework (local clone may lag the pinned package) |
