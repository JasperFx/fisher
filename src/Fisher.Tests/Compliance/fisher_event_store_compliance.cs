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
