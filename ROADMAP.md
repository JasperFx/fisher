# Fisher Roadmap

Where Fisher is, what comes next, and why in this order. See [CLAUDE.md](CLAUDE.md) for
architecture and the SQLite-specific decisions.

Status as of `69bf873` + inline projections. 156 tests green on net9.0 and net10.0, **66 of them
shared cross-store compliance tests across 10 suites**.

## The destination

**First round of JasperFx compliance tests passing — reached.** `JasperFx.Events.ComplianceTests` is
the shared cross-store suite Marten and Polecat both enroll in; passing it is what makes Fisher a
real Critter Stack event store rather than a lookalike. Ten suites are green:

| Suite | Tests |
|---|---|
| `StreamReadCompliance` | 11 |
| `EventMetadataCompliance` | 9 |
| `LiveAggregationCompliance` | 7 |
| `ActivityCorrelationCompliance` | 4 |
| `AutoDiscoveredAggregateCompliance` | 2 |
| `FetchForWritingCompliance` | 13 |
| `SelfAggregatingEvolveCompliance` | 8 |
| `StringIdentitySingleStreamCompliance` | 6 |
| `EventProjectionRegistrationCompliance` | 3 |
| `EventProjectionEnrichmentCompliance` | 3 |

**Only four suites remain**, and between them they need exactly two things: the async daemon
(`AsyncDaemonCompliance`, `RebuildConcurrencyCapCompliance` — 7 tests) and DCB tags
(`AssignTagWhereCompliance`, `DcbTagQueryAndConsistencyCompliance` — 32 tests). Nothing else in the
suite catalogue is blocked on document or projection work any more.

## Done

| Milestone | Notes |
|---|---|
| Solution + build infrastructure | net9.0/net10.0, CPM, xUnit v3 on MTP, CI |
| `fi_` schema | `fi_streams`, `fi_events`, `fi_event_progression` via Weasel.Sqlite |
| SQLite dialects over Weasel.Storage | `SqliteStorageDialect<TId>`, `SqliteEventStoreDialect` |
| Sessions + append | `DocumentStore`, `FisherSession` UoW, `EventOperations`, `AppendPlanner` |
| Event store reads | `FetchStreamAsync`, `FetchStreamStateAsync`, `LoadAsync`, archive/un-archive |
| Live aggregation | `AggregateStreamAsync` over auto-discovered self-aggregating types |
| Event store write surface | `IEventStoreOperations` in full — `FetchForWriting`, `WriteToAggregate`, `AppendOptimistic`, `FetchLatest`/`ProjectLatest` |
| Compliance enrollment | `FisherComplianceFixture` + 5 suites, 33 shared tests |
| Session metadata | correlation/causation seeded from `Activity.Current`, applied to appended events |
| `StoreOptions.Projections` | thin `ProjectionGraph` — aggregator cache + evolver discovery |
| Document storage | `Store`/`Insert`/`Update`/`Delete`/`LoadAsync`/`LoadManyAsync`, Guid + string ids |
| Inline projections | `Snapshot<T>`, `Add(projection, lifecycle)`, applied in the events' own transaction |

The id-type question step 1 raised was settled with a minimal resolver, not by waiting on
`DocumentMapping`: `Storage/AggregateIdentity.cs` resolves the aggregate's identity member through
the shared `JasperFx.DocumentIdentity` helper — the same one Polecat's `DocumentMapping` delegates
to. When `DocumentMapping` lands it should resolve identity *through* `AggregateIdentity` rather than
beside it. `EventGraph` implements `IAggregationSourceFactory<IQuerySession>`; `StoreOptions.Projections`
was deliberately *not* stood up for it at the time, and once it did land `EventGraph.AggregatorFor<T>`
was repointed at `ProjectionGraph.AggregatorFor<T>` rather than keeping a second cache. See CLAUDE.md
for the source-generator constraint that shapes all of it.

`EventOperations` now declares the whole of `IEventStoreOperations`, which is what
`EventStoreComplianceFixture.EventsFor(session)` must return — the single interface everything
portable in the compliance suites runs through. What is not implemented is collected in
`EventOperations.Unsupported.cs` (DCB tags, event rewriting) rather than scattered. Two open
decisions came out of it:

- **Exclusive appends are the optimistic ones.** SQLite has no row lock; documented in CLAUDE.md's
  divergence table with what revisiting it would cost. Still open.
- **`AllAggregateTypes()` had no assembly scan.** Settled by standing up a thin
  `StoreOptions.Projections`, which was cheap only because the write-surface work had already made
  `IDocumentSession` an `IStorageOperations` — the constraint `ProjectionGraph` imposes. Fisher gets
  `DiscoverGeneratedEvolvers` from the framework rather than reimplementing it, which is what the
  earlier deferral was waiting for.

The `FisherCommandBuilder` shim is gone: weasel#424 shipped in Weasel.Sqlite 9.23.2. JasperFx is on
2.39.3; 2.37.2 → 2.39.1 is where the six newer compliance suites came from, and 2.39.3 changes no
suite source at all.

## Next, in order

### 1. Finish document storage

The write and load-by-id paths are done. What is left, roughly in value order:

- **Hi-Lo sequences** — a `fi_hilo` table and an `ISequence`, which is the only thing standing
  between Fisher and int/long document identities. Weasel offers no other numeric strategy, so
  `FisherDatabase.SequenceFor` throwing is what makes those ids unusable today.
