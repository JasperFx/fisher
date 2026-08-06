using System.Linq.Expressions;
using JasperFx.Events;
using JasperFx.Events.Tags;

namespace Fisher.Events;

/// <summary>
///     The members of <see cref="IEventStoreOperations" /> Fisher does not implement yet.
/// </summary>
/// <remarks>
///     Collected in one file deliberately. Every one of them throws with a message naming the feature
///     it waits on, so a caller finds out what is missing rather than what is null — and so this file
///     shrinking is a visible measure of progress.
/// </remarks>
public partial class EventOperations
{
    // ---- Dynamic Consistency Boundary tags ----
    //
    // The tag tables, the write path and the two read members are live — see
    // EventOperations.Tags.cs and Events/Storage/EventTagWriter.cs. What remains here needs the
    // aggregate-routing half: folding tag-matched events into an aggregate, and the boundary that
    // appends back to a tag-derived stream under an optimistic consistency check.

    private const string TagsMessage =
        "This part of the Dynamic Consistency Boundary surface is not implemented in Fisher yet. " +
        "Tag tables, tagged appends, QueryByTagsAsync and EventsExistAsync work; aggregate routing " +
        "by tag does not.";

}
