using JasperFx.Events.ComplianceTests;

namespace Fisher.Tests.Compliance;

/*
 * Fisher's enrollment in the cross-store event sourcing compliance suites. Each class below is empty
 * on purpose: the behaviour lives once in JasperFx.Events.ComplianceTests and is closed here over
 * Fisher's session pair through FisherComplianceFixture. Marten and Polecat enroll the same way, so
 * these tests cannot drift between the products.
 *
 * Suites were added one at a time as Fisher grew into them, and fifty are enrolled from
 * JasperFx.Events.ComplianceTests 2.65.0, which itself ships fifty-two. One is not enrolled:
 * SingleTenantedEventSlicingCompliance, for the precondition reason set out below.
 *
 * Three of the ten suites this wave adds are enrolled and gated off rather than green, each for a
 * reason recorded at the flag on FisherComplianceFixture rather than here:
 * DcbHasTagLinqCompliance and AggregateToLinqOperatorCompliance / AggregateToManyCompliance all
 * terminate a QueryAllRawEvents() IQueryable<IEvent> that Fisher does not have and would need a
 * second LINQ provider to grow, since Fisher's is built over document storage. Fisher answers the
 * same questions through QueryEventsAsync, AssignTagWhere and AggregateByTagsAsync, all of which are
 * covered by suites that are enrolled and green.
 *
 * SingleTenantedEventSlicingCompliance's non-enrollment is a precondition rather than a behaviour,
 * and that distinction is the reason it is recorded here rather than gated: the suite plants events
 * whose tenant ids disagree on a single-tenanted store and drives the daemon over them, and Fisher
 * cannot construct that state at all -- see jasperfx#727. Its own guard reads the events back and
 * skips when the store normalised the tenant ids away, so enrolling it would be forty-odd facts that
 * skip themselves. It is not a suite Fisher declines on behaviour.
 *
 * Nothing in the fixture throws any more, but the discipline stands for the next seam member that
 * arrives ahead of the feature: a member Fisher cannot honour throws a NotSupportedException naming
 * its milestone, so a suite reaching for one fails loudly rather than passing on a stub.
 */

public class stream_read_compliance
    : StreamReadCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class event_metadata_compliance
    : EventMetadataCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class live_aggregation_compliance
    : LiveAggregationCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class activity_correlation_compliance
    : ActivityCorrelationCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class auto_discovered_aggregate_compliance
    : AutoDiscoveredAggregateCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class self_aggregating_evolve_compliance
    : SelfAggregatingEvolveCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class fetch_for_writing_compliance
    : FetchForWritingCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class string_identity_single_stream_compliance
    : StringIdentitySingleStreamCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class event_projection_registration_compliance
    : EventProjectionRegistrationCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class event_projection_enrichment_compliance
    : EventProjectionEnrichmentCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class fetch_latest_compliance
    : FetchLatestCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class stream_archiving_compliance
    : StreamArchivingCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class event_store_explorer_compliance
    : EventStoreExplorerCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class rebuild_concurrency_cap_compliance
    : RebuildConcurrencyCapCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class assign_tag_where_compliance
    : AssignTagWhereCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class dcb_tag_query_and_consistency_compliance
    : DcbTagQueryAndConsistencyCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class async_daemon_compliance
    : AsyncDaemonCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class string_stream_identity_compliance
    : StringStreamIdentityCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class snapshot_lifecycle_compliance
    : SnapshotLifecycleCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class multi_stream_projection_compliance
    : MultiStreamProjectionCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class flat_table_projection_compliance
    : FlatTableProjectionCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class strong_typed_identity_compliance
    : StrongTypedIdentityCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class event_data_masking_compliance
    : EventDataMaskingCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class stream_compacting_compliance
    : StreamCompactingCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class rebuild_and_catch_up_compliance
    : RebuildAndCatchUpCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class dead_letter_compliance
    : DeadLetterCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class conjoined_event_tenancy_compliance
    : ConjoinedEventTenancyCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class subscription_compliance
    : SubscriptionCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