- **Querying** — there is no LINQ and no way to fetch a document except by id. The
  `ISelectClause` seam on `FisherDocumentStorage` is in place for it.
- Soft delete, duplicated fields and user indexes, hierarchies, numeric revisions — each additive
  against the existing column shape rather than a rewrite of it.

### 2. Async daemon

**Now the highest-value milestone**: it is the only thing standing between Fisher and
`AsyncDaemonCompliance` + `RebuildConcurrencyCapCompliance`, and it is what makes
`SnapshotLifecycle.Async` — currently rejected outright — mean anything.

`FisherDatabase` must implement `IEventDatabase`. Needs high-water detection over `fi_events`,
event loading/paging, and `BuildProjectionDaemonAsync`. `DocumentStore` also needs to implement
JasperFx's `IEventStore` for the rebuild suite.

Two SQLite-specific things to think about up front:
- The high-water mark assumes `seq_id` only moves forward. `AUTOINCREMENT` is what guarantees that
  (see CLAUDE.md) — do not weaken it.
- WAL journaling is what lets the daemon read while a session writes. It is on by default via
  `SqlitePragmaSettings.Default`, but a consumer overriding `StoreOptions.PragmaSettings` could turn
  it off and quietly serialize the daemon behind every write.

### 3. DCB tags

The largest remaining block of compliance tests (32) and the only area Fisher has not started at
all. Needs the `fi_event_tag_*` tables, the tag write path, and the batched-query seam
(`IComplianceBatch`, `CreateBatch`). `EventGraph.RegisterTagType` already accepts registrations that
nothing reads.

### 4. Projections, the rest

Inline works. Still missing: the Async lifecycle (daemon), projection side effects
(`GetOrStartMessageSink` throws), `EventProjection.storeEntity` for projections that store arbitrary
documents, and composite projections.

## Enrollment status

Enrolling is one empty subclass per suite in `Compliance/fisher_event_store_compliance.cs`. Every
suite compiles whether or not it is enrolled — see CLAUDE.md for the two files that cannot compile at
all and are `<Compile Remove>`d.

| Suite | Tests | Status |
|---|---|---|
| `StreamReadCompliance` | 11 | **green** |
| `EventMetadataCompliance` | 9 | **green** |
| `LiveAggregationCompliance` | 7 | **green** |
| `ActivityCorrelationCompliance` | 4 | **green** |
| `AutoDiscoveredAggregateCompliance` | 2 | **green** |
| `FetchForWritingCompliance` | 13 | **green** |
| `SelfAggregatingEvolveCompliance` | 8 | **green** |
| `StringIdentitySingleStreamCompliance` | 6 | **green** |
| `EventProjectionRegistrationCompliance` | 3 | **green** |
| `EventProjectionEnrichmentCompliance` | 3 | **green** |
| `AsyncDaemonCompliance` | 2 | daemon (3) |
| `RebuildConcurrencyCapCompliance` | 5 | `IEventStore` on `DocumentStore` + rebuilds (3) |
| `AssignTagWhereCompliance` | 6 | DCB tags |
| `DcbTagQueryAndConsistencyCompliance` | 26 | DCB tags — the last one, 727 lines |

Every suite that document storage or projections could unblock is enrolled. The async daemon is the
next unlock at 7 tests; DCB tags are the larger prize at 32, and the only remaining area Fisher has
not started at all.

## Open items not on the critical path

- **Concurrency regression test.** The append path's safety rests on `BEGIN IMMEDIATE` being what
  `IsolationLevel.Serializable` produces — verified empirically against Microsoft.Data.Sqlite 10.0.9,
  but it is library behaviour Fisher does not own. `append_optimistic_loses_to_a_concurrent_commit`
  now covers the version-guard half (two sessions, one fails cleanly); what is still uncovered is a
  test that would fail if `Serializable` stopped producing `BEGIN IMMEDIATE` — that needs two
  genuinely interleaved writers, not two sequential `SaveChangesAsync` calls.
- **`TombstoneStreamOperation` is unreachable.** Written into the dialect, no caller. Archive/
  un-archive got wired up and tested; tombstone still needs a session-facing API.
- **Not started at all:** DCB tags, multi-tenancy beyond a tenant id column, subscriptions, DI
  registration (`AddFisher`), LINQ, bulk insert, natural keys, strongly typed ids.

## Things not to rediscover the hard way

All in CLAUDE.md, repeated here because each one cost real time:

- A non-literal column `DEFAULT` must be parenthesized.
- `AUTOINCREMENT` on `seq_id` is load-bearing, not decorative.
- Constraint-violation mapping needs the *extended* SQLite result code.
- Guids and timestamps convert explicitly in **both** directions — never rely on provider coercion.
- `dotnet test` cannot emit TRX under MTP; CI runs the test executable directly.
- `SqliteConnection.ClearAllPools()` is process-wide and xUnit runs collections in parallel — it
  disposes connections other tests are using. Clear one connection string's pool, never all of them.
- Conventional `Apply`/`Create` dispatch is emitted by JasperFx's source generator, keyed on
  `(aggregate, id type)`, with **no runtime fallback**. An aggregate with no `Id` gets no dispatcher.
