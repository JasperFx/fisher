using JasperFx.Events.Aggregation;

namespace Fisher.Tests.Documentation;

/*
 * The compiled source behind docs/events/dcb.md.
 *
 * See "Documentation samples" in CLAUDE.md: every sample a reader would copy lives in a
 * #region here and is pulled into the markdown by mdsnippets, so a sample that stops compiling
 * fails the build rather than going stale in a page nobody rebuilds.
 */

public record SeatReserved(string Seat);

public record SeatReleased(string Seat);

#region sample_dcb_boundary_aggregate
[BoundaryAggregate]
public partial class ShowSeating
{
    public HashSet<string> Reserved { get; } = [];

    public void Apply(SeatReserved e) => Reserved.Add(e.Seat);

    public void Apply(SeatReleased e) => Reserved.Remove(e.Seat);
}
#endregion
