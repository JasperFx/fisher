# Fisher Roadmap

Where Fisher is, what comes next, and why in this order. See [CLAUDE.md](CLAUDE.md) for
architecture and the SQLite-specific decisions.

Status as of the async daemon landing. **All 17 compliance suites green.** 396 tests green on net9.0
and net10.0, **124 of them shared cross-store compliance tests across 17 suites**.

## The destination

**First round of JasperFx compliance tests passing — reached, in full.**
`JasperFx.Events.ComplianceTests` is the shared cross-store suite Marten and Polecat both enroll in;
passing it is what makes Fisher a real Critter Stack event store rather than a lookalike. All
seventeen suites are green:

| Suite | Tests |
|---|---|
| `DcbTagQueryAndConsistencyCompliance` | 26 |
| `FetchForWritingCompliance` | 13 |
| `StreamReadCompliance` | 11 |
| `EventMetadataCompliance` | 9 |
| `SelfAggregatingEvolveCompliance` | 8 |
| `FetchLatestCompliance` | 7 |
| `LiveAggregationCompliance` | 7 |
| `StringIdentitySingleStreamCompliance` | 6 |
| `StreamArchivingCompliance` | 6 |
| `EventStoreExplorerCompliance` | 6 |
| `AssignTagWhereCompliance` | 6 |
| `RebuildConcurrencyCapCompliance` | 5 |
| `ActivityCorrelationCompliance` | 4 |
| `EventProjectionRegistrationCompliance` | 3 |
| `EventProjectionEnrichmentCompliance` | 3 |
| `AsyncDaemonCompliance` | 2 |
| `AutoDiscoveredAggregateCompliance` | 2 |

`AsyncDaemonCompliance` was the last one in, and its two-test count badly understated it: those tests
demand the whole daemon. JasperFx supplies the machinery (~10,500 lines); a store supplies the
storage seam — for Fisher that is `IEventDatabase` on `FisherDatabase`, the high-water detector, the
event loader, the projection batch, and the generic half of `IEventStore<IDocumentSession,
IQuerySession>`.

Being green on all seventeen is not the same as being feature-complete against Marten. The suites
cover what is portable across stores; the deliberate gaps listed in HANDOFF.md are still gaps.

## Filed follow-ups

