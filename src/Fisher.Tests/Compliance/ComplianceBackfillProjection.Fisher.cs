namespace JasperFx.Events.ComplianceTests;

// jasperfx#1222 / fisher#643: Fisher's own Backfill(...) syntax, which the shared backfill suite cannot write for it
public partial class ComplianceBackfillProjection
{
    partial void declareBackfill()
        => Backfill(BackfillName).On<BackfillLineAdded>((doc, e) => doc.LineTotal += e.Amount);
}