/*
 * fisher#148 / jasperfx#737 — the broadened cross-stream EventQuery behind
 * IReadOnlyEventStore.QueryEventsAsync: every filter field including the inclusive timestamp and
 * sequence windows, the multi-type union, and the folded DCB tag conditions, plus the
 * sequence-ascending ordering and paging/TotalCount contracts. Fisher declares the full
 * EventQueryFilters set for the suite's configuration (correlation + user-name capture on, tag type
 * registered), so all forty-one facts run — see EventOperations.SupportedEventQueryFilters for the
 * flags a differently-configured store honestly drops.
 *
 * Enrolling this suite caught an upstream seed off-by-one in paging_composes_with_filtering (6
 * matching events seeded, 7 asserted — unpassable on any store), fixed in jasperfx#739 before
 * 2.62.0 shipped. Fisher's own read_only_event_store.paging_composes_with_filtering keeps a
 * store-side pin on the same contract.
 */

public class event_query_compliance
    : EventQueryCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

/*
 * fisher#151 / jasperfx#740 — the streams table as an IQueryable<StreamState> behind
 * IReadOnlyEventStore.QueryStreamStates, executed through the shared IDocumentQueryExecutor hook,
 * plus the CompactedVersion compaction watermark (recorded by CompactStreamAsync: partial = the
 * cutoff version, full = the stream version, never = 0). Fifteen facts: one Where() per public get
 * member with per-member decoys, the compaction-policy selector verbatim
 * (AggregateType == typeof(X) && Version - CompactedVersion > N && !IsArchived), the stated
 * ordering (Created ascending, Id tiebreak) with truthful paging, and the shared terminators.
 * The untranslatable-member and tenantless-tenant refusals cannot be pinned upstream (both
 * reference stores translate the full set), so Fisher's own stream_state_queries pins them.
 */

public class stream_state_query_compliance
    : StreamStateQueryCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

/*
 * fisher#154 / jasperfx#725 — composite projections: several members sharing one shard, one
 * progression row and one event batch, executed in stage order and torn down together on rebuild.
 * Opt-in through the registrar's AddCompositeProjection, whose throwing default is what kept this
 * suite unenrolled — never a behaviour Fisher declined: Projections.CompositeProjectionFor is
 * fisher#19, and composite_member_teardown is one of the two local regression tests (with Polecat's
 * Bug_439) whose existence is the suite's own argument for being shared.
 *
 * The suite's load-bearing fact is the rebuild one, with deliberately additive members: a store that
 * replayed the stream over surviving rows instead of tearing each member down reads back exactly
 * doubled — the fisher#63 / marten#5175 class of bug. The single-shard fact pins what a composite
 * is; a store expanding one into a shard per member would pass the other two and still have changed
 * the model.
 */

public class composite_projection_compliance
    : CompositeProjectionCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

/*
 * ---------------------------------------------------------------------------------------------
 * Wave 13 — JasperFx.Events.ComplianceTests 2.64.0, fisher#184.
 *
 * Eight new suites plus a deepened SubscriptionCompliance, and NONE of them had run against a real
 * event store before this: the JasperFx repository enrols only the document suites, so every one
 * arrived compile-checked and design-reasoned. Fisher enrolling them is first-contact runtime
 * validation, which is worth saying because it changes what a failure here means — a red suite is as
 * likely to be an over-tight assertion as a store bug, and each one was classified rather than
 * assumed.
 * ---------------------------------------------------------------------------------------------
 */

/*
 * jasperfx#764 — natural keys: addressing a stream by the business identifier it was created with,
 * across the whole FetchForWriting / FetchForExclusiveWriting / FetchLatest triple, both stream
 * identity styles, mutation, renaming, archiving, tenancy and rebuild.
 *
 * Gated on SupportsNaturalKeys, which uniquely guards no seam member at all: the attributes, the
 * definition and the discovery are shared (JasperFx.Events.Aggregation), and what varies is whether
 * a store built the storage half. Fisher's is fisher#40.
 *
 * Its uniqueness fact is the one that was ruled in Fisher's favour: a second stream claiming a live
 * key is REFUSED, where Polecat's MERGE repointed the key and left the original stream unreachable
 * by the identifier it was created with. That ruling is also why Fisher's
 * DuplicateNaturalKeyException now subclasses the lifted JasperFx.Events one (fisher#178) — the
 * canonical type was lifted from Fisher's, message and all, and the suite catches the shared type.
 */