| Issue | What |
|---|---|
| [fisher#1](https://github.com/JasperFx/fisher/issues/1) | LINQ: ordering and range comparison on date document members. Correctness only — `strftime` normalises inline, no duplicated column needed. |
| [fisher#2](https://github.com/JasperFx/fisher/issues/2) | Duplicated fields, so a query can use an index. The performance follow-on to #1, independent of it. |
| [fisher#3](https://github.com/JasperFx/fisher/issues/3) | An async projection cannot append events of its own — needs version assignment and sequence read-back inside the batch's transaction. |
| [fisher#4](https://github.com/JasperFx/fisher/issues/4) | Projection side effects cannot be published; `GetOrStartMessageSink` throws. |
| ~~[fisher#5](https://github.com/JasperFx/fisher/issues/5)~~ | **Closed.** Dead letter queue — `fi_dead_letters`, so `SkipApplyErrors` quarantines rather than stopping the shard. |
| ~~[fisher#6](https://github.com/JasperFx/fisher/issues/6)~~ | **Closed.** `DeleteAllEventDataAsync` violated the tag tables' foreign key. Found while building #5. |
| ~~[fisher#7](https://github.com/JasperFx/fisher/issues/7)~~ | **Closed.** `WaitForNonStaleProjectionDataAsync` threw `OperationCanceledException` instead of `TimeoutException` when the clock landed mid-query. |

Every deliberate gap gets an issue. A note in this file or in CLAUDE.md is context, not tracking —
if something is deferred, it is in the list above.

## Done

| Milestone | Notes |
|---|---|
| Solution + build infrastructure | net9.0/net10.0, CPM, xUnit v3 on MTP, CI |
| `fi_` schema | `fi_streams`, `fi_events`, `fi_event_progression` via Weasel.Sqlite |
| SQLite dialects over Weasel.Storage | `SqliteStorageDialect<TId>`, `SqliteEventStoreDialect` |
| Sessions + append | `DocumentStore`, `FisherSession` UoW, `EventOperations`, `AppendPlanner` |
| Event store reads | `FetchStreamAsync`, `FetchStreamStateAsync`, `LoadAsync`, archive/un-archive/tombstone |
| Live aggregation | `AggregateStreamAsync` over auto-discovered self-aggregating types |
| Event store write surface | `IEventStoreOperations` in full — `FetchForWriting`, `WriteToAggregate`, `AppendOptimistic`, `FetchLatest`/`ProjectLatest` |
| Compliance enrollment | `FisherComplianceFixture` + 5 suites, 33 shared tests |
| Session metadata | correlation/causation seeded from `Activity.Current`, applied to appended events |
| `StoreOptions.Projections` | thin `ProjectionGraph` — aggregator cache + evolver discovery |
| Document storage | `Store`/`Insert`/`Update`/`Delete`/`LoadAsync`/`LoadManyAsync`, all four id types |
| Inline projections | `Snapshot<T>`, `Add(projection, lifecycle)`, applied in the events' own transaction |
| Hi-Lo sequences | `fi_hilo`, `HiloSequence`, `SequenceFactory` — int/long document identities |
| `EventProjection.storeEntity` | a `Create`/`Project` result is stored in the events' own transaction |
| `Advanced` | `Clean` (`IDocumentCleaner`), `ResetAllDataAsync`, `ResetHiloSequenceFloorAsync<T>` |
| `IEventStore` on `DocumentStore` | explorer reads + `TryCreateUsage`; `EventStoreExplorerCompliance` green |
| Rebuild concurrency cap | `StoreOptions.MaxPoolSize`; `RebuildConcurrencyCapCompliance` green |
| LINQ | `session.Query<T>()` — where, ordering, paging, async terminals |
| DCB tags | tag tables, tagged appends, queries, `AssignTagWhere`, boundaries + consistency, batched queries |
| Async daemon | `IEventDatabase`, high-water detector, event loader, projection batch, `IEventStore<,>`, `BuildProjectionDaemonAsync`, `SnapshotLifecycle.Async` |
| Dead letters | `fi_dead_letters`; `SkipApplyErrors` quarantines a poison event instead of stopping its shard |

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
2.39.4; 2.37.2 → 2.39.1 is where the six newer compliance suites came from, and 2.39.4 changes no
suite source at all.

## Next, in order

Nothing left is unblocking a compliance suite, so ordering is by what a real application would miss
first rather than by test count.

### 1. Finish the daemon's edges

The daemon runs, but two things inside it still throw by name rather than working. Each is a real
capability a Marten user would expect:

- **[fisher#3](https://github.com/JasperFx/fisher/issues/3)** — event-emitting async projections.
- **[fisher#4](https://github.com/JasperFx/fisher/issues/4)** — projection side effects.

Dead letters ([fisher#5](https://github.com/JasperFx/fisher/issues/5)) were the third and are done.

### 2. Finish document storage

Write, load-by-id and LINQ are done for all four identity types. What is left, roughly in value
order: soft delete, duplicated fields and user indexes ([fisher#2](https://github.com/JasperFx/fisher/issues/2)),
hierarchies, numeric revisions — each additive against the existing column shape rather than a
rewrite of it. Ordering and range comparison on a date member is
[fisher#1](https://github.com/JasperFx/fisher/issues/1).

### 3. Projections, the rest

All three lifecycles work, including `EventProjection`s that store arbitrary documents. Still
missing: composite projections, and the side-effect and event-emission paths above.

### 4. DI registration and subscriptions

`AddFisher` has no equivalent yet, so every consumer builds a `DocumentStore` by hand and hosts the
daemon itself. `ISubscriptionRunner` is the other half — Polecat implements it beside its
`IEventStore<,>`, and Fisher's projection batch is already the piece a subscription would commit
through.

## Enrollment status

Enrolling is one empty subclass per suite in `Compliance/fisher_event_store_compliance.cs`. Every
suite compiles whether or not it is enrolled, which is why all four global aliases in
`ComplianceAliases.cs` must resolve even for suites Fisher cannot pass. Nothing is `<Compile Remove>`d
any more.

| Suite | Tests | Status |
|---|---|---|
| `DcbTagQueryAndConsistencyCompliance` | 26 | **green** |
| `FetchForWritingCompliance` | 13 | **green** |
| `StreamReadCompliance` | 11 | **green** |
| `EventMetadataCompliance` | 9 | **green** |
| `SelfAggregatingEvolveCompliance` | 8 | **green** |
| `FetchLatestCompliance` | 7 | **green** |
| `LiveAggregationCompliance` | 7 | **green** |
| `StringIdentitySingleStreamCompliance` | 6 | **green** |
| `StreamArchivingCompliance` | 6 | **green** |
| `EventStoreExplorerCompliance` | 6 | **green** |
| `AssignTagWhereCompliance` | 6 | **green** |
| `RebuildConcurrencyCapCompliance` | 5 | **green** |
| `ActivityCorrelationCompliance` | 4 | **green** |
| `EventProjectionRegistrationCompliance` | 3 | **green** |
| `EventProjectionEnrichmentCompliance` | 3 | **green** |
| `AsyncDaemonCompliance` | 2 | **green** |
| `AutoDiscoveredAggregateCompliance` | 2 | **green** |

Every suite the package ships is enrolled. New suites arriving in a JasperFx bump are the only way
this table grows now — and each one compiles against Fisher whether or not it is enrolled, so a bump
that adds a suite Fisher cannot pass still builds.

## Open items not on the critical path

- **Concurrency regression test.** The append path's safety rests on `BEGIN IMMEDIATE` being what
  `IsolationLevel.Serializable` produces — verified empirically against Microsoft.Data.Sqlite 10.0.9,
  but it is library behaviour Fisher does not own. `append_optimistic_loses_to_a_concurrent_commit`
  now covers the version-guard half (two sessions, one fails cleanly); what is still uncovered is a
  test that would fail if `Serializable` stopped producing `BEGIN IMMEDIATE` — that needs two
  genuinely interleaved writers, not two sequential `SaveChangesAsync` calls.
- **`Advanced` is a thin subset.** `Clean`, `ResetAllDataAsync` and `ResetHiloSequenceFloorAsync<T>`
  only. Marten and Polecat also carry bulk insert, `InitialData` and metadata helpers there.
- **Not started at all:** multi-tenancy beyond a tenant id column, subscriptions, DI registration
  (`AddFisher`), bulk insert, natural keys, strongly typed ids.
- **The daemon's WAL guard is a warning, not a refusal.** `BuildProjectionDaemonAsync` logs when
  `PragmaSettings.JournalMode` is not WAL, because without it the daemon and every writer serialize
  against each other. Refusing to start would be the stronger position; warning is what is there.

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
