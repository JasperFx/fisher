using Fisher.Internal;
using JasperFx.Events;
using JasperFx.Events.Tags;

namespace Fisher.Events.Tags;

/// <summary>
///     Fisher's <see cref="IEventBoundary{T}" /> — a writable handle over every stream whose events
///     match a tag query.
/// </summary>
/// <remarks>
///     <para>
///         Unlike an <see cref="IEventStream{T}" />, which is pinned to one stream, a boundary spans
///         whatever streams the tag query reached. An event appended to it is therefore <em>routed</em>
///         rather than simply added: see <see cref="AppendOne" />.
///     </para>
///     <para>
///         The consistency check does not live here. The boundary records
///         <see cref="LastSeenSequence" /> at construction, and the session re-runs the tag query inside
///         its write transaction at <c>SaveChangesAsync</c> — see
///         <c>FisherSession.AssertBoundariesAreStillConsistentAsync</c>. Checking here would be
///         checking outside the lock, which proves nothing.
///     </para>
/// </remarks>
internal sealed class FisherEventBoundary<T> : IEventBoundary<T> where T : class
{
    private readonly FisherSession _session;
    private readonly EventGraph _graph;

    internal FisherEventBoundary(FisherSession session, EventGraph graph, EventTagQuery query, T? aggregate,
        IReadOnlyList<IEvent> events)
    {
        _session = session;
        _graph = graph;
        Query = query;
        Aggregate = aggregate;
        Events = events;

        // Zero when nothing matched, which still enforces consistency: any matching event appearing
        // later has a sequence above zero. That is what makes a boundary over an empty result a
        // usable "this must not exist yet" assertion.
        LastSeenSequence = events.Count == 0 ? 0 : events.Max(x => x.Sequence);
    }

    internal EventTagQuery Query { get; }

    public T? Aggregate { get; }

    public long LastSeenSequence { get; }

    public IReadOnlyList<IEvent> Events { get; }

    public void AppendOne(object @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var wrapped = @event as IEvent ?? _graph.BuildEvent(@event);
        var streamId = StreamIdFor(ResolveTags(wrapped));

        if (_graph.StreamIdentity == StreamIdentity.AsGuid)
        {
            _session.Events.Append((Guid)streamId, [wrapped]);
        }
        else
        {
            _session.Events.Append(streamId.ToString()!, [wrapped]);
        }
    }

    public void AppendMany(params object[] events) => AppendMany((IEnumerable<object>)events);

    public void AppendMany(IEnumerable<object> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        foreach (var @event in events)
        {
            AppendOne(@event);
        }
    }

    /// <summary>
    ///     The event's own tags, or tags inferred from its public properties.
    /// </summary>
    /// <remarks>
    ///     An event with neither is a hard error rather than an untagged append. A boundary has no
    ///     single stream to fall back on, so an untagged event has nowhere to go — and silently
    ///     inventing a stream for it would put the event somewhere no tag query could ever find it.
    /// </remarks>
    private IReadOnlyList<EventTag> ResolveTags(IEvent @event)
    {
        if (@event.Tags is { Count: > 0 })
        {
            return @event.Tags;
        }

        var inferred = EventTagInference.InferTags(@event.Data, _graph.TagTypes);

        if (inferred.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot append '{@event.Data.GetType().Name}' to a DCB boundary: it carries no tags and none "
                + "could be inferred from its properties. Set them explicitly with WithTag(), or give the event "
                + "a property of a registered tag type.");
        }

        foreach (var tag in inferred)
        {
            @event.WithTag(tag.Value);
        }

        return @event.Tags!;
    }

    /// <summary>
    ///     Route a tagged event to a stream.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The stream is derived from the first tag whose registration names an aggregate type: that
    ///         tag is the consistency boundary the aggregate is built over, so its value <em>is</em> the
    ///         stream identity. Appending — rather than starting — is deliberate, because the derived
    ///         stream may well already exist from an earlier boundary write, and
    ///         <c>StartStream</c> would collide. Fisher's <c>Append</c> creates the stream when it does
    ///         not exist, so one call covers both.
    ///     </para>
    ///     <para>
    ///         When no tag names an aggregate type there is no boundary to route to, and each event gets
    ///         its own new stream. That is not a fallback so much as the honest answer: an unrouted tag
    ///         says the event belongs to no aggregate.
    ///     </para>
    /// </remarks>
    private object StreamIdFor(IReadOnlyList<EventTag> tags)
    {
        foreach (var tag in tags)
        {
            var registration = _graph.FindTagType(tag.TagType);

            if (registration?.AggregateType != null)
            {
                return registration.ExtractValue(tag.Value);
            }
        }

        return _graph.StreamIdentity == StreamIdentity.AsGuid
            ? Guid.NewGuid()
            : Guid.NewGuid().ToString();
    }
}
