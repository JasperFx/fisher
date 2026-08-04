# Fisher Roadmap

Where Fisher is, what comes next, and why in this order. See [CLAUDE.md](CLAUDE.md) for
architecture and the SQLite-specific decisions.

Status as of `ffaa688` + two more compliance suites. 98 tests green on net9.0 and net10.0, 33 of
them shared cross-store suites.

## The destination

**First round of JasperFx compliance tests passing — reached.** `JasperFx.Events.ComplianceTests` is
the shared cross-store suite Marten and Polecat both enroll in; passing it is what makes Fisher a
real Critter Stack event store rather than a lookalike. Five suites are green:

| Suite | Tests |
|---|---|
| `StreamReadCompliance` | 11 |
| `EventMetadataCompliance` | 9 |
| `LiveAggregationCompliance` | 7 |
| `ActivityCorrelationCompliance` | 4 |
| `AutoDiscoveredAggregateCompliance` | 2 |

The destination is now **the rest of them**, and the ordering below is unchanged, because what the
remaining suites need is exactly what the remaining milestones build. Every unenrolled suite is now
blocked on a numbered milestone — there are no loose ends left between here and document storage.
Suite-by-suite blocking is tabulated under "Enrollment status".

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

The scaffolding is up: `IProjection`, `Fisher.Projections.SingleStreamProjection<TDoc, TId>`,
`EventProjection`, and a thin `FisherProjectionOptions : ProjectionGraph<...>` behind
`StoreOptions.Projections` that carries the live aggregator cache and evolver discovery.

What is missing is everything that writes. There is no way to register a projection at all —
`Snapshot<T>` and `AddProjection` are the gap — no Inline/Async lifecycle, and no inline snapshot
application during `SaveChangesAsync`. `FisherSession.FetchProjectionStorageAsync`,
`GetOrStartMessageSink` and `EventProjection.storeEntity` are the `NotImplementedException`s to fill
in.

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
| `ActivityCorrelationCompliance` | 4 | **green** |
| `AutoDiscoveredAggregateCompliance` | 2 | **green** |
| `FetchForWritingCompliance` | 13 | snapshots + document load-back (1 + 2) |
| `SelfAggregatingEvolveCompliance` | 8 | snapshots + document load-back (1 + 2) |
| `StringIdentitySingleStreamCompliance` | 6 | projection registration + document load-back (1 + 2) |
| `EventProjectionRegistrationCompliance` | 3 | document storage (1) — file excluded from compilation |
| `EventProjectionEnrichmentCompliance` | 3 | document storage (1) — file excluded from compilation |
| `AsyncDaemonCompliance` | 2 | daemon (3) |
| `RebuildConcurrencyCapCompliance` | 5 | `IEventStore` on `DocumentStore` + rebuilds (3) |
| `AssignTagWhereCompliance` | 6 | DCB tags |
| `DcbTagQueryAndConsistencyCompliance` | 26 | DCB tags — the last one, 727 lines |

Nothing on that list is a loose end any more: every unenrolled suite needs document storage,
projections, the daemon, or DCB tags. `FetchForWritingCompliance` is the largest single prize at 13
tests, and Fisher already implements the `FetchForWriting` surface it exercises — what it lacks is the
snapshot registration and document load-back the suite asserts against.

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
