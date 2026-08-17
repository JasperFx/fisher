using JasperFx.Events.ComplianceTests;

namespace Fisher.Tests.Compliance;

/*
 * Fisher's enrollment in the cross-store event sourcing compliance suites. Each class below is empty
 * on purpose: the behaviour lives once in JasperFx.Events.ComplianceTests and is closed here over
 * Fisher's session pair through FisherComplianceFixture. Marten and Polecat enroll the same way, so
 * these tests cannot drift between the products.
 *
 * Suites were added one at a time as Fisher grew into them. All twenty-eight that ship in
 * JasperFx.Events.ComplianceTests 2.45.0 are enrolled; there is no suite Fisher declines.
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
