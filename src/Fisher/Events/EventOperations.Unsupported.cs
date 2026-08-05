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

    /// <inheritdoc />
    public Task<T?> AggregateByTagsAsync<T>(EventTagQuery query, CancellationToken cancellation = default)
        where T : class
        => throw new NotImplementedException(TagsMessage);

    /// <inheritdoc />
    public Task<IEventBoundary<T>> FetchForWritingByTags<T>(EventTagQuery query,
        CancellationToken cancellation = default) where T : class
        => throw new NotImplementedException(TagsMessage);

    // ---- Event rewriting ----
    //
    // Both of these mutate events that are already committed, which Fisher has no operation for. The
    // schema supports it — fi_events rows are updatable — so this is a missing operation rather than
    // a missing capability.

    private const string RewriteMessage =
        "Fisher cannot rewrite committed events yet. There is no update operation for a persisted event row.";

    /// <inheritdoc />
    public void OverwriteEvent(IEvent e) => throw new NotImplementedException(RewriteMessage);

    /// <inheritdoc />
    public Guid CompletelyReplaceEvent<T>(long sequence, T eventBody) where T : class
        => throw new NotImplementedException(RewriteMessage);
}
