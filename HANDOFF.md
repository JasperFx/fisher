# Handoff

State of Fisher after the JasperFx 2.39.4 upgrade and `DocumentStore : IEventStore`, written for
whoever picks this up next.

[CLAUDE.md](CLAUDE.md) has the architecture and the SQLite traps; [ROADMAP.md](ROADMAP.md) has the
ordered plan. This document is the compliance scoreboard and the things that are true right now but
not obvious from either.

**204 tests green on net9.0 and net10.0.** 85 of them are shared cross-store compliance tests.

## Where we are against the compliance suites

`JasperFx.Events.ComplianceTests` 2.39.4 ships **17 suites, 124 tests**. Fisher passes **85 of 124
(69%), 13 suites of 17**. Every suite compiles; only the subclassed ones run.

2.39.4 added three suites. Two of them — `FetchLatestCompliance` and `StreamArchivingCompliance` —
went green on the version bump alone, with no production change: `FetchLatest`/`ProjectLatest` and
`ArchiveStream` were already built to the shape the shared suite expects. The third,
`EventStoreExplorerCompliance`, is what motivated `DocumentStore : IEventStore`.

### Green — 13 suites, 85 tests

| Suite | Tests |
|---|---|
| `FetchForWritingCompliance` | 13 |
| `StreamReadCompliance` | 11 |
| `EventMetadataCompliance` | 9 |
| `SelfAggregatingEvolveCompliance` | 8 |
| `FetchLatestCompliance` | 7 |
| `LiveAggregationCompliance` | 7 |
| `StringIdentitySingleStreamCompliance` | 6 |
| `StreamArchivingCompliance` | 6 |
| `EventStoreExplorerCompliance` | 6 |
| `ActivityCorrelationCompliance` | 4 |
| `EventProjectionRegistrationCompliance` | 3 |
| `EventProjectionEnrichmentCompliance` | 3 |
| `AutoDiscoveredAggregateCompliance` | 2 |

### Remaining — 4 suites, 39 tests

| Suite | Tests | Needs |
|---|---|---|
| `DcbTagQueryAndConsistencyCompliance` | 26 | DCB tags + batched queries |
| `AssignTagWhereCompliance` | 6 | DCB tags |
| `RebuildConcurrencyCapCompliance` | 5 | projection rebuilds — `IEventStore` is no longer the blocker |
| `AsyncDaemonCompliance` | 2 | the async daemon |

**Two capabilities still account for all 39.** Nothing is blocked on document storage, projections
or `IEventStore` any more.

### What the fixture still throws

`FisherComplianceFixture` implements every member; three throw `NotSupportedException` naming the
milestone. Enrolling a suite prematurely therefore fails loudly rather than passing on a stub.

- `CreateBatch` — no batched queries
- `StartDaemonAsync`, `WaitForNonStaleProjectionDataAsync` — no daemon

`EventStore` is live as of the `IEventStore` milestone — the fixture hands back the `DocumentStore`
itself. `LoadDocumentAsync` is fully live too: Guid, string, int and long all load. It still throws
for a strongly typed id, which Fisher does not support anywhere.

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
