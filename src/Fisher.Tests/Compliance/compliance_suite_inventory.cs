using Fisher.Tests.Compliance;
using JasperFx.Events.ComplianceTests;
using Shouldly;

// Deliberately NOT Fisher.Tests.Compliance, although it sits beside the fixtures it audits:
// scripts/check_scoreboard.py counts every test class in that namespace as an enrolled compliance suite,
// and this class is an audit of the enrollment, not a suite JasperFx ships. In that namespace it would
// read as a sixty-fourth enrolled suite of the sixty-four 2.101.0 ships, while it exists to prove that
// exactly one of them (SingleTenantedEventSlicingCompliance) is not enrolled.
namespace Fisher.Tests;

/// <summary>
///     jasperfx#1123 / fisher#519 — a compliance suite can never be left out silently. Every leaf suite
///     JasperFx ships must have a concrete subclass closed over Fisher's fixture, or an excuse with a reason.
/// </summary>
public class compliance_suite_inventory
{
    // Only SingleTenantedEventSlicingCompliance, for the precondition reason in fisher_event_store_compliance.cs.
    private static readonly Dictionary<Type, string> EventExcuses = new()
    {
        [typeof(SingleTenantedEventSlicingCompliance<,,>)] =
            "Fisher cannot construct the state the suite plants (events whose tenant ids disagree on a " +
            "single-tenanted store); see jasperfx#727"
    };

    [Fact]
    public void every_event_suite_is_enrolled_or_excused()
        => ComplianceSuiteInventory.AssertEnrolled(typeof(compliance_suite_inventory).Assembly,
            new FisherComplianceFixture(), EventExcuses);

    [Fact]
    public void every_document_suite_is_enrolled_or_excused()
        => ComplianceSuiteInventory.AssertEnrolled(typeof(compliance_suite_inventory).Assembly,
            new FisherDocumentComplianceFixture());
}
