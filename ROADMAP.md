# Fisher Roadmap

Where Fisher is, what comes next, and why in this order. See [CLAUDE.md](CLAUDE.md) for
architecture and the SQLite-specific decisions.

Status: **twenty-eight open issues, all enhancements — nothing is broken.** On JasperFx **2.45.0**.
**All 28 compliance suites green.** 743 tests green on net9.0
and net10.0, with **no known intermittents**.

Twenty-eight of those twenty-nine came out of a file-by-file comparison against Polecat on 2026-08-08,
which filed [#22](https://github.com/JasperFx/fisher/issues/22) through
[#50](https://github.com/JasperFx/fisher/issues/50) and is indexed in
[polecat-gaps.md](polecat-gaps.md) — that document also records what SQLite has no equivalent for and
never will. It is context; the issues are the tracking.
[#45](https://github.com/JasperFx/fisher/issues/45) and
[#34](https://github.com/JasperFx/fisher/issues/34) are closed; the rest are being worked one at a time.

## The destination

**First round of JasperFx compliance tests passing — reached, and held through six package bumps
that added ten suites.** `JasperFx.Events.ComplianceTests` is the shared cross-store suite Marten and
Polecat both enroll in; passing it is what makes Fisher a real Critter Stack event store rather than a
lookalike. As of 2.45.0 that library's event sourcing backlog is empty, so all twenty-eight suites it
will ship for the foreseeable future are green:

| Suite | Tests |
|---|---|
| `DcbTagQueryAndConsistencyCompliance` | 26 |
| `StringStreamIdentityCompliance` | 19 |
| `FetchForWritingCompliance` | 13 |
| `StreamReadCompliance` | 11 |
| `StrongTypedIdentityCompliance` | 11 |
| `StreamCompactingCompliance` | 11 |
| `RebuildAndCatchUpCompliance` | 11 |
| `MultiStreamProjectionCompliance` | 10 |
| `EventDataMaskingCompliance` | 10 |
| `EventMetadataCompliance` | 9 |
| `SelfAggregatingEvolveCompliance` | 8 |
| `FlatTableProjectionCompliance` | 8 |
| `ConjoinedEventTenancyCompliance` | 8 |
| `FetchLatestCompliance` | 7 |
| `LiveAggregationCompliance` | 7 |
| `StringIdentitySingleStreamCompliance` | 6 |
| `StreamArchivingCompliance` | 6 |
| `SnapshotLifecycleCompliance` | 6 |
| `EventStoreExplorerCompliance` | 6 |
| `AssignTagWhereCompliance` | 6 |
| `DeadLetterCompliance` | 6 |
| `SubscriptionCompliance` | 6 |
| `RebuildConcurrencyCapCompliance` | 5 |
| `ActivityCorrelationCompliance` | 4 |
| `EventProjectionRegistrationCompliance` | 3 |
| `EventProjectionEnrichmentCompliance` | 3 |
| `AsyncDaemonCompliance` | 2 |
| `AutoDiscoveredAggregateCompliance` | 2 |

**Eight of the ten suites added since 2.39.5 went green on the version bump alone** —
`StringStreamIdentityCompliance`, `SnapshotLifecycleCompliance`, `EventDataMaskingCompliance`,
`StreamCompactingCompliance`, both of 2.44.0's, and both of 2.45.0's. That is the useful signal, and
it is a direct dividend of mirroring Polecat's internals: each of those features was built from the
sibling's shape before a shared suite existed to check it, and the suite arriving green is what turns
"ported faithfully" into a fact. `MultiStreamProjectionCompliance` cost one file;
`FlatTableProjectionCompliance` and `StrongTypedIdentityCompliance` cost real features.

Test counts keep understating the suites that matter. `AsyncDaemonCompliance` is two tests that demand
the whole daemon; `FlatTableProjectionCompliance` is eight that demand an upsert generator, a
migration hook and rebuild teardown.

Being green on all twenty-eight is not the same as being feature-complete against Marten. The suites
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
| ~~[fisher#13](https://github.com/JasperFx/fisher/issues/13)~~ | **Closed.** The session's operation queue was an unguarded `List<T>`, and the daemon queues onto one session from several threads. One slice's write was silently lost. |
| ~~[fisher#14](https://github.com/JasperFx/fisher/issues/14)~~ | **Closed.** Strong-typed identities — no new seam needed; `IIdentification` already reserved the three members, and `DocumentIdentity.FindIdMember`'s predicate overload was the entry point. |
| ~~[fisher#11](https://github.com/JasperFx/fisher/issues/11)~~ | **Closed.** Document metadata member mapping — four of the five columns projected back onto members, by interface, attribute or DSL. `dotnet_type` is the fifth and has no member slot in Weasel's binder. |
| ~~[fisher#10](https://github.com/JasperFx/fisher/issues/10)~~ | **Closed.** Stream compacting, at both levels — the untyped `IEventStore` entry point resolves the aggregate from `fi_streams` rather than throwing as Polecat's does. |
| ~~[fisher#15](https://github.com/JasperFx/fisher/issues/15)~~ | **Closed.** `OpenReadOnlyEventStore` — the stated blocker was stale; `EventQuery` is flat exact-match filters plus paging, so it cost a where clause, `limit`/`offset` and a `count(*)`. Nothing on `IEventStore` throws any more. |
| ~~[fisher#16](https://github.com/JasperFx/fisher/issues/16)~~ | **Closed.** User-declared indexes, as SQLite expression indexes over the member's `TypedLocator` — no column materialised, so cheaper here than on either sibling. |
| ~~[fisher#17](https://github.com/JasperFx/fisher/issues/17)~~ | **Closed.** Document hierarchies on a `doc_type` alias column. This issue's premise was wrong: `dotnet_type` cannot be the discriminator, so it needed a schema change after all. |
| ~~[fisher#18](https://github.com/JasperFx/fisher/issues/18)~~ | **Closed.** Numeric revisions, following Marten's strictly-greater rule. The difficulty was the positional slot contract, not the SQL. |
| [fisher#19](https://github.com/JasperFx/fisher/issues/19) | **Open.** `CompositeProjection` — the one projection shape Fisher does not support. Possibly close to free, but nobody has tried it. |
| [#22–#50](https://github.com/JasperFx/fisher/issues/22) | **In progress**, #45 closed. The Polecat comparison backlog — LINQ, sessions, document storage, the event store's remaining surface, and two satellite packages. Indexed with rationale in [polecat-gaps.md](polecat-gaps.md); listed individually below rather than repeated here. |
| ~~[fisher#20](https://github.com/JasperFx/fisher/issues/20)~~ | **Closed.** `AddFisher(...)`, scoped sessions, hosted services. Surfaced a real bug: everything a container disposes was `IAsyncDisposable` only, which made a scoped session unusable. |
| ~~[fisher#21](https://github.com/JasperFx/fisher/issues/21)~~ | **Closed.** Subscriptions — `ISubscriptionRunner<ISubscription>`, the session taken from the batch so writes commit with the progression row. |

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
| Event rewriting | `Events/Protected/` — overwrite, replace and delete-by-sequence; emptied `EventOperations.Unsupported.cs`, which is now deleted |
| Event data masking (fisher#9) | `Advanced.ApplyEventDataMaskingAsync`, masking rules on the event graph, and `QueryEventsAsync` for the predicate selector |
| The rebuild flake (fisher#13) | the session's operation queue is guarded; an unsynchronised `List<T>.Add` was silently losing a projection slice's write |
| Strong-typed identities (fisher#14) | wrapper ids on aggregates and documents; `LoadAsync<T, TId>`; the last unenrolled compliance suite |
| Stream compacting (fisher#10) | `CompactStreamAsync<T>` + the untyped `IEventStore` overload; reads back free, because JasperFx's aggregator fast-forwards a `Compacted<T>` |
| JasperFx 2.43.0 | `EventDataMaskingCompliance` and `StreamCompactingCompliance` enrolled, both green on the bump; three seam members, no production change; `EventOperations.Unsupported.cs` emptied and deleted |
| JasperFx 2.44.0 | `RebuildAndCatchUpCompliance` and `DeadLetterCompliance` enrolled, both green on the bump; **no seam members and no production change** |
| JasperFx 2.45.0 | `ConjoinedEventTenancyCompliance` and `SubscriptionCompliance` enrolled, both green on the bump; two seam members and one partial, **no production change**. Empties the upstream ES compliance backlog |
| `IDocumentStore` (fisher#45) | the store's own API as an interface; tooling surfaces stay explicit, and the surface is reflection-pinned in both directions |
| Raw SQL (fisher#34) | `QueueSqlCommand` in the unit of work, `session.AdvancedSql` for typed reads, and the three parameter conversions SQLite needs |

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
portable in the compliance suites runs through. What was not implemented was collected in
`EventOperations.Unsupported.cs` (DCB tags, then event rewriting) rather than scattered, so the file
shrinking measured progress; it reached zero and was deleted. Two open decisions came out of it:

- **Exclusive appends are the optimistic ones.** SQLite has no row lock; documented in CLAUDE.md's
  divergence table with what revisiting it would cost. Still open.
- **`AllAggregateTypes()` had no assembly scan.** Settled by standing up a thin
  `StoreOptions.Projections`, which was cheap only because the write-surface work had already made
  `IDocumentSession` an `IStorageOperations` — the constraint `ProjectionGraph` imposes. Fisher gets
  `DiscoverGeneratedEvolvers` from the framework rather than reimplementing it, which is what the
  earlier deferral was waiting for.

The `FisherCommandBuilder` shim is gone: weasel#424 shipped in Weasel.Sqlite 9.23.2. JasperFx is on
**2.45.0** and Weasel on 9.23.2, both current. 2.40.0 and 2.41.0 are where four of the newest
compliance suites came from. 2.41.0 also lifted `CompactStreamAsync<T>` onto `IEventStoreOperations`
and `IEventDataMasking` into `JasperFx.Events/Protected/`; the upstream note warns consumers that
adopting the first is not optional, but that applies to Marten and Polecat, which declared the member
themselves and would go ambiguous. **Fisher never did**, so the bump needed no change and the
default-implemented throw was the right behaviour until fisher#10 made it real.

The 2.42.2 bump cost exactly one line of Fisher: `IComplianceStoreRegistrar` gained
`RegisterValueType<TValue>()` for the new `StrongTypedIdentityCompliance` suite. It briefly threw,
because Fisher discovered nothing to register; fisher#14 made it the **no-op** the interface intends,
which is the right implementation for a store that resolves value types from their shape — Polecat
derives the same information from `ValueTypeInfo` when it builds its document mapping, and Fisher now
does too.

The 2.43.0 bump cost **three seam members and no production change at all**. It brought
`EventDataMaskingCompliance` and `StreamCompactingCompliance`, and both went green immediately:
compacting needed nothing, since 2.41.0's lift already put the member on the shared operations
surface, and masking needed only `ApplyEventDataMaskingAsync` on the fixture plus the two
`AddMaskingRule` overloads on the registrar — `IEventDataMasking` is shared, but the `Advanced`
surface that hands one out is not, on any of the three stores. The core `JasperFx` and
`JasperFx.Events` assemblies did not change; 2.43.0 is a compliance-tests release. The pass also
emptied and removed `EventOperations.Unsupported.cs`, which had been down to a single unreferenced
constant.

The 2.44.0 bump cost **nothing at all** — the first bump to add suites and require no Fisher change
whatever, not even a seam member. `RebuildAndCatchUpCompliance` (11) and `DeadLetterCompliance` (6)
were both filed upstream as needing a seam addition and both turned out not to: the rebuild surface
is already declared on `IProjectionDaemon`, and the dead letter path runs through
`IEventStore<,>.ContinuousErrors` and `IEventDatabase.QueryDeadLetterEventsAsync`, which Fisher
implements because the daemon needs them anyway. Like 2.43.0 this is a compliance-tests-only release;
the core assemblies are unchanged from 2.43.0, which is one commit behind it. The rebuild suite's
teardown test is the one with teeth — see CLAUDE.md's "Compliance suites" for why, and for the
unreproduced upstream intermittent that has not been seen here.

The 2.45.0 bump brought `ConjoinedEventTenancyCompliance` (8) and `SubscriptionCompliance` (6), and
**both went green on the bump with no production change** — 14 tests, at the cost of two seam members
and one small partial. It is the more interesting of the two recent waves for a reason the counts hide:
`ConjoinedEventTenancyCompliance` is the **first suite to check a Fisher feature that had never had a
cross-store test at all.** Conjoined event tenancy was built as part of the original schema work,
`StreamsTable` and `EventsTable` have keyed on `(tenant_id, id)` since then, and until now the only
thing holding it was Fisher's own `event_store_schema_creation`. The suite checks the property that
actually matters and that a schema test cannot see — *isolation*, in both directions, over a stream id
deliberately reused across two tenants. See HANDOFF.md for why that is worth more than it sounds.

**With 2.45.0 the upstream event sourcing compliance backlog is empty.** Twenty-eight suites is where
the library sits until someone files a new one, so "keep up with the suites" stops being a recurring
task and reverts to watching for releases.

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
What is left, roughly in value order: user-declared indexes over unduplicated members
([#16](https://github.com/JasperFx/fisher/issues/16)), hierarchies
([#17](https://github.com/JasperFx/fisher/issues/17)), numeric revisions
([#18](https://github.com/JasperFx/fisher/issues/18)).

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
table. Still missing: composite projections
([#19](https://github.com/JasperFx/fisher/issues/19)), which may be close to free — `ProjectionGraph`
already discovers them and Fisher derives from it — but nobody has tried it. Delivery behind the
side-effect seam is deliberately absent — see step 1.

### 4. DI registration and subscriptions

`AddFisher` has no equivalent yet ([#20](https://github.com/JasperFx/fisher/issues/20)), so every
consumer builds a `DocumentStore` by hand and hosts the daemon itself. `ISubscriptionRunner` is the
other half ([#21](https://github.com/JasperFx/fisher/issues/21)) — Polecat implements it beside its
`IEventStore<,>`, and Fisher's projection batch is already the piece a subscription would commit
through. The two are independently deliverable; #20 is worth strictly more on its own.

### 5. `OpenReadOnlyEventStore`, the last throwing member

Small, and smaller than its own doc comment claims
([#15](https://github.com/JasperFx/fisher/issues/15)). That comment says the blocker is a paged event
query layer Fisher does not have; fisher#9 built it. `EventQuery` is a flat bag of exact-match filters
plus paging rather than an expression, and `EventOperations.QueryEventsAsync` already does the
cross-stream read. Listed last because no suite and no application is waiting on it — CritterWatch's
Event Explorer is.

### 6. The Polecat comparison backlog (#22–#50)

Steps 1–5 are done. This is what replaces them, and the ordering is by what an application hits first
rather than by size. Full rationale per issue; [polecat-gaps.md](polecat-gaps.md) is the index.

**First — the surfaces whose absence is a hard stop rather than an inconvenience.**

| | Why first |
|---|---|
| [#30](https://github.com/JasperFx/fisher/issues/30) sessions and `SessionOptions` | The enlistment half: one writer per file means an application writing its own tables and Fisher's in the same file cannot do both atomically today. `QuerySession()` on the store is a one-liner alongside it. |
| ~~[#34](https://github.com/JasperFx/fisher/issues/34) `QueueSqlCommand`~~ | **Done**, with `IAdvancedSql` alongside it. The port was the small half; the work was `SqliteParameterValue`, which has no sibling to port from — raw SQL is the one path with no conversion between the caller's value and what Fisher stored, and Guid, timestamp and decimal each bind to something that matches nothing. |
| ~~[#45](https://github.com/JasperFx/fisher/issues/45) `IDocumentStore`~~ | **Done.** Eight public members, extracted and pinned by reflection in both directions. [#46](https://github.com/JasperFx/fisher/issues/46) and [#49](https://github.com/JasperFx/fisher/issues/49) are unblocked. |

**Then LINQ**, in dependency order — [#22](https://github.com/JasperFx/fisher/issues/22) aggregates
(no new SQL shape; `CountAsync` already proves the pattern), then
[#23](https://github.com/JasperFx/fisher/issues/23) `Select` projections, then
[#24](https://github.com/JasperFx/fisher/issues/24) `GroupBy` on top of both.
[#26](https://github.com/JasperFx/fisher/issues/26)'s marker operators are independent and mostly
small. [#25](https://github.com/JasperFx/fisher/issues/25) joins and
[#27](https://github.com/JasperFx/fisher/issues/27) cursor paging are the two places SQLite is the
*easiest* of the three dialects, which is unusual enough to be worth spending on.

**Then the document features with the best ratio.**
[#35](https://github.com/JasperFx/fisher/issues/35) patching is the strongest single case in the whole
backlog — every operation is one json1 function in one statement, with no server function to install,
and duplicated fields follow a patch for free because fisher#2 made them generated columns.
[#29](https://github.com/JasperFx/fisher/issues/29) metadata is the document-side counterpart of
something the event store already does (correlation and causation reach events with no application
code; documents in the same transaction get none of it).
[#36](https://github.com/JasperFx/fisher/issues/36) bulk insert needs no bulk-copy protocol.

**Then the event store's remainder** — [#40](https://github.com/JasperFx/fisher/issues/40) natural
keys, which closes the last stated partial on `IEventStoreOperations`, and
[#41](https://github.com/JasperFx/fisher/issues/41), whose stated blocker ("nothing to resolve a path
against") holds for `IEvent` and not for a query that names the event type.

**[#47](https://github.com/JasperFx/fisher/issues/47), database-per-tenant, is the biggest and is
deliberately not last.** A tenant is a file, so provisioning is `File.Create` and deletion is deleting
a file — and tenants get separate write locks, which is the only way a multi-tenant Fisher application
scales writes. It is staged in three parts inside the issue; stage 1 is most of the work and is
mechanical.

**Last, and honestly optional:** [#43](https://github.com/JasperFx/fisher/issues/43) binary events,
[#48](https://github.com/JasperFx/fisher/issues/48) OpenTelemetry (though the retry-event half of it
answers a question nothing else can — a slow request that spent its time waiting for the write lock
looks like a slow request), and the two satellite packages
([#49](https://github.com/JasperFx/fisher/issues/49),
[#50](https://github.com/JasperFx/fisher/issues/50)).

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
  only. Now filed: bulk insert ([#36](https://github.com/JasperFx/fisher/issues/36)), `InitialData`
  ([#39](https://github.com/JasperFx/fisher/issues/39)), and the rest of the surface
  ([#42](https://github.com/JasperFx/fisher/issues/42)).
- **Tenancy beyond the conjoined style.** Conjoined works and is suite-pinned; database-per-tenant is
  [#47](https://github.com/JasperFx/fisher/issues/47), and it is the item where SQLite's single-writer
  constraint turns into the argument *for* the feature rather than against it.
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
