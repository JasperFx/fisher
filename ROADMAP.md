# Fisher Roadmap

Where Fisher is, what comes next, and why in this order. See [CLAUDE.md](CLAUDE.md) for
architecture and the SQLite-specific decisions.

Status as of `ed3833d` + compliance enrollment. 92 tests green on net9.0 and net10.0, 27 of them
shared cross-store suites.

## The destination

**First round of JasperFx compliance tests passing — reached.** `JasperFx.Events.ComplianceTests` is
the shared cross-store suite Marten and Polecat both enroll in; passing it is what makes Fisher a
real Critter Stack event store rather than a lookalike. Three suites are green:

| Suite | Tests |
|---|---|
| `StreamReadCompliance` | 11 |
| `EventMetadataCompliance` | 9 |
| `LiveAggregationCompliance` | 7 |

The destination is now **the rest of them**, and the ordering below is unchanged, because what the
remaining suites need is exactly what the remaining milestones build. Suite-by-suite blocking is
tabulated under "Enrollment status".

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
| Compliance enrollment | `FisherComplianceFixture` + 3 suites, 27 shared tests |

The id-type question step 1 raised was settled with a minimal resolver, not by waiting on
`DocumentMapping`: `Storage/AggregateIdentity.cs` resolves the aggregate's identity member through
the shared `JasperFx.DocumentIdentity` helper — the same one Polecat's `DocumentMapping` delegates
to. When `DocumentMapping` lands it should resolve identity *through* `AggregateIdentity` rather than
beside it. `StoreOptions.Projections` was deliberately *not* stood up for this; `EventGraph`
implements `IAggregationSourceFactory<IQuerySession>` and caches aggregators itself, which is the
same seam a `ProjectionGraph` falls back to. See CLAUDE.md for the source-generator constraint that
shapes all of it.

`EventOperations` now declares the whole of `IEventStoreOperations`, which is what
`EventStoreComplianceFixture.EventsFor(session)` must return — the single interface everything
portable in the compliance suites runs through. What is not implemented is collected in
`EventOperations.Unsupported.cs` (DCB tags, event rewriting) rather than scattered. Two open
decisions came out of it:

- **Exclusive appends are the optimistic ones.** SQLite has no row lock; documented in CLAUDE.md's
  divergence table with what revisiting it would cost.
- **`AllAggregateTypes()` still has no assembly scan.** `AutoDiscoveredAggregateCompliance` wants
  aggregate types discovered from `[GeneratedEvolver]` at construction. That is
  `ProjectionGraph.DiscoverGeneratedEvolvers`, which Fisher gets for free the moment
  `StoreOptions.Projections` exists — reimplementing it on `EventGraph` now would duplicate framework
  logic with a one-milestone shelf life.

The `FisherCommandBuilder` shim is gone: weasel#424 shipped in Weasel.Sqlite 9.23.2. JasperFx moved
2.37.2 → 2.39.1 in the same commit, which is where the six newer compliance suites came from —
including the three Fisher enrolled in immediately.

## Next, in order

### 1. Document storage

`IStorageSession.StorageFor`, `IStorageDatabase.Providers` and `FisherDatabase.SequenceFor` all
throw `NotImplementedException` today. Needs `DocumentMapping`, a `DocumentProviderRegistry` behind
`IProviderGraph`, the closed-shape document storages, `fi_doc_*` tables, and Store/Insert/Load/
Delete.

Prefer Weasel.Storage's closed-shape storages over hand-written SQL — the whole point of the
dialect layer is that this should mostly be configuration. Polecat's
`SqlServerDocumentStorageDescriptorBuilder` is the shape to mirror, minus the SQL Server type
mapping.

### 2. Projections

`ProjectionGraph<IProjection, IDocumentSession, IQuerySession>` — needs `IProjection`,
`StoreOptions.Projections`, the projection storage seam, and inline snapshot application during
`SaveChangesAsync`. Live aggregation already put the two hard prerequisites in place:
`IDocumentSession` implements `IStorageOperations`, and `Fisher.Projections.SingleStreamProjection<
TDoc, TId>` exists. `FisherSession.FetchProjectionStorageAsync` and `GetOrStartMessageSink` are the
`NotImplementedException`s to fill in.

**Steps 1 and 2 are entangled, not sequential.** `Projections.Snapshot<T>` needs somewhere to write
the snapshot, which is document storage. Expect to interleave them rather than finishing one first.

### 3. Async daemon

`FisherDatabase` must implement `IEventDatabase`. Needs high-water detection over `fi_events`,
event loading/paging, and `BuildProjectionDaemonAsync`.

Two SQLite-specific things to think about up front:
- The high-water mark assumes `seq_id` only moves forward. `AUTOINCREMENT` is what guarantees that
  (see CLAUDE.md) — do not weaken it.
- WAL journaling is what lets the daemon read while a session writes. It is on by default via
  `SqlitePragmaSettings.Default`, but a consumer overriding `StoreOptions.PragmaSettings` could turn
  it off and quietly serialize the daemon behind every write.

## Enrollment status

Enrolling is one empty subclass per suite in `Compliance/fisher_event_store_compliance.cs`. Every
suite compiles whether or not it is enrolled — see CLAUDE.md for the two files that cannot compile at
all and are `<Compile Remove>`d.

| Suite | Tests | Status |
|---|---|---|
| `StreamReadCompliance` | 11 | **green** |
| `EventMetadataCompliance` | 9 | **green** |
| `LiveAggregationCompliance` | 7 | **green** |
| `ActivityCorrelationCompliance` | 4 | session correlation seeded from `Activity.Current.RootId` — small, self-contained, no milestone behind it |
| `AutoDiscoveredAggregateCompliance` | 2 | `AllAggregateTypes()` → `ProjectionGraph.DiscoverGeneratedEvolvers`, i.e. projections (2) |
| `FetchForWritingCompliance` | 13 | snapshots + document load-back (1 + 2) |
| `SelfAggregatingEvolveCompliance` | 8 | snapshots + document load-back (1 + 2) |
| `StringIdentitySingleStreamCompliance` | 6 | projection registration + document load-back (1 + 2) |
| `EventProjectionRegistrationCompliance` | 3 | document storage (1) — file excluded from compilation |
| `EventProjectionEnrichmentCompliance` | 3 | document storage (1) — file excluded from compilation |
| `AsyncDaemonCompliance` | 2 | daemon (3) |
| `RebuildConcurrencyCapCompliance` | 5 | `IEventStore` on `DocumentStore` + rebuilds (3) |
| `AssignTagWhereCompliance` | 6 | DCB tags |
| `DcbTagQueryAndConsistencyCompliance` | 26 | DCB tags — the last one, 727 lines |

`ActivityCorrelationCompliance` is the only remaining suite not gated behind a numbered milestone.
The three fixture members it needs (`CorrelationIdFor`, `CausationIdFor`, `SetCorrelationId`) are
already implemented; what is missing is Fisher seeding a session's correlation id from the ambient
`Activity` — there is no `System.Diagnostics.Activity` usage anywhere in Fisher today.

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
