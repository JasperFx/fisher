# Fisher Roadmap

Where Fisher is, what comes next, and why in this order. See [CLAUDE.md](CLAUDE.md) for
architecture and the SQLite-specific decisions.

Status: **two open issues, neither of them next-release work.**
[#109](https://github.com/JasperFx/fisher/issues/109) is the downstream half of
[jasperfx#684](https://github.com/JasperFx/jasperfx/issues/684), an epic rather than a next-release
item; it is filed so the Fisher half is not rediscovered later rather than because it is actionable
now. [#122](https://github.com/JasperFx/fisher/issues/122) is a rebuild that clears the rows of a
*second* projection publishing the same document type — by design given per-projection teardown, and
Marten behaves identically, so what is wanted is a warning and a docs note rather than a change to
what teardown means.

**1.0.3** is two bugs found by one Marten→Fisher migration, both of which answered a question
wrongly rather than failing.

[#119](https://github.com/JasperFx/fisher/pull/119) is the sharper one: an inline projection read
`IEvent.Timestamp` as `DateTimeOffset.MinValue`, so every read model recording it baked in a
year-0001 date — and because replay reads the column while the envelope was only hydrated on read
paths, **the same projection produced different documents inline and rebuilt**. Silent until somebody
looked at a date. `AppendPlanner.ApplySessionMetadata` now stamps the timestamp from
`EventGraph.TimeProvider` and `FisherQuickAppendEventsOperation` persists that value instead of the
column default; both halves are needed, since stamping alone leaves the two views a clock apart. This
is the cost CLAUDE.md already predicted for not being able to use `StreamAction.PrepareEvents` coming
due, and it carries a trade now written down there: with inline projections registered the reading is
taken outside the write lock, so `fi_events.timestamp` is no longer strictly monotonic with `seq_id`
across streams. Same-stream ordering is untouched, Marten resolves it identically, and it cannot be
had both ways. Reported and fixed by @kebin.

[#120](https://github.com/JasperFx/fisher/issues/120) is `projections list` answering "No projections
in this store." for a store with twenty of them, and `projections rebuild` matching none:
`TryCreateUsage` never populated `usage.Subscriptions`, which is what both commands read. The fix is
one line — `Options.Projections.Describe(usage, this)`, already implemented upstream and already
satisfied by Fisher's own two projection source types — and that is the uncomfortable part rather
than the reassuring one, because nothing about the omission was dialect-specific and so nothing
prompted anybody to look. Every member of `EventStoreUsage` is a list or nullable starting empty, so
an unfilled slot reads as *the store has none* rather than as "not described"; the whole descriptor
was therefore audited alongside the reported member. `RegisteredEventTypes`, `TagTypes` /
`DcbTagTypes`, both projection error policies, `EventMetadata` and `MaxEventSequence` are now
populated too. That last one means something different here than on either sibling: the gap between
it and the high-water mark is what CritterWatch#150's second signal renders, and on Fisher there can
never be one, because one writer per file plus `BEGIN IMMEDIATE` makes committed sequences
contiguous.

Neither was catchable by the shared compliance suite, which asserts on `usage.Events` alone and has
no coverage of envelope metadata inside an inline projection —
[jasperfx#700](https://github.com/JasperFx/jasperfx/issues/700) is filed for the first half of that.

**1.0.2** is Weasel **9.25.1**, and it exists because of what the 9.25.0 bump removed rather than
what 9.25.1 adds. Fisher carried a `DocumentTable.ConfigureQueryCommand` override for
[weasel#426](https://github.com/JasperFx/weasel/issues/426), which had shipped in 9.24.0 — so the
override was a release stale, and 9.25.0 then added a fifth statement (triggers) to the metadata query
it copies. The override and the reader that consumes it are one contract, so a consumer on 1.0.1 who
took Weasel 9.25.0 transitively got `ArgumentOutOfRangeException` from `readForeignKeysAsync` on any
document type with a foreign key. Removed on `main`, released here.

9.25.1 rather than 9.25.0 because upstream's own upgrade page now leads with a warning to skip
9.25.0: a length rule on local identifiers refused conventional schemas whose primary key constraint
names were merely long (weasel#485, weasel#486), and the quiet half aborted a projection's schema
application so the daemon then failed against storage that had never been created. **Fisher itself is
not exposed to that** — verified rather than assumed, by applying a schema with a ~140-character index
name on both 9.25.0 and 9.25.1: Fisher emits an inline `PRIMARY KEY` with no named constraint, so the
rule has nothing of Fisher's to reject. Taking 9.25.1 is still right, because a Fisher application's
*own* Weasel-managed schema is not Fisher's to vouch for.

**1.0.1** is the JasperFx **2.53.0** bump and two fixes it turned up.
[#108](https://github.com/JasperFx/fisher/issues/108) is the reason for the bump:
[jasperfx#683](https://github.com/JasperFx/jasperfx/issues/683) adds
`IProjectionStorage.IsThreadSafe`, and `EfCoreProjectionStorage` returns false, so the daemon applies
a range's slices one at a time rather than through `AggregationRunner`'s ten-wide block. A
`DbContext` is not thread-safe and the storage's own lock does not close it — a lock serializes each
individual call, but between two calls one thread's aggregation mutates entities that another
thread's `Entry()` is running `DetectChanges` over. Two things were measured rather than assumed:
Fisher genuinely is called ten-wide (9 concurrent calls observed), and Fisher is much harder to break
than Marten was, because that lock absorbs the simultaneous-call corruption Marten reported —
15,000 slice applications over a forced ten-wide run produced no exception at all. So the lock stays
and the declaration is what closes it.

[#111](https://github.com/JasperFx/fisher/issues/111) fell out of writing that fix's test: only
`Snapshot<T>` carried the `HasProviderFor` guard, so a type registered with `ProjectToEfCore` and
projected through `Projections.Add` was mapped anyway and got a stray, empty `fi_doc_` table beside
the EF table it actually writes to. `Add` is the only door for a multi-stream projection, so that was
every EF-backed one. Silent in both directions — the projection works, because storage resolution
checks the registry first, and the table sits in the schema forever.

The release also carries two documentation repairs and the thing that should stop the second one
recurring. [#107](https://github.com/JasperFx/fisher/issues/107) is that HANDOFF.md's deliberate-gaps
list still described composite projections and database-per-tenant as absent, months after both
shipped — understating Fisher in the document the README points at as the public account of what it
does not do. The sweep found worse rot in the compliance scoreboard than in the gaps list it was
filed about: it claimed "2.49.0 ships 32 suites, 275 tests" while its own header already said 2.51.0.
Those numbers are now checked by CI (`scripts/check_scoreboard.py`) against the TRX reports on every
run, per-suite table rows and pinned versions included, so the next drift fails the build instead of
reaching a reader. It caught a mangled comment on its first run.

**1.0.0** is the release that closed the Polecat comparison. Every gap this repository knew about was
closed, the deliberate gaps in [HANDOFF.md](HANDOFF.md) became the standing account of what Fisher
does not do rather than a backlog, and the docs site began shipping with the release —
`docs.yml` matches the same `v*` tag as `publish.yml`, so a tag produces three nupkgs and
fisher.jasperfx.net from one commit. Before that coupling the site had never run and was serving
pre-0.9.0 content.

**0.9.1** is the last of the pre-1.0 fixes, and it is worth reading as a warning about what an
intermittent can hide.
`WaitForNonStaleProjectionDataAsync` decided it was done from the rows in `fi_event_progression`
rather than from the shards the store *registers* — so a shard that had not run yet was invisible, and
a store with two async projections was declared non-stale the moment the first one reached the head.
Behind `QueryForNonStaleData` that tells an application its data is current while a projection has
never run. It presented once, as a `rebuild_and_catch_up_compliance` failure on a loaded two-core CI
runner, and passed 25/25 locally in isolation — the window is the gap between one shard's first commit
and the next shard's. The same rule was broken the other way round too: with events present and no
rows at all, a store with **no** async projections waited out its timeout every time. Registered
shards are now the authority in both directions, and an orphan row from a de-registered projection is
ignored rather than waited on forever.

That is a milestone and not a finish line, so it is worth being precise about what it does and does not
mean. Every gap this repository knows about is closed; it is emphatically **not** the same as being
feature-complete against Marten, and the deliberate gaps in [HANDOFF.md](HANDOFF.md) are still gaps —
they are decisions rather than omissions, which is why they are not issues. The live work that remains
lives elsewhere: `Wolverine.Fisher` is built in the wolverine repo against
[wolverine#3907](https://github.com/JasperFx/wolverine/issues/3907), and
[weasel#426](https://github.com/JasperFx/weasel/issues/426) has since shipped. **The next issue is most
likely to come from a JasperFx release rather than from this repository** — that has been the pattern
for seven bumps, and it is what the compliance enrollment is for.

**0.8.1** is the JasperFx **2.51.0** bump. [#98](https://github.com/JasperFx/fisher/issues/98) is its
one requirement and it is entirely fixture-side: `DocumentComplianceConfig` gained a `StreamIdentity`
knob (jasperfx#672), so a document suite now *states* the stream identity it needs instead of leaving
each fixture to guess. Fisher's fixture had exactly that guess to remove — it set string identity
whenever the config declared event types, an inference that happened to be right only because
`DocumentSessionEventsCompliance` was the only suite populating `EventTypes`, and would have silently
mis-configured the first Guid-keyed event suite to arrive. Verified load-bearing by disabling it: three
of that suite's five facts fail with the stream-identity error jasperfx#672 describes.

The bump also ships two **opt-in** suites, and both contract members behind them carry throwing
defaults — which is why the bump itself builds clean, and why a store adopting neither would look
finished to the compiler.

**0.8.2** is the first of the two. [#96](https://github.com/JasperFx/fisher/issues/96) /
jasperfx#673 puts `PendingStreams` on `IDocumentSessionOperations`, so a consumer holding a session as
the shared contract can read the `StreamAction`s it has queued and not yet committed — a listener or a
pre-commit hook deciding something from the events the session is about to write, without naming a
store. Fisher had the collection and neither the spelling nor the type: `Events.PendingStreams` is
`IReadOnlyCollection<StreamAction>` over a dictionary's values against the contract's
`IReadOnlyList<StreamAction>`, and there is no `PendingChanges` facade as there is on both siblings. So
it is an explicit forward that copies — and the copy is wanted, since the native collection is a *live*
view. **Tenant scopes are included**, because the scopes' streams commit in the same transaction and
`IChangeSet` already reports the two together; the question is about the unit of work rather than about
one tenant. `PendingStreamActionsCompliance` (9 tests) enrolled, and verified load-bearing by removing
the forward: every one of the nine fails on the contract's throwing default.

**0.9.0** is the other, and it is a feature rather than a forward.
[#97](https://github.com/JasperFx/fisher/issues/97) / jasperfx#674 moved `IAggregateWriteCache` into
`JasperFx.Events`, so the three stores share one second-level snapshot cache behind `FetchForWriting`
instead of a consumer targeting all three writing one per flavour. Opt-in per aggregate type through
`Events.CacheAggregatesForWriting<T>()`, off by default, and **grade 1 only**: the cached snapshot is a
*baseline*, the stream version and every event after it are still read on every call, and the
optimistic guard is untouched — so a stale entry costs a larger fold, never a wrong aggregate.

**What a hit removes is bigger here than the issue's PostgreSQL measurements suggest, and it is a
different thing.** On the siblings the cache removes a snapshot *load*; Fisher's `FetchForWriting` folds
the stream on every call by design, so a hit removes *the fold of the history*. Two decisions are
Fisher's rather than the shared design's: nothing is written back at fetch time, because an entry
written while the caller still holds the instance defeats take-on-read and lets a second session fold
its delta onto the object the first is reading; and the version stored is the one read *before* the
unit of work, because Fisher's inline projection — unlike Marten's — leaves the fetched instance alone,
so the committed version would claim events it has not applied.
`aggregate_write_cache.the_inline_projection_leaves_the_fetched_aggregate_alone` pins that premise.
`AggregateWriteCacheCompliance` (14 tests) enrolled, and verified load-bearing by disabling the take:
exactly the two hit-count facts fail.

The 2026-08-17 wave is **0.8.0**, and it is the pattern above playing out exactly: the issue came from
a JasperFx release. [#93](https://github.com/JasperFx/fisher/issues/93) asked for Marten's binary event
serialization, and asked in the same breath for the interface to be lifted into `JasperFx.Events` so a
store-agnostic consumer writes one serializer rather than three. **2.50.0 did lift it**, so fisher#43's
Fisher-native `IEventBinarySerializer` and `[BinaryEvent]` are gone and the `JasperFx.Events` pair
replaces them — the one breaking change in 0.8.0, and the reason it is a minor rather than a patch.
Along with it: per-type registration through `Events.UseBinarySerializer<TEvent>(…)` beside the
attribute, `Events.BinarySerializer` renamed to `Events.DefaultBinarySerializer`, and `data_binary` now
**unconditional** with per-row rather than per-type dispatch — which is what makes marking a type
`[BinaryEvent]` an in-place change on a live file with no migration, and what fisher#43's design could
not do. 2.50.0's other half is jasperfx#669, an `Events` accessor on the document session contracts;
Fisher's sessions declared `Events` as their own concrete type, which does **not** satisfy a contract
member (C# interface implementation is not return-type covariant), so both tiers needed an explicit
implementation. Two new compliance suites pin both halves. On JasperFx **2.51.0** / Weasel **9.24.0**.
**All 36 compliance suites, 309 tests, green** — 30 event suites and 250 tests, plus six document
suites and 59 tests. 1305 tests green on net9.0 and net10.0.

The 2026-08-16 wave is **0.7.2**, and it emptied the tracker again.
[#88](https://github.com/JasperFx/fisher/issues/88) was a real cross-store divergence found by the
CritterWatch port — `FetchLatest<T>` synthesised a default-constructed aggregate for a stream its type
does not handle, where Marten and Polecat return null — and is the polecat#463 class.
[#81](https://github.com/JasperFx/fisher/issues/81) settled a disagreement the store had with itself:
the on-demand table path now honours `AutoCreate.None`, as `HiloSequence` already did. **That one is a
behaviour change** rather than a fix, and is the only thing in 0.7.2 that can break a store which works
today. [#89](https://github.com/JasperFx/fisher/issues/89) is the JasperFx **2.49.0** bump and
`LoadAsync<T>(object)`, the document contract's eighth operation — which is also what let #88's fix
widen to cover strong-typed aggregates.

[#68](https://github.com/JasperFx/fisher/issues/68) closed with its **first half done** — Fisher
implements the JasperFx document persistence abstractions and is enrolled in the four document
compliance suites that came with them — and its second half handed to the wolverine repo, which is
where polecat#443 and marten#5216 both closed too.

The 2026-08-12 wave closed the rest. [#67](https://github.com/JasperFx/fisher/issues/67) was
diagnosed rather than papered over: the pooled-connection release is prompt but not synchronous, which
is the first of the two possibilities that issue laid out and not the second.
[#69](https://github.com/JasperFx/fisher/issues/69) was found while investigating it — a
`[ThreadStatic]` re-entrancy guard held across an `await`, filed with the honest note that neither of
its failure modes ever reproduced. [#55](https://github.com/JasperFx/fisher/issues/55) landed a chain
of joins, which cost one new type and made the rest of the join code shorter.

Before that, the 2026-08-10 wave (#60–#66): two of those audits found real defects (#60's dead
heartbeat branch, #63's composite teardown), #62's ported matrix found two more, and #61 and #66
confirmed Fisher was already correct and now pin it. On JasperFx **2.49.0** / Weasel **9.24.0**.

Most of the issues this file tracks came out of a file-by-file comparison against Polecat on
2026-08-08, which filed [#22](https://github.com/JasperFx/fisher/issues/22) through
[#50](https://github.com/JasperFx/fisher/issues/50) and is indexed in
[polecat-gaps.md](polecat-gaps.md) — that document also records what SQLite has no equivalent for and
never will. It is context; the issues are the tracking.
[#45](https://github.com/JasperFx/fisher/issues/45),
[#34](https://github.com/JasperFx/fisher/issues/34),
[#22](https://github.com/JasperFx/fisher/issues/22),
[#23](https://github.com/JasperFx/fisher/issues/23) and
[#24](https://github.com/JasperFx/fisher/issues/24) are closed; the rest are being worked one at a time.

## The destination

**First round of JasperFx compliance tests passing — reached, and held through seven package bumps
that added fourteen suites.** `JasperFx.Events.ComplianceTests` is the shared cross-store suite Marten
and Polecat both enroll in; passing it is what makes Fisher a real Critter Stack event store rather
than a lookalike. Every event suite it ships is green — twenty-eight from 2.45.0, plus
`BinaryEventSerializationCompliance` from 2.50.0:

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
| `BinaryEventSerializationCompliance` | 6 |
| `AggregateWriteCacheCompliance` | 14 |

**2.47.0 opened a second front rather than adding a twenty-ninth event suite.** `JasperFx.Events.Documents`
(jasperfx#647) is the store-agnostic *document* contract behind the Wolverine aggregate-handler
unification, and it shipped with four suites Fisher is enrolled in — the first shared definition the
document half of any Critter Stack store has been held to. They replace the one-time hand-comparison
against Polecat that filed #22–#50 with something standing.

| Document suite | Tests |
|---|---|
| `DocumentQueryCompliance` | 17 |
| `DocumentLoadAndStoreCompliance` | 11 |
| `DocumentDeleteCompliance` | 10 |
| `PendingStreamActionsCompliance` | 9 |
| `DocumentSessionCompliance` | 7 |
| `DocumentSessionEventsCompliance` | 5 |

All four passed on the first run. The binding cost four interface declarations, one partial class and
one constraint widening — see "The store-agnostic document contract" in CLAUDE.md.

**Eight of the ten event suites added since 2.39.5 went green on the version bump alone** —
`StringStreamIdentityCompliance`, `SnapshotLifecycleCompliance`, `EventDataMaskingCompliance`,
`StreamCompactingCompliance`, both of 2.44.0's, and both of 2.45.0's. That is the useful signal, and
it is a direct dividend of mirroring Polecat's internals: each of those features was built from the
sibling's shape before a shared suite existed to check it, and the suite arriving green is what turns
"ported faithfully" into a fact. `MultiStreamProjectionCompliance` cost one file;
`FlatTableProjectionCompliance` and `StrongTypedIdentityCompliance` cost real features.

Test counts keep understating the suites that matter. `AsyncDaemonCompliance` is two tests that demand
the whole daemon; `FlatTableProjectionCompliance` is eight that demand an upsert generator, a
migration hook and rebuild teardown.

Being green on all thirty-seven is not the same as being feature-complete against Marten. The suites
cover what is portable across stores; the deliberate gaps listed in HANDOFF.md are still gaps.

## Filed follow-ups

| Issue | What |
|---|---|
| ~~[fisher#1](https://github.com/JasperFx/fisher/issues/1)~~ | **Closed.** LINQ ordering and range comparison on date document members — `strftime` normalises inline, no duplicated column needed, exactly as predicted. |
| ~~[fisher#2](https://github.com/JasperFx/fisher/issues/2)~~ | **Closed.** Duplicated fields, as indexed SQLite `VIRTUAL` generated columns — nothing writes them, so they cannot drift from `data` and need no backfill. The generated-column shape this file predicted was the right one. |
| ~~[weasel#426](https://github.com/JasperFx/weasel/issues/426)~~ | **Shipped in Weasel.Sqlite 9.24.0.** `pragma_table_info` omitted generated columns, so a table carrying one never converged. Fisher's `DocumentTable` override was removed on the 9.25.0 bump — a release late, which cost a result-set misalignment when 9.25.0 added a statement the stale copy did not have. Found while building #2. |
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
| ~~[fisher#19](https://github.com/JasperFx/fisher/issues/19)~~ | **Closed.** `CompositeProjection` — ordered stages under one shard, close to free as the issue guessed. What a composite shares across stages is the aggregate cache, not a database read. |
| ~~[fisher#60](https://github.com/JasperFx/fisher/issues/60)~~ | **Closed.** The high-water health check preferred a heartbeat nothing writes for that row, so it silently degraded to the gap heuristic. The agent now re-stamps `last_updated` on an idle cycle — throttled, because on SQLite a periodic write is the file's one write lock. |
| ~~[fisher#61](https://github.com/JasperFx/fisher/issues/61)~~ | **Closed.** Verify-first: the three raised-event members are real, not polecat#420's stubs. One of JasperFx's two branches was unpinned, and now is. |
| ~~[fisher#62](https://github.com/JasperFx/fisher/issues/62)~~ | **Closed.** Marten's streaming/ETag hardening matrix. Two reproduced — a revisioned document could emit no ETag at all, and a cursor whose key would not bind was a 500. Three did not, for structural reasons, and are pinned. |
| ~~[fisher#63](https://github.com/JasperFx/fisher/issues/63)~~ | **Closed.** A composite member held by the wrapper published nothing, so a rebuild replayed onto its surviving rows. The composite's own teardown rules were dropped too. |
| ~~[fisher#64](https://github.com/JasperFx/fisher/issues/64)~~ | **Closed.** JasperFx 2.46.0 + Weasel 9.24.0. No new compliance suites, and the 28/230 counts were re-verified rather than carried over. |
| ~~[fisher#65](https://github.com/JasperFx/fisher/issues/65)~~ | **Closed.** The README's "what is not there" paragraph was stale in four ways, not one. |
| ~~[fisher#66](https://github.com/JasperFx/fisher/issues/66)~~ | **Closed.** Preventive audit: Fisher runs one command per operation, so marten#5210's batch misalignment cannot occur. Pinned, along with the marker's own claim. |
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
| LINQ aggregates (fisher#22) | `Sum`/`Min`/`Max`/`Average`, `Last`, and the predicate overloads; no parser change, and three explicit result conversions `Convert.ChangeType` cannot do |
| LINQ projections (fisher#23) | `Select` as a compiled rewrite, `Distinct` over a projection, `DistinctBy` over documents through `row_number()`; the provider now splits source type from result type |
| Tenant scoping fix (fisher#51) | a conjoined `Query<T>()` with no `Where` returned every tenant's rows; the filter is now one statement-level pass, like the hierarchy and soft-delete ones |
| LINQ marker operators (fisher#26) | `AnyTenant`/`TenantIsOneOf`, `IsOneOf`/`In`, `IsEmpty`, `object.Equals`, `ModifiedSince`/`Before`, `QueryForNonStaleData` |
| Event body queries (fisher#41) | `QueryEventDataAsync<T>`; no new SQL machinery, because an event body is a JSON document in a column called `data` |
| Composite projections (fisher#19) | ordered stages under one shard, rebuilt in one pass; close to free as predicted, and the cross-stage semantics are the aggregate cache rather than a database read |
| Bulk insert (fisher#36, fisher#53) | one transaction per batch through the ordinary statements; no bulk-copy protocol needed, and none exists. `IgnoreDuplicates` filters rather than adding a fifth statement — and its probe is the one place where going around the soft-delete and hierarchy filters is the correct answer, because a soft-deleted row still holds the primary key |
| Patching (fisher#35, fisher#52) | `Patch<T>` by id or predicate; every operation one json1 function, chains nesting into one statement, and a duplicated column following it with nothing to refresh. `Insert` at an index rebuilds the array from an explicit ordinal rather than trusting `json_each`'s row order — and building it found two silent bugs in the `Remove` that shipped first: a JSON boolean flattened to a number, and every `null` in the array dropped by any removal |
| `Advanced` parity (fisher#42) | event store statistics, `CleanAsync<T>`, DDL script generation, and the projection scenario harness |
| JSON reads (fisher#28) | `LoadJsonAsync`, `ToJsonArrayAsync`, the version variant and streaming; byte-exact, which neither sibling can promise |
| Batching and plans (fisher#37) | the DCB batch widened to documents and moved to `Fisher.Batching`, plus `IQueryPlan`, `CheckExistsAsync` and `ToSql` |
| LINQ paging (fisher#27) | `ToPagedListAsync` with a real total, and keyset paging with a Polecat-compatible cursor |
| LINQ grouping (fisher#24) | `GroupBy`, a `Select` over the group, `HAVING` from a `Where` after it, and ordering by an aggregate; the expected lax-GROUP-BY hazard is unreachable through the API |
| LINQ joins (fisher#25) | `Join` and `GroupJoin(...).SelectMany(...)` on the ordinary `Statement`, so counting, paging and `ToSql` serve a join for free; aliases threaded through `MemberFactory` rather than rewritten into rendered SQL. Chained across any number of tables by [#55](https://github.com/JasperFx/fisher/issues/55), which cost one type (`JoinShape`) and made the rest shorter |
| Join aggregates (fisher#54) | the scalar aggregates and `Last` over a join, from the chain's source type rather than its element type — which also fixed a real defect, an aggregate after a `Select` throwing about identity members instead of refusing by name |
| Natural keys (fisher#40) | `fi_natural_key_<alias>`, written inside the append's transaction and resolved through a join to `fi_streams` — which is also why there is no `is_archived` column to keep in sync, and so no rebuild path. Closes the last partial member on `IEventStoreOperations` |
| Document metadata (fisher#29) | five opt-in columns — `created_at`, `correlation_id`, `causation_id`, `last_modified_by`, `headers` — plus `tenant_id` read back onto a member, and `MetadataForAsync`. Every binder was already in Weasel.Storage, so it was wiring; and `created_at` needed no exception to the `excluded.*` rule after all, because a read-only binder never enters the write list |
| Sessions and enlistment (fisher#30) | `QuerySession()` and `OpenSession(SessionOptions)` on the store, and a session running inside a connection or transaction the caller owns — the other half of the atomicity problem `QueueSqlCommand` answers from one side |

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
table. Composite projections landed in [#19](https://github.com/JasperFx/fisher/issues/19) and were close to
free, as predicted. Delivery behind the
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
| ~~[#30](https://github.com/JasperFx/fisher/issues/30) sessions and `SessionOptions`~~ | **Done.** Enlistment was the half that mattered and it landed: a session runs on a caller's connection, or inside a caller's transaction, committing nothing. Two of the five listed options were deliberately not built — `Tracking` belongs to #31 and `Listeners` to #32, and either would be a knob that does nothing. |
| ~~[#34](https://github.com/JasperFx/fisher/issues/34) `QueueSqlCommand`~~ | **Done**, with `IAdvancedSql` alongside it. The port was the small half; the work was `SqliteParameterValue`, which has no sibling to port from — raw SQL is the one path with no conversion between the caller's value and what Fisher stored, and Guid, timestamp and decimal each bind to something that matches nothing. |
| ~~[#45](https://github.com/JasperFx/fisher/issues/45) `IDocumentStore`~~ | **Done.** Eight public members, extracted and pinned by reflection in both directions. [#46](https://github.com/JasperFx/fisher/issues/46) and [#49](https://github.com/JasperFx/fisher/issues/49) are unblocked. |

**Then LINQ**, in dependency order — ~~[#22](https://github.com/JasperFx/fisher/issues/22) aggregates~~
(**done**; it needed no parser change at all, because the terminal extensions take the selector as an
argument and it never reaches the expression tree), then
~~[#23](https://github.com/JasperFx/fisher/issues/23) `Select` projections~~ (**done**), then
~~[#24](https://github.com/JasperFx/fisher/issues/24) `GroupBy`~~ (**done**; the lax-GROUP-BY hazard
this file predicted turned out to be unreachable, because a grouped `Select`'s parameter is the
grouping rather than the document).
[#26](https://github.com/JasperFx/fisher/issues/26)'s marker operators are independent and mostly
small. ~~[#25](https://github.com/JasperFx/fisher/issues/25) joins~~ (**done**) and
~~[#27](https://github.com/JasperFx/fisher/issues/27) cursor paging~~ (**done**) were the two places
SQLite is the *easiest* of the three dialects, and both paid out: the join needed no `OPENJSON`, no
lateral gymnastics and no bespoke statement type, and an expression index from #16 serves either side
of it.

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
- **`Advanced` is no longer a thin subset.** fisher#42 added statistics, per-type cleaning, script
  generation and the projection scenario. What is still absent is `InitialData`
  ([#39](https://github.com/JasperFx/fisher/issues/39)).
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
