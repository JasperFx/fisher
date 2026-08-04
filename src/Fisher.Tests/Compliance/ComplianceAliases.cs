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
