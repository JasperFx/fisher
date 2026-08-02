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
| Sequence read-back | bulk function / `OUTPUT ... INTO` | trailing SELECT by stream + version range |
| Load-many ids | `= ANY($1)` / `OPENJSON` | `json_each(@ids)` |
| Unit of work | parallel, aggregates failures | strictly sequential (one writer per file) |
| Transient retry | none needed | real Polly retry on `SQLITE_BUSY` / `SQLITE_LOCKED` |

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

### Known upstream gap — weasel#423

`FisherCommandBuilder` exists only because Weasel.Sqlite 9.23.1's `CommandBuilder` does not declare
`Weasel.Core.ICommandBuilder` (Weasel.Postgresql, Weasel.SqlServer, and Weasel.Oracle all do;
Weasel.MySql is the other outlier). It is missing `TenantId`, `AppendParameters`,
`CreateGroupedParameterBuilder`, and a `DbParameter`-returning `AppendParameter`. Without the shim,
no Weasel.Storage operation can be configured against a SQLite command builder at all.

Filed as [JasperFx/weasel#423](https://github.com/JasperFx/weasel/issues/423). It is a faithful port
of the SQL Server implementation — **delete it and switch back to `Weasel.Sqlite.CommandBuilder`
once a release carries the fix.**

## Current state

Working, with tests:

- `fi_streams` / `fi_events` / `fi_event_progression` schema via Weasel.Sqlite
- `SqliteStorageDialect<TId>` and `SqliteEventStoreDialect` (Quick append + auxiliary operations)
- `DocumentStore`, `FisherSession` unit of work, `EventOperations` (`IEventOperations`)
- `StartStream` / `Append`, version assignment, optimistic concurrency, sequence read-back
- Reads: `FetchStreamAsync` (version / from-version / timestamp bounded), `FetchStreamStateAsync`,
  `LoadAsync`, both stream identity styles
- `ArchiveStream` / `UnArchiveStream`

Not implemented yet — do not assume these work:

- **Document storage.** `IStorageSession.StorageFor`, `IStorageDatabase.Providers` and
  `SequenceFor` throw `NotImplementedException`. No `Store`/`Load`/`Delete`, no LINQ.
- **Projections.** No `StoreOptions.Projections`, no live aggregation, no `FetchForWriting`.
- **Async daemon.** `FisherDatabase` does not implement `IEventDatabase`.
- **Live aggregation.** `AggregateStreamAsync` needs the projection layer.
- **DCB tags**, multi-tenancy beyond a tenant id column, subscriptions, DI registration.

### Compliance suites

`JasperFx.Events.ComplianceTests` is referenced but **gated behind `$(EnableComplianceTests)`** in
`Fisher.Tests.csproj`. It is a source-only package, so its suites hard-fail the build until Fisher
supplies three global aliases they bind against:

```
ComplianceQuerySession    -> Fisher.IQuerySession
ComplianceOperations      -> Fisher.IDocumentSession
ComplianceEventProjection -> Fisher's EventProjection base type   (does not exist yet)
```

Enabling them also requires document load-back, projections, and the daemon — see
`EventStoreComplianceFixture`. Flip the default to true once those exist.

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

## Related codebases

| Codebase | Path | Use |
|---|---|---|
| Polecat | `~/code/polecat` | **The closest template** — SQL Server sibling; mirror its structure |
| Marten | `~/code/marten` | PostgreSQL reference implementation |
| Weasel | `~/code/weasel` | `Weasel.Sqlite` + `Weasel.Storage` sources |
| JasperFx | `~/code/jasperfx` | Core + Events framework (local clone may lag the pinned package) |
