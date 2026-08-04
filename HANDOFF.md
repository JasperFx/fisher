# Handoff

State of Fisher after Hi-Lo sequences, the `Advanced` facade and `TombstoneStream`, written for
whoever picks this up next.

[CLAUDE.md](CLAUDE.md) has the architecture and the SQLite traps; [ROADMAP.md](ROADMAP.md) has the
ordered plan. This document is the compliance scoreboard and the things that are true right now but
not obvious from either.

**178 tests green on net9.0 and net10.0.** 66 of them are shared cross-store compliance tests.

## Where we are against the compliance suites

`JasperFx.Events.ComplianceTests` 2.39.3 ships **14 suites, 105 tests**. Fisher passes **66 of 105
(63%), 10 suites of 14**. Every suite compiles; only the subclassed ones run.

### Green — 10 suites, 66 tests

| Suite | Tests |
|---|---|
| `FetchForWritingCompliance` | 13 |
| `StreamReadCompliance` | 11 |
| `EventMetadataCompliance` | 9 |
| `SelfAggregatingEvolveCompliance` | 8 |
| `LiveAggregationCompliance` | 7 |
| `StringIdentitySingleStreamCompliance` | 6 |
| `ActivityCorrelationCompliance` | 4 |
| `EventProjectionRegistrationCompliance` | 3 |
| `EventProjectionEnrichmentCompliance` | 3 |
| `AutoDiscoveredAggregateCompliance` | 2 |

### Remaining — 4 suites, 39 tests

| Suite | Tests | Needs |
|---|---|---|
| `DcbTagQueryAndConsistencyCompliance` | 26 | DCB tags + batched queries |
| `AssignTagWhereCompliance` | 6 | DCB tags |
| `RebuildConcurrencyCapCompliance` | 5 | `IEventStore` on `DocumentStore` + projection rebuilds |
| `AsyncDaemonCompliance` | 2 | the async daemon |

**Two capabilities account for all 39.** Nothing is blocked on document storage or projections any
more — that was true up to `69bf873` and is no longer. Hi-Lo sequences and
`EventProjection.storeEntity` closed the last two document-storage holes without moving the
compliance number, because no enrolled suite exercised either.

### What the fixture still throws

`FisherComplianceFixture` implements every member; four throw `NotSupportedException` naming the
milestone. Enrolling a suite prematurely therefore fails loudly rather than passing on a stub.

- `EventStore` — `DocumentStore` does not implement JasperFx's `IEventStore`
- `CreateBatch` — no batched queries
- `StartDaemonAsync`, `WaitForNonStaleProjectionDataAsync` — no daemon

`LoadDocumentAsync` is fully live now: Guid, string, int and long all load. It still throws for a
strongly typed id, which Fisher does not support anywhere.

## Recommended next move

**The async daemon**, and not because of the test count — 7 tests is the smallest remaining block.
Take it first because:

- it is the last thing gating `SnapshotLifecycle.Async`, which `Projections.Snapshot<T>` currently
  rejects outright, so an entire configured lifecycle is unreachable without it;
- DCB tags (32 tests) are a from-scratch subsystem — new tables, a tag write path, and the batched
  query seam — and are better started from a store with no other half-built areas in flight;
- the two SQLite-specific hazards are already understood and written down (below), so the design
  risk is low.

If you want tests-per-hour instead, DCB tags are the bigger number. Say so explicitly when you pick,
because the roadmap orders them daemon-first.

### Daemon-specific things to know before starting

Both are in CLAUDE.md, repeated because they will bite during this milestone specifically:

- **`AUTOINCREMENT` on `fi_events.seq_id` is load-bearing.** The high-water mark assumes sequence
  numbers only move forward. A bare `INTEGER PRIMARY KEY` aliases the rowid, which SQLite reuses
  after a delete — a reused `seq_id` would silently hide events from every async projection.
- **WAL is what lets the daemon read while a session writes.** On by default via
  `SqlitePragmaSettings.Default`, but a consumer overriding `StoreOptions.PragmaSettings` can turn it
  off and quietly serialize the daemon behind every write. Worth a guard or at least a documented
  warning when the daemon starts.

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
- **No LINQ, no querying beyond load-by-id.** The `ISelectClause` seam on `FisherDocumentStorage` is
  in place for it.
- **`Projections.Snapshot<T>` rejects `Async`.** See above.
- **`GetOrStartMessageSink` throws** — projection side effects cannot be published.
- **No soft delete, hierarchies, duplicated fields, numeric revisions, sub-classing.** All additive
  against the current column shape.

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