public class natural_key_compliance
    : NaturalKeyCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

/*
 * jasperfx#762 — IEventStream.AlwaysEnforceConsistency: an empty unit of work still asserts the
 * stream version it was fetched at.
 *
 * No capability gate, deliberately — the flag is on the shared IEventStream and a store either
 * honours it or silently does not. Fisher did not, and this suite is what found it: the append
 * planner collected only streams with at least one event, so a stream fetched for writing, flagged,
 * and then left alone was dropped from the unit of work along with its version guard. See
 * AppendPlanner.CollectActionableStreams.
 */

public class always_enforce_consistency_compliance
    : AlwaysEnforceConsistencyCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

/*
 * jasperfx#763 — projection side effects: a projection's raised events land on their stream inside
 * the batch's transaction, and its published messages reach the store's message outbox with both
 * commit hooks bracketing that transaction.
 *
 * The outbox facts are gated on SupportsMessageOutbox and the suite brings its own recording outbox,
 * for the reason RecordingAggregateWriteCache exists: every behavioural fact about side effects is
 * vacuously true of a store that dropped them on the floor, and two of the three products shipped
 * the raise seam stubbed empty (fisher#61, polecat#420). The visibility probe is gated separately
 * because its hazard is the engine's rather than the outbox's — it reads committed state while the
 * write transaction is open, which WAL answers and a lock-based reader would deadlock on.
 */

public class projection_side_effect_compliance
    : ProjectionSideEffectCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

/*
 * jasperfx#769 — the shared ProjectionScenario harness, reached through Fisher's own documented
 * entry point (Advanced.EventProjectionScenarioAsync) rather than reconstructed by the fixture.
 * That distinction is the suite's, and it is the right one: a fixture that inlined
 * construct-configure-execute would pass every fact while the store's advertised entry point was
 * missing or wired to the wrong store.
 */

public class projection_scenario_compliance
    : ProjectionScenarioCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

/*
 * jasperfx#732 — the store registers a REACHABLE IProjectionCoordinator over a host built the
 * documented way.
 *
 * ⚠️ This suite exists because of fisher#138, and it is the third instance of one pattern after
 * jasperfx#700 and jasperfx#718: Fisher registered only an IHostedService over an internal class
 * implementing nothing else, so both documented routes to the running daemon failed — and all 37
 * suites passed the whole time, because every other daemon suite drives a daemon the fixture built
 * by hand. Its pause/resume fact targets exactly the StartAsync bug fisher#138 fixed.
 *
 * It carries no capability gate on purpose: a store that enrolls it without implementing
 * StartCoordinatorHostAsync fails every fact rather than skipping, since a skippable registration
 * check would recreate the silent gap the suite exists to close. Only the ancillary fact is gated,
 * and Fisher opts in — AddFisherStore<T> registers IProjectionCoordinator<T> keyed on the marker.
 */

public class projection_coordinator_compliance
    : ProjectionCoordinatorCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

/*
 * jasperfx#770 — the two stream-fetch query plans, standalone and inside a batched query. Fisher
 * ships them as of this wave (Fisher.Batching.FetchStreamStatePlan / FetchStreamPlan), which is
 * parity with polecat#370 and cost two small classes.
 *
 * The suite's `batched` axis is asserting sameness that Fisher gets structurally rather than by
 * keeping two implementations honest: Polecat's batched half composes its own SQL fragment, where
 * Fisher's IBatchedQuery runs each item in turn on one connection — there are no round trips to
 * collapse in an embedded store, and the batch is carried for API parity. So the version cap, the
 * parameter the suite calls out as most likely to drift, cannot.
 */

