# Handoff

State of Fisher after the JasperFx 2.39.4 upgrade, `DocumentStore : IEventStore`, the rebuild
concurrency cap, and the first two increments of the LINQ layer. Written for whoever picks this up
next.

[CLAUDE.md](CLAUDE.md) has the architecture and the SQLite traps; [ROADMAP.md](ROADMAP.md) has the
ordered plan. This document is the compliance scoreboard and the things that are true right now but
not obvious from either.

**248 tests green on net9.0 and net10.0.** 90 of them are shared cross-store compliance tests.

## Where we are against the compliance suites

`JasperFx.Events.ComplianceTests` 2.39.4 ships **17 suites, 124 tests**. Fisher passes **90 of 124
(73%), 14 suites of 17**. Every suite compiles; only the subclassed ones run.

2.39.4 added three suites. Two of them — `FetchLatestCompliance` and `StreamArchivingCompliance` —
went green on the version bump alone, with no production change: `FetchLatest`/`ProjectLatest` and
`ArchiveStream` were already built to the shape the shared suite expects. The third,
`EventStoreExplorerCompliance`, is what motivated `DocumentStore : IEventStore`.

### Green — 14 suites, 90 tests

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
| `RebuildConcurrencyCapCompliance` | 5 |
| `ActivityCorrelationCompliance` | 4 |
| `EventProjectionRegistrationCompliance` | 3 |
| `EventProjectionEnrichmentCompliance` | 3 |
| `AutoDiscoveredAggregateCompliance` | 2 |

### Remaining — 3 suites, 34 tests

| Suite | Tests | Needs |
|---|---|---|
| `DcbTagQueryAndConsistencyCompliance` | 26 | DCB tags + batched queries |
| `AssignTagWhereCompliance` | 6 | DCB tags |
| `AsyncDaemonCompliance` | 2 | the async daemon |

**Two capabilities still account for all 34.** Nothing is blocked on document storage, projections
or `IEventStore` any more.

`RebuildConcurrencyCapCompliance` was long described here and in the roadmap as needing projection
rebuilds. It does not touch them: all five tests read
`IEventStore.MaxConcurrentRebuildsPerDatabase` and one reads it back off `TryCreateUsage`. It went
green with a config knob and no daemon work.

### What the fixture still throws

`FisherComplianceFixture` implements every member; three throw `NotSupportedException` naming the
milestone. Enrolling a suite prematurely therefore fails loudly rather than passing on a stub.

- `CreateBatch` — no batched queries
- `StartDaemonAsync`, `WaitForNonStaleProjectionDataAsync` — no daemon

`EventStore` is live as of the `IEventStore` milestone — the fixture hands back the `DocumentStore`
itself. `LoadDocumentAsync` is fully live too: Guid, string, int and long all load. It still throws
for a strongly typed id, which Fisher does not support anywhere.

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

## Recommended next move

**Finish the LINQ layer, then DCB tags.** Two of four LINQ increments are committed; see
"The LINQ layer" below for what is done and what is left.

The ordering is not a preference. `AssignTagWhere` — 6 of the 34 remaining compliance tests — takes
an `Expression<Func<IEvent, bool>>`, and in Marten it is a *client* of the LINQ `WhereClauseParser`
over an event-metadata member set, not a bespoke translator. Building a special-purpose predicate
translator for it would be thrown away as soon as LINQ landed, and would diverge from both siblings.

LINQ also unblocks more than DCB: document querying (the largest remaining product gap),
`QueryEventsAsync` for `OpenReadOnlyEventStore`, and the batched-query seam (`CreateBatch`) that the
DCB suite needs anyway.

**The async daemon is now the smallest block at 2 tests**, and is still the only thing gating
`SnapshotLifecycle.Async`, which `Projections.Snapshot<T>` rejects outright. Take it whenever an
entire configured lifecycle being unreachable outweighs the tag count.

### Daemon-specific things to know before starting

Both are in CLAUDE.md, repeated because they will bite during this milestone specifically:

- **`AUTOINCREMENT` on `fi_events.seq_id` is load-bearing.** The high-water mark assumes sequence
  numbers only move forward. A bare `INTEGER PRIMARY KEY` aliases the rowid, which SQLite reuses
  after a delete — a reused `seq_id` would silently hide events from every async projection.
- **WAL is what lets the daemon read while a session writes.** On by default via
  `SqlitePragmaSettings.Default`, but a consumer overriding `StoreOptions.PragmaSettings` can turn it
  off and quietly serialize the daemon behind every write. Worth a guard or at least a documented
  warning when the daemon starts.

## The LINQ layer

Ported from Polecat, which owns `Polecat.Linq.SqlGeneration` itself rather than taking it from
`Weasel.SqlServer` — so Fisher carrying its own is the mirror, and no upstream Weasel change is
needed. `Weasel.Sqlite` has no `SqlGeneration` namespace at all.

Committed:

1. **`Fisher.Linq.SqlGeneration`** — the where-fragment set. `Statement` is the one genuinely
   dialect-specific file; see below.
2. **`Fisher.Linq.Members`** — member locators over `json_extract`.

Left: the `WhereClauseParser` / query-parser core, then the queryable, provider, selectors and query
handlers. `Joins`, `CursorPaging`, `SoftDeletes`, `GroupBySelectBuilder` and
`SelectProjectionAnalyzer` are deliberately out of scope — they serve features Fisher does not have.

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
`AllowsRangeComparison` false so the parser refuses rather than returning plausible-but-wrong rows.
**Lifting this needs a normalised sortable duplicate — the same machinery duplicated fields will
need**, so the two are worth planning together.

This concerns documents only. The `fi_events` / `fi_streams` timestamp columns are
`SqliteTimestamp`'s fixed-width UTC format precisely so they *do* sort as text.

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
- **Querying is still load-by-id.** The LINQ layer is two increments in but not yet wired to a
  session; the `ISelectClause` seam on `FisherDocumentStorage` is what it will hang off.
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
