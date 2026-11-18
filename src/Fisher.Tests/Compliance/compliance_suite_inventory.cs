using Fisher.Tests.Compliance;
using JasperFx.Events.ComplianceTests;
using Shouldly;

// Deliberately NOT Fisher.Tests.Compliance, although it sits beside the fixtures it audits:
// scripts/check_scoreboard.py counts every test class in that namespace as an enrolled compliance suite,
// and this class is an audit of the enrollment, not a suite JasperFx ships. In that namespace it would
// read as one more enrolled suite, while it exists to prove that every shipped suite is enrolled or excused.
namespace Fisher.Tests;

/// <summary>
///     jasperfx#1123 / fisher#519 — a compliance suite can never be left out silently. Every leaf suite
///     JasperFx ships must have a concrete subclass closed over Fisher's fixture, or an excuse with a reason.
/// </summary>
public class compliance_suite_inventory
{
    private static readonly Dictionary<Type, string> EventExcuses = new()
    {
        [typeof(EventRewriteCompliance<,,>)] =
            "Fisher does not rewrite stored events yet (fisher#643). SupportsEventRewrites stays false until then",
        [typeof(BackfillCompliance<,,>)] =
            "Fisher does not run projection backfills yet (fisher#643), as Marten (marten#5632) and Polecat "
            + "(polecat#1055) do not. SupportsBackfills stays false until then"
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
