using Fisher.Tests.Compliance;
using JasperFx.Events.ComplianceTests;
using Shouldly;

// Deliberately NOT Fisher.Tests.Compliance, although it sits beside the fixtures it audits:
// scripts/check_scoreboard.py counts every test class in that namespace as an enrolled compliance suite,
// and this class is an audit of the enrollment, not a suite JasperFx ships. In that namespace it would
// read as a sixty-fifth enrolled suite of the sixty-five 2.102.0 ships, while it exists to prove that
// exactly one of them (SingleTenantedEventSlicingCompliance) is not enrolled.
namespace Fisher.Tests;

/// <summary>
///     jasperfx#1123 / fisher#519 — a compliance suite can never be left out silently. Every leaf suite
///     JasperFx ships must have a concrete subclass closed over Fisher's fixture, or an excuse with a reason.
/// </summary>
public class compliance_suite_inventory
{
    // JasperFx 3.0.0 no longer ships SingleTenantedEventSlicingCompliance, so nothing is excused.
    private static readonly Dictionary<Type, string> EventExcuses = new();

    [Fact]
    public void every_event_suite_is_enrolled_or_excused()
        => ComplianceSuiteInventory.AssertEnrolled(typeof(compliance_suite_inventory).Assembly,
            new FisherComplianceFixture(), EventExcuses);

    [Fact]
    public void every_document_suite_is_enrolled_or_excused()
        => ComplianceSuiteInventory.AssertEnrolled(typeof(compliance_suite_inventory).Assembly,
            new FisherDocumentComplianceFixture());
}
