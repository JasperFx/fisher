using JasperFx.Events;

namespace Fisher.Projections.Vectors;

/// <summary>
///     Declares which events a <see cref="VectorProjection{TDoc,TId}" /> embeds, and which delete
///     (fisher#261).
/// </summary>
/// <remarks>
///     <para>
///         <b>⚠️ There is no <c>Delete&lt;TEvent&gt;()</c> without an id selector, and that absence is
///         the fix for a real defect rather than an omission.</b> Marten's template reads
///         <c>@event.StreamId</c> on its delete path unconditionally and ignores the configured id
///         selector entirely — so a projection keyed on a payload member writes rows under one id and
///         deletes under another, and the delete silently matches nothing. The row stays in the index
///         forever, which for a deletion is the worst possible direction to fail in.
///     </para>
///     <para>
///         Requiring the selector on both sides makes the two structurally incapable of disagreeing,
///         which is better than checking that they agree. The common case costs <c>e =&gt; e.StreamId</c>
///         — six characters of honesty.
///     </para>
/// </remarks>
public sealed class VectorProjectionMap<TDoc, TId>
    where TDoc : class, IVectorized<TId>, new()
    where TId : notnull
{
    private readonly Dictionary<Type, Mapping> _content = new();
    private readonly Dictionary<Type, Func<IEvent, TId>> _deletes = new();

    internal bool IsEmpty => _content.Count == 0 && _deletes.Count == 0;

    /// <summary>
    ///     Embed <typeparamref name="TEvent" />: <paramref name="content" /> is the text,
    ///     <paramref name="id" /> the document it belongs to.
    /// </summary>
    /// <param name="content">
    ///     The text to embed. <b>Returning null means "this event carries no content"</b> and skips it,
    ///     which is a real answer — a status change on an otherwise indexable entity, say. Throwing is
    ///     a different thing and is not caught; see the class remarks on
    ///     <see cref="VectorProjection{TDoc,TId}" />.
    /// </param>
    /// <param name="id">The document id this event's content belongs to.</param>
    public VectorProjectionMap<TDoc, TId> Map<TEvent>(
        Func<IEvent<TEvent>, string?> content,
        Func<IEvent<TEvent>, TId> id) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(id);

        if (_content.ContainsKey(typeof(TEvent)))
        {
            throw new InvalidOperationException(
                $"'{typeof(TEvent).Name}' is already mapped. Two mappings for one event type would "
                + "leave which one wins depending on registration order.");
        }

        _content[typeof(TEvent)] = new Mapping(
            e => content((IEvent<TEvent>)e),
            e => id((IEvent<TEvent>)e));

        return this;
    }

    /// <summary>
    ///     Remove the document <paramref name="id" /> names when <typeparamref name="TEvent" /> is
    ///     seen.
    /// </summary>
    /// <param name="id">
    ///     The document to delete. Required — see the class remarks for the defect that makes a
    ///     convenience overload defaulting to the stream id the wrong offer.
    /// </param>
    public VectorProjectionMap<TDoc, TId> Delete<TEvent>(Func<IEvent<TEvent>, TId> id)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(id);

        if (_content.ContainsKey(typeof(TEvent)))
        {
            throw new InvalidOperationException(
                $"'{typeof(TEvent).Name}' is mapped for content and for deletion, so one event would "
                + "both write and remove the same document and the outcome would depend on which "
                + "branch ran first.");
        }

        if (!_deletes.TryAdd(typeof(TEvent), e => id((IEvent<TEvent>)e)))
        {
            throw new InvalidOperationException($"'{typeof(TEvent).Name}' is already mapped for deletion.");
        }

        return this;
    }

    internal bool TryDelete(IEvent @event, out TId id)
    {
        if (_deletes.TryGetValue(@event.EventType, out var selector))
        {
            id = selector(@event);
            return true;
        }

        id = default!;
        return false;
    }

    internal bool TryContent(IEvent @event, out TId id, out string? content)
    {
        if (_content.TryGetValue(@event.EventType, out var mapping))
        {
            // ⚠️ NOT wrapped in a try/catch, and that is the point. Marten's template catches
            // everything a selector throws and returns null, which the caller reads as "no content
            // for this event" -- so a selector with a bug drops the document out of the index with
            // nothing reported anywhere. A throw here faults the shard, which is what the daemon's
            // error handling is for and is the only outcome an operator can act on.
            id = mapping.Id(@event);
            content = mapping.Content(@event);
            return true;
        }

        id = default!;
        content = null;
        return false;
    }

    private sealed record Mapping(Func<IEvent, string?> Content, Func<IEvent, TId> Id);
}
