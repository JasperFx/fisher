using JasperFx.Events.ComplianceTests;

namespace Fisher.Tests.Compliance;

/*
 * Fisher's enrollment in the cross-store event sourcing compliance suites. Each class below is empty
 * on purpose: the behaviour lives once in JasperFx.Events.ComplianceTests and is closed here over
 * Fisher's session pair through FisherComplianceFixture. Marten and Polecat enroll the same way, so
 * these tests cannot drift between the products.
 *
 * Suites are added one at a time as Fisher grows into them. What is NOT enrolled yet, and why:
 *
 *   FetchForWritingCompliance          snapshots + document load-back
 *   SelfAggregatingEvolveCompliance    snapshots + document load-back
 *   StringIdentitySingleStreamCompliance   projection registration + document load-back
 *   EventProjection{Registration,Enrichment}Compliance   projections + document storage
 *   AsyncDaemonCompliance              the async daemon
 *   RebuildConcurrencyCapCompliance    IEventStore on DocumentStore + rebuilds
 *   DcbTagQueryAndConsistencyCompliance, AssignTagWhereCompliance   DCB tag tables
 *
 * Every one of those is now blocked on a numbered roadmap milestone rather than on a loose end.
 * The fixture throws a NotSupportedException naming the milestone for each, so enrolling a suite
 * prematurely fails loudly rather than silently passing on a stub.
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
