// The shared compliance suites declare types at file scope — self-aggregating aggregates whose
// EvolveAsync convention method takes the store's read session, and projection classes that cannot
// reach the <TOperations, TQuerySession> pair their suite class is generic over. JasperFx's aggregate
// source generator resolves those parameters by type name, so a per-consumer global alias lets one
// shared source file bind to Fisher's session types here and to Marten's or Polecat's there.

global using ComplianceQuerySession = Fisher.IQuerySession;
global using ComplianceOperations = Fisher.IDocumentSession;
global using ComplianceEventProjection = Fisher.Projections.EventProjection;

// The string-identity suite's custom projection needs Fisher's own SingleStreamProjection base, which
// is generic over the identity type as well as the document — so this alias names a closed generic
// rather than an open one.
global using ComplianceStringPartyProjectionBase =
    Fisher.Projections.SingleStreamProjection<JasperFx.Events.ComplianceTests.StringQuestParty, string>;

// The multi-stream suite groups by department name, so the base closes over a string identity even
// though the events it slices arrive on Guid-identified streams. Same closed-generic reason as above.
global using ComplianceMultiStreamProjectionBase =
    Fisher.Projections.MultiStreamProjection<JasperFx.Events.ComplianceTests.ComplianceDepartment, string>;

// Wave 13 (2.64.0) adds three more closed-generic aliases. The side effect suite's projection is
// single stream over the aggregate's own Guid; the two aggregate-to-many projections are multi
// stream, one identity-routed and one grouped through a session lookup.
//
// Only the first is in the shared README's alias block — the two below are undocumented there,
// which is a doc gap rather than a design one: a consumer meets them as three CS0246s on the bump
// with nothing saying what to write. Reported upstream alongside this wave.
global using ComplianceWatchtowerProjectionBase =
    Fisher.Projections.SingleStreamProjection<JasperFx.Events.ComplianceTests.ComplianceWatchtower, System.Guid>;
global using ComplianceBalanceProjectionBase =
    Fisher.Projections.MultiStreamProjection<JasperFx.Events.ComplianceTests.ComplianceBalance, System.Guid>;
global using ComplianceMemberLoyaltyProjectionBase =
    Fisher.Projections.MultiStreamProjection<JasperFx.Events.ComplianceTests.ComplianceMemberLoyalty, System.Guid>;