public class stream_query_plan_compliance
    : StreamQueryPlanCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

/*
 * jasperfx#755 and jasperfx#754 — the three suites Fisher enrolls and GATES OFF, each declining a
 * LINQ surface rather than a behaviour.
 *
 * All three terminate a cross-stream `QueryAllRawEvents()` returning IQueryable<IEvent>, which
 * Fisher does not have: its LINQ provider is built over document storage — statements, selectors and
 * member factories all resolve against a fi_doc_* table — so an event queryable would be a parallel
 * provider serving one caller. That is why EventOperations.QueryEventsAsync takes a predicate, and
 * why AssignTagWhere reaches the same WhereClauseParser through EventMemberFactory instead.
 *
 * Nothing behavioural is declined. DCB tag querying is enrolled and green through
 * DcbTagQueryAndConsistencyCompliance and AssignTagWhereCompliance; cross-stream aggregation is
 * AggregateByTagsAsync and AggregateStreamAsync. They are enrolled here rather than left out so the
 * decision is visible in the run and so flipping a flag is all it would take.
 */

public class dcb_has_tag_linq_compliance
    : DcbHasTagLinqCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class aggregate_to_linq_operator_compliance
    : AggregateToLinqOperatorCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

public class aggregate_to_many_compliance
    : AggregateToManyCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

/*
 * jasperfx#752 / fisher#191 — event upcasting: a transformation registered against
 * EventRegistry.Upcasters reinterprets an old stored event schema as the current CLR event type on
 * every read path, so no read — stream fetch, live aggregation, FetchForWriting, the daemon — ever
 * hands application code the old schema.
 *
 * ⚠️ This suite was written BEFORE any store implemented the contract, which is unique in the
 * library: its gate ships closed and the suite IS the specification. Fisher is the first store to
 * flip it, so every one of these facts is running for the first time anywhere.
 *
 * Two of them are worth naming. `an_async_only_upcast_applies_on_the_async_read_path` is why Fisher's
 * row hydration became asynchronous: an async-only transformation's synchronous delegate throws
 * UpcastingException by design, so a store hydrating synchronously could not honour one at all — and
 * every Fisher read path was already inside an `await reader.ReadAsync(...)` loop, so the change cost
 * a ValueTask per row. And `a_typed_append_of_the_old_event_type_does_not_shadow_the_upcaster` is the
 * marten#4680 authority rule: the stored dotnet_type hint does not get a vote, which is why the
 * registry is consulted BEFORE ResolveEventType rather than as a fallback after it.
 */

public class upcasting_compliance
    : UpcastingCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

/*
 * fisher#93 — binary event serialization, arriving in JasperFx.Events.ComplianceTests 2.50.0 and
 * opt-in rather than baseline: the two registrar members it drives carry throwing defaults, so a
 * store without binary storage declines by not writing the line below.
 *
 * Fisher does not decline. The definition it is held to is the one that matters most for an embedded
 * store — that JSON and binary rows coexist per event type in one table, so marking a chatty type
 * [BinaryEvent] on a live SQLite file is an in-place change with no migration of the events already
 * in it.
 */

public class binary_event_serialization_compliance
    : BinaryEventSerializationCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

/*
 * fisher#97 / jasperfx#674 — the second-level FetchForWriting snapshot cache, opt-in the same way.
 *
 * The suite's subject is that turning caching on is unobservable except in latency: a hit is
 * indistinguishable from a miss, including when the baseline is stale, ahead of the stream, or
 * evicted, and a cached baseline can never suppress a concurrency failure. Every one of those facts
 * is vacuously true of a store that dropped the opt-in on the floor, since an uncached fetch is
 * correct by construction — which is why the suite brings its own recording cache and asserts a
 * nonzero hit count. `the_cache_is_actually_consulted_when_a_type_opts_in` is the fact holding that,
 * and it is the only one Fisher could fail by doing nothing.
 */

public class aggregate_write_cache_compliance
    : AggregateWriteCacheCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

