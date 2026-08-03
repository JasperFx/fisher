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
    // All of these need the fi_event_tag_* tables and the tag registration pipeline. EventGraph
    // already accepts RegisterTagType<TTag>, but nothing reads or writes a tag table yet.

    private const string TagsMessage =
        "Dynamic Consistency Boundary tags are not implemented in Fisher yet — there are no tag tables " +
        "to query. EventGraph.RegisterTagType exists, but nothing reads or writes them.";

    /// <inheritdoc />
    public void AssignTagWhere(Expression<Func<IEvent, bool>> expression, object tag)
        => throw new NotImplementedException(TagsMessage);

    /// <inheritdoc />
    public Task<bool> EventsExistAsync(EventTagQuery query, CancellationToken cancellation = default)
        => throw new NotImplementedException(TagsMessage);

    /// <inheritdoc />
    public Task<IReadOnlyList<IEvent>> QueryByTagsAsync(EventTagQuery query,
        CancellationToken cancellation = default)
        => throw new NotImplementedException(TagsMessage);

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
