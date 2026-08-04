# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What is Fisher?

SQLite-backed Event Store and lightweight Document Database in the Critter Stack. A deliberate
subset of Marten (PostgreSQL) and Polecat (SQL Server), built on Weasel.Sqlite for schema management
and Weasel.Storage for the shared closed-shape document/event runtime.

**Fisher is early and incomplete.** See "Current state" below before assuming a feature exists, and
[ROADMAP.md](ROADMAP.md) for what comes next and in what order.

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
| Exclusive append | row lock — the loser **waits** | no row lock — the loser **fails** (see below) |
| Sequence read-back | bulk function / `OUTPUT ... INTO` | trailing SELECT by stream + version range |
| Load-many ids | `= ANY($1)` / `OPENJSON` | `json_each(@ids)` |
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
- `DocumentStore`, `FisherSession` unit of work, `EventOperations` (`IEventOperations`)
- `StartStream` / `Append`, version assignment, optimistic concurrency, sequence read-back
- Reads: `FetchStreamAsync` (version / from-version / timestamp bounded), `FetchStreamStateAsync`,
  `LoadAsync`, both stream identity styles
- `ArchiveStream` / `UnArchiveStream`
- Live aggregation: `AggregateStreamAsync`, `AggregateStreamToLastKnownAsync`, over auto-discovered
  self-aggregating types
- `FetchForWriting` / `WriteToAggregate` / `AppendOptimistic` / `FetchLatest` / `ProjectLatest`
- `EventOperations` implements the full `IEventStoreOperations` — see below for which members throw

Not implemented yet — do not assume these work:

- **Document storage.** `IStorageSession.StorageFor`, `IStorageDatabase.Providers` and
  `SequenceFor` throw `NotImplementedException`. No `Store`/`Load`/`Delete`, no LINQ.
- **Projections.** `StoreOptions.Projections` exists but is thin — it carries the live aggregator
  cache and the source-generated-evolver discovery, nothing more. Nothing is persisted: no
  Inline/Async lifecycles, no `Snapshot<T>`, no way to register a projection at all.
  `IStorageOperations.FetchProjectionStorageAsync` and `GetOrStartMessageSink` throw.
- **Async daemon.** `FisherDatabase` does not implement `IEventDatabase`.
- **DCB tags**, multi-tenancy beyond a tenant id column, subscriptions, DI registration.

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

### Compliance suites

**Fisher is enrolled.** `JasperFx.Events.ComplianceTests` is referenced unconditionally — the old
`$(EnableComplianceTests)` gate is gone. Five suites are live in `Compliance/`:
`StreamReadCompliance`, `EventMetadataCompliance`, `LiveAggregationCompliance`,
`ActivityCorrelationCompliance`, `AutoDiscoveredAggregateCompliance` — 33 shared tests. Every suite
still unenrolled is blocked on document storage, projections, the daemon, or DCB tags.

The mechanics, because they are not what the package's name suggests:

- **Every suite compiles; only the subclassed ones run.** Enrolling is one empty class in
  `fisher_event_store_compliance.cs`. Not enrolling costs nothing at runtime — but the shared source
  still has to compile, which is why all four global aliases in `ComplianceAliases.cs` must resolve
  even for suites Fisher cannot pass.
- **Two files cannot compile at all** and are `<Compile Remove>`d in the csproj:
  `EventProjectionRegistrationCompliance` and `EventProjectionEnrichmentCompliance` call
  `IDocumentSession.Store` and `IQuerySession.LoadAsync`, which Fisher does not have. Every other
  un-enrolled suite merely *uses* the session types and compiles fine. Delete those two lines when
  document storage lands.
- **`FisherComplianceFixture` throws `NotSupportedException` naming the milestone** for each member
  Fisher cannot honour (`LoadDocumentAsync`, `EventStore`, `AllAggregateTypes`, `CreateBatch`, the
  daemon pair). Enrolling a suite prematurely therefore fails loudly rather than passing on a stub.
- `CleanEventDataAsync` deletes straight from the tables. It is called before every test, so it
  cannot throw — it is a stand-in for `Advanced.Clean`, and should move there rather than grow here.

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

`ComplianceEventProjection` binds to `Fisher.Projections.EventProjection`, which exists but is not
exercised by anything yet: its one required member, `storeEntity`, is document storage and throws.

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