/*
 * fisher#68 / jasperfx#647 — the DOCUMENT compliance suites, which arrived in
 * JasperFx.Events.ComplianceTests 2.47.0 and cover the slice JasperFx.Events did not before.
 *
 * Same model as the event sourcing enrollment above, with one difference worth noticing: these close
 * over nothing. The event fixture is generic over Fisher's session pair because so much of the event
 * surface is only reachable through a store's own session type; every one of these runs through the
 * shared JasperFx.Events.Documents contracts, which Fisher's own IQuerySession / IDocumentOperations /
 * IDocumentSession / IDocumentStore implement directly rather than through an adapter. If one of them
 * ever needs to reach past the contract, that is a hole in the contract rather than a seam to widen.
 */

public class document_session_compliance
    : DocumentSessionCompliance<FisherDocumentComplianceFixture>;

public class document_load_and_store_compliance
    : DocumentLoadAndStoreCompliance<FisherDocumentComplianceFixture>;

public class document_delete_compliance
    : DocumentDeleteCompliance<FisherDocumentComplianceFixture>;

public class document_query_compliance
    : DocumentQueryCompliance<FisherDocumentComplianceFixture>;

/*
 * jasperfx#669 — the route from a session the consumer opened to that session's event store. Opt-in
 * because it is the one document suite that needs the store to be an event store as well; Fisher is
 * both, so it enrolls.
 *
 * ⚠️ The failure it catches is silent. Fisher's IQuerySession and IDocumentSession both declare an
 * Events of Fisher's own EventOperations type, and C# interface implementation is not return-type
 * covariant — so neither satisfies IDocumentReadOperations.Events or IDocumentSessionOperations.Events,
 * and both would bind to the contract's throwing default with no compile error anywhere. The two
 * explicit implementations on FisherSession are what close it; this is what proves them closed.
 */

public class document_session_events_compliance
    : DocumentSessionEventsCompliance<FisherDocumentComplianceFixture>;

/*
 * jasperfx#673 — the StreamActions a session has queued and not yet committed, read by code that did
 * not do the appending. Same trap one member over, and Fisher is the store most exposed to it: it
 * already has a member *named* PendingStreams, on EventOperations, returning
 * IReadOnlyCollection<StreamAction> rather than the contract's IReadOnlyList<StreamAction>. Had that
 * shape ever landed on the session type it would bind to the throwing default with a clean build.
 *
 * The suite's first fact is the load-bearing one: an empty collection on a session with nothing
 * enlisted, which a store still on the default cannot produce. That is why the default throws rather
 * than answering empty — empty is indistinguishable from a session with nothing pending, so a silent
 * default would let a consumer's derived work be discarded with green tests.
 */

public class pending_stream_actions_compliance
    : PendingStreamActionsCompliance<FisherDocumentComplianceFixture>;

/*
 * jasperfx#679 — the post-commit session hook, and the change set it is handed. Fisher enrolls
 * because the suite needs documents and nothing else.
 *
 * ⚠️ This is the one document suite a green build says nothing about. Unlike jasperfx#669 and #673
 * the shared contract declares no default implementation, so a near-miss member is CS0535 rather
 * than a silent bind — but the *wiring* is invisible to the compiler at every point. A store that
 * declares IDocumentCommitListener and IDocumentChangeSet perfectly and never invokes a listener
 * compiles clean and passes every other suite in the library. These ten facts are the only thing
 * standing between the contract and a no-op.
 *
 * Two of Fisher's firing rules are deliberately NOT exercised here, and the suite says so in its own
 * remarks: it asserts nothing about an empty unit of work, and nothing about a session enlisted in a
 * caller's transaction. Fisher skips the hook for both (SaveChangesAsync's early return, and
 * `EnlistedTransaction is null` on the post-commit branch), which the contract permits and Marten
 * does not do. Fisher's own tests own those two — see the session listener tests.
 */

public class document_commit_listener_compliance
    : DocumentCommitListenerCompliance<FisherDocumentComplianceFixture>;
