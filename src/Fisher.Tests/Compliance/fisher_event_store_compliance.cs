using JasperFx.Events.ComplianceTests;

namespace Fisher.Tests.Compliance;

/*
 * Fisher's enrollment in the cross-store event sourcing compliance suites. Each class below is empty
 * on purpose: the behaviour lives once in JasperFx.Events.ComplianceTests and is closed here over
 * Fisher's session pair through FisherComplianceFixture. Marten and Polecat enroll the same way, so
 * these tests cannot drift between the products.
 *
 * Suites were added one at a time as Fisher grew into them, and thirty-eight are enrolled from
 * JasperFx.Events.ComplianceTests 2.62.0, which itself ships forty. The two not enrolled are both
 * opt-in and both new in 2.59.0: SingleTenantedEventSlicingCompliance (jasperfx#724), whose
 * mixed-tenancy precondition cannot be constructed on Fisher at all -- see jasperfx#727 -- and
 * CompositeProjectionCompliance (jasperfx#725), which needs an AddCompositeProjection member on the
 * fixture. Neither is a suite Fisher declines on behaviour.
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
 * ⚠️ KNOWN RED at 2.62.0-local737, upstream not Fisher: paging_composes_with_filtering seeds 6
 * matching events (i in 0..9 with i % 3 == 0 as noise is 4 noise + 6 matches) while asserting the
 * stated 7, so it cannot pass on any store. Fisher's own
 * read_only_event_store.paging_composes_with_filtering covers the fact's intent with a correct
 * seed. Expect 41/41 once the suite's seed is fixed upstream for the real 2.62.0.
 */

public class event_query_compliance
    : EventQueryCompliance<FisherComplianceFixture, IDocumentSession, IQuerySession>;

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
