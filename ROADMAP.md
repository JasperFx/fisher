# Fisher Roadmap

Where Fisher is, what comes next, and why in this order. See [CLAUDE.md](CLAUDE.md) for
architecture and the SQLite-specific decisions.

Status as of **strong-typed identities** (fisher#14), on JasperFx **2.42.2**.
**All 22 compliance suites green.** 565 tests green on net9.0
and net10.0 (one intermittent — fisher#13), **167 of them shared cross-store compliance tests across
21 suites**.

## The destination

**First round of JasperFx compliance tests passing — reached, and held through two package bumps
that added four suites.** `JasperFx.Events.ComplianceTests` is the shared cross-store suite Marten and
Polecat both enroll in; passing it is what makes Fisher a real Critter Stack event store rather than a
lookalike. All twenty-two are green, `StrongTypedIdentityCompliance` included:

| Suite | Tests |
|---|---|
| `DcbTagQueryAndConsistencyCompliance` | 26 |
| `StringStreamIdentityCompliance` | 19 |
| `FetchForWritingCompliance` | 13 |
| `StreamReadCompliance` | 11 |
| `MultiStreamProjectionCompliance` | 10 |
| `EventMetadataCompliance` | 9 |
| `SelfAggregatingEvolveCompliance` | 8 |
| `FlatTableProjectionCompliance` | 8 |
| `FetchLatestCompliance` | 7 |
| `LiveAggregationCompliance` | 7 |
| `StringIdentitySingleStreamCompliance` | 6 |
| `StreamArchivingCompliance` | 6 |
| `SnapshotLifecycleCompliance` | 6 |
| `EventStoreExplorerCompliance` | 6 |
| `AssignTagWhereCompliance` | 6 |
| `RebuildConcurrencyCapCompliance` | 5 |
| `ActivityCorrelationCompliance` | 4 |
| `EventProjectionRegistrationCompliance` | 3 |
| `EventProjectionEnrichmentCompliance` | 3 |
| `AsyncDaemonCompliance` | 2 |
| `AutoDiscoveredAggregateCompliance` | 2 |
| `StrongTypedIdentityCompliance` | 11 |

Two of the four new suites — `StringStreamIdentityCompliance` and `SnapshotLifecycleCompliance` — went
green on the version bump alone, which is the useful signal: string stream identity and the
inline/async snapshot equivalence were already built to the shape the shared suite expects.
`MultiStreamProjectionCompliance` cost one file. `FlatTableProjectionCompliance` cost a real feature,
and is the only one that did.

Test counts keep understating the suites that matter. `AsyncDaemonCompliance` is two tests that demand
the whole daemon; `FlatTableProjectionCompliance` is eight that demand an upsert generator, a
migration hook and rebuild teardown.

Being green on all twenty-one is not the same as being feature-complete against Marten. The suites
cover what is portable across stores; the deliberate gaps listed in HANDOFF.md are still gaps.

## Filed follow-ups

| Issue | What |
|---|---|
| ~~[fisher#1](https://github.com/JasperFx/fisher/issues/1)~~ | **Closed.** LINQ ordering and range comparison on date document members — `strftime` normalises inline, no duplicated column needed, exactly as predicted. |
| ~~[fisher#2](https://github.com/JasperFx/fisher/issues/2)~~ | **Closed.** Duplicated fields, as indexed SQLite `VIRTUAL` generated columns — nothing writes them, so they cannot drift from `data` and need no backfill. The generated-column shape this file predicted was the right one. |
| [weasel#426](https://github.com/JasperFx/weasel/issues/426) | Upstream. `pragma_table_info` omits generated columns, so a Weasel.Sqlite table carrying one never converges. Fisher works around it in `DocumentTable`; the override goes when this ships. Found while building #2. |
| ~~[fisher#3](https://github.com/JasperFx/fisher/issues/3)~~ | **Closed.** Event-emitting async projections — raised events are planned and appended inside the batch's transaction. |
| ~~[fisher#4](https://github.com/JasperFx/fisher/issues/4)~~ | **Closed.** Projection side effects — `IMessageOutbox` / `IMessageBatch`, both commit paths bracketed. |
| ~~[fisher#8](https://github.com/JasperFx/fisher/issues/8)~~ | **Closed wontfix.** A built-in outbox is not Fisher's job — delivery is a bus integration's, as on both siblings. `NulloMessageOutbox` is the intended end state and is documented as one. |
| ~~[fisher#5](https://github.com/JasperFx/fisher/issues/5)~~ | **Closed.** Dead letter queue — `fi_dead_letters`, so `SkipApplyErrors` quarantines rather than stopping the shard. |
| ~~[fisher#6](https://github.com/JasperFx/fisher/issues/6)~~ | **Closed.** `DeleteAllEventDataAsync` violated the tag tables' foreign key. Found while building #5. |
| ~~[fisher#7](https://github.com/JasperFx/fisher/issues/7)~~ | **Closed.** `WaitForNonStaleProjectionDataAsync` threw `OperationCanceledException` instead of `TimeoutException` when the clock landed mid-query. |
| ~~[fisher#9](https://github.com/JasperFx/fisher/issues/9)~~ | **Closed.** Event data masking — `Advanced.ApplyEventDataMaskingAsync`, over the rule registry ported from Polecat's `EventGraph` (the registry was never lifted into JasperFx, only the request shape). |
| ~~[fisher#12](https://github.com/JasperFx/fisher/issues/12)~~ | **Closed.** A retried projection batch silently dropped its document writes and committed the progression row anyway. Found while investigating #13. |
| [fisher#13](https://github.com/JasperFx/fisher/issues/13) | Open, not understood. `a_rebuild_reproduces_the_grouping` fails ~1 full-suite run in 5 on net9.0; predates the current work. 12 clean runs after the 2.42.2 bump, which is *not* evidence — instrumentation produced the same 12 without fixing anything. |
| ~~[fisher#14](https://github.com/JasperFx/fisher/issues/14)~~ | **Closed.** Strong-typed identities — no new seam needed; `IIdentification` already reserved the three members, and `DocumentIdentity.FindIdMember`'s predicate overload was the entry point. |
| ~~[fisher#11](https://github.com/JasperFx/fisher/issues/11)~~ | **Closed.** Document metadata member mapping — four of the five columns projected back onto members, by interface, attribute or DSL. `dotnet_type` is the fifth and has no member slot in Weasel's binder. |
| ~~[fisher#10](https://github.com/JasperFx/fisher/issues/10)~~ | **Closed.** Stream compacting, at both levels — the untyped `IEventStore` entry point resolves the aggregate from `fi_streams` rather than throwing as Polecat's does. |

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
| Projection side effects | `IMessageOutbox` / `IMessageBatch`; both commit paths bracket their transaction |
| Event-emitting projections | raised events planned and appended inside the projection batch's transaction |
| Multi-stream projections | `MultiStreamProjection<TDoc, TId>` — `Identity`/`Identities`/`FanOut`, inline and async |
| Flat-table projections | `Projections/Flattened/` — upsert generator, migration-created table, rebuild teardown |
| LINQ date ordering (fisher#1) | `TimestampMember` normalises through `strftime`; string-stored enums now refuse instead |
| Soft delete | `is_deleted` / `deleted_at`, `HardDelete`, the three `*Where` operations, and the four query operators |
| Duplicated fields (fisher#2) | `Duplicate(x => x.Name)` as an indexed `VIRTUAL` generated column; `Schema.For<T>()` now returns a typed expression |
| Metadata member mapping (fisher#11) | four columns projected back onto members, by interface, attribute or `Metadata(...)`; `IVersioned` now turns optimistic concurrency on |
| Event rewriting | `Events/Protected/` — overwrite, replace and delete-by-sequence; `EventOperations.Unsupported.cs` is down to the DCB tag members |
| Event data masking (fisher#9) | `Advanced.ApplyEventDataMaskingAsync`, masking rules on the event graph, and `QueryEventsAsync` for the predicate selector |
| Strong-typed identities (fisher#14) | wrapper ids on aggregates and documents; `LoadAsync<T, TId>`; the last unenrolled compliance suite |
| Stream compacting (fisher#10) | `CompactStreamAsync<T>` + the untyped `IEventStore` overload; reads back free, because JasperFx's aggregator fast-forwards a `Compacted<T>` |

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
**2.42.2** and Weasel on 9.23.2, which is current. 2.40.0 and 2.41.0 are where four of the newest
compliance suites came from. 2.41.0 also lifted `CompactStreamAsync<T>` onto `IEventStoreOperations`
and `IEventDataMasking` into `JasperFx.Events/Protected/`; the upstream note warns consumers that
adopting the first is not optional, but that applies to Marten and Polecat, which declared the member
themselves and would go ambiguous. **Fisher never did**, so the bump needed no change and the
default-implemented throw is the right behaviour here.

The 2.42.2 bump cost exactly one line of Fisher: `IComplianceStoreRegistrar` gained
`RegisterValueType<TValue>()` for the new `StrongTypedIdentityCompliance` suite. It briefly threw,
because Fisher discovered nothing to register; fisher#14 made it the **no-op** the interface intends,
which is the right implementation for a store that resolves value types from their shape — Polecat
derives the same information from `ValueTypeInfo` when it builds its document mapping, and Fisher now
does too.

## Next, in order

Nothing left is unblocking a compliance suite, so ordering is by what a real application would miss
first rather than by test count.

### 1. Messaging — settled, nothing to build

**Nothing in the daemon throws any more.** Event-emitting projections
([fisher#3](https://github.com/JasperFx/fisher/issues/3)), side effects
([fisher#4](https://github.com/JasperFx/fisher/issues/4)) and dead letters
([fisher#5](https://github.com/JasperFx/fisher/issues/5)) are all built.

[fisher#8](https://github.com/JasperFx/fisher/issues/8) asked whether Fisher should ship a delivery
mechanism behind the side-effect seam, given that "add a broker" is a much bigger ask for an embedded
single-file store than for a Marten or Polecat application that already runs a database server.
**Closed wontfix.** Delivery is a bus integration's job here as it is on both siblings, and a
Fisher-only outbox subsystem — drainer, retry policy, poison handling, concurrent-drainer
coordination — would be a surface projection code could not port to the siblings. `NulloMessageOutbox`
is the intended end state; "a published side effect goes nowhere until an `IMessageOutbox` is
supplied" is a contract, documented in CLAUDE.md, rather than a gap.

### 2. Finish document storage

Write, load-by-id and LINQ are done for all four identity types, LINQ orders and range-compares
timestamps correctly ([fisher#1](https://github.com/JasperFx/fisher/issues/1), closed), **soft delete
is done**, and **duplicated fields are done** ([fisher#2](https://github.com/JasperFx/fisher/issues/2),
closed) — all of it additive against the existing column shape exactly as predicted, and the
duplicated columns being generated meant the write path did not change at all. **Metadata member
mapping is done** ([fisher#11](https://github.com/JasperFx/fisher/issues/11), closed), and it too
touched no write path — mapping only decides whether a column that was always written comes back out.
What is left, roughly in value order: user-declared indexes over unduplicated members, hierarchies,
numeric revisions.

These are the first features no compliance suite asks for, which is the useful signal about what comes
next: the shared suites are event-store suites, so everything remaining in document storage is pinned
by Fisher's own tests and judged against Marten's behaviour rather than against a scoreboard.

#2 is the one that changed character. It was "so a query can use an index" in general; it is now also
the answer to the specific cost fisher#1 introduced, because `strftime` over `json_extract` is computed
per row. A **generated column** may be the better shape than a written one — SQLite indexes `VIRTUAL`
generated columns, so the duplication costs index space but not row space and cannot drift from `data`.

### 3. Projections, the rest

All three lifecycles work across all four projection shapes: self-aggregating snapshots,
`EventProjection`s that store arbitrary documents, `MultiStreamProjection<TDoc, TId>` with
`Identity`/`Identities`/`FanOut` grouping, and `FlatTableProjection` writing into a plain relational
table. Still missing: composite projections. Delivery behind the side-effect seam is deliberately
absent — see step 1.

### 4. DI registration and subscriptions

`AddFisher` has no equivalent yet, so every consumer builds a `DocumentStore` by hand and hosts the
daemon itself. `ISubscriptionRunner` is the other half — Polecat implements it beside its
`IEventStore<,>`, and Fisher's projection batch is already the piece a subscription would commit
through.

## Enrollment status

Enrolling is one empty subclass per suite in `Compliance/fisher_event_store_compliance.cs`. Every
suite compiles whether or not it is enrolled, which is why every global alias in
`ComplianceAliases.cs` must resolve even for suites Fisher cannot pass. Nothing is `<Compile Remove>`d
any more.

| Suite | Tests | Status |
|---|---|---|
| `DcbTagQueryAndConsistencyCompliance` | 26 | **green** |
| `StringStreamIdentityCompliance` | 19 | **green** |
| `FetchForWritingCompliance` | 13 | **green** |
| `StreamReadCompliance` | 11 | **green** |
| `MultiStreamProjectionCompliance` | 10 | **green** |
| `EventMetadataCompliance` | 9 | **green** |
| `SelfAggregatingEvolveCompliance` | 8 | **green** |
| `FlatTableProjectionCompliance` | 8 | **green** |
| `FetchLatestCompliance` | 7 | **green** |
| `LiveAggregationCompliance` | 7 | **green** |
| `StringIdentitySingleStreamCompliance` | 6 | **green** |
| `StreamArchivingCompliance` | 6 | **green** |
| `SnapshotLifecycleCompliance` | 6 | **green** |
| `EventStoreExplorerCompliance` | 6 | **green** |
| `AssignTagWhereCompliance` | 6 | **green** |
| `RebuildConcurrencyCapCompliance` | 5 | **green** |
| `ActivityCorrelationCompliance` | 4 | **green** |
| `EventProjectionRegistrationCompliance` | 3 | **green** |
| `EventProjectionEnrichmentCompliance` | 3 | **green** |
| `AsyncDaemonCompliance` | 2 | **green** |
| `AutoDiscoveredAggregateCompliance` | 2 | **green** |
| `StrongTypedIdentityCompliance` | 11 | **green** |

Every suite the package ships is enrolled, and `StrongTypedIdentityCompliance` — briefly the only one
Fisher could not pass — went green in fisher#14. It has no capability flag to decline with, the way
`SupportsFlatTableProjections` lets a store enroll ahead of the feature, so a store either passes it or
leaves it out.

New suites arriving in a JasperFx bump are the only way this table grows now.

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
  (`AddFisher`), bulk insert, natural keys, user-declared indexes over unduplicated members.
- **`dotnet_type` cannot be mapped onto a member**, because Weasel's `DocumentDotNetTypeBinder` takes
  no member where every other document metadata binder does. Upstream gap, found while building
  fisher#11; worth a Weasel issue if anything ever needs to read it.
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
