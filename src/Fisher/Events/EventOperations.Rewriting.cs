using Fisher.Events.Protected;
using JasperFx.Events;

namespace Fisher.Events;

/// <summary>
///     Rewriting events that are already committed.
/// </summary>
/// <remarks>
///     <para>
///         Both members queue onto the session's unit of work rather than executing, so a rewrite
///         commits in the same transaction as whatever else the session is doing — which is what lets
///         event data masking rewrite a batch of events atomically.
///     </para>
///     <para>
///         <b>Neither member reaches an async projection that has already run.</b> The daemon's
///         high-water mark is a sequence and a rewrite does not move it, so a shard that has passed the
///         event will never read the new body; a projection whose state depends on the old one stays
///         wrong until it is rebuilt. Marten behaves the same way. This is why event data masking is
///         described as a data-at-rest operation: it changes what is stored, not what has already been
///         derived from it.
///     </para>
/// </remarks>
public partial class EventOperations
{
    /// <summary>
    ///     Rewrite this event's body — and its headers, where the store keeps them — as the instance
    ///     now stands.
    /// </summary>
    /// <remarks>
    ///     The event is matched by its sequence, so it has to be one that was read back from the store
    ///     rather than one built in memory. An <see cref="IEvent" /> with no sequence matches no row and
    ///     the update silently affects nothing, which is worth failing on rather than discovering later.
    /// </remarks>
    public void OverwriteEvent(IEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Sequence <= 0)
        {
            throw new ArgumentException(
                "This event has no sequence, so there is no persisted row to overwrite. Overwrite an "
                + "event read back from the store, not one built in memory.", nameof(e));
        }

        _session.QueueOperation(new OverwriteEventOperation(Graph, e));
    }

    /// <summary>
    ///     Replace the event at a sequence with a different body, of a possibly different type.
    /// </summary>
    /// <returns>The replacement event's new id.</returns>
    /// <remarks>
    ///     The row keeps its stream, version, sequence and timestamp and takes a new identity, because
    ///     it is no longer the event that was appended. Registering the replacement's type is what makes
    ///     it readable afterwards — an unregistered <c>dotnet_type</c> is skipped by the stream reads
    ///     and throws in the daemon's loader.
    /// </remarks>
    public Guid CompletelyReplaceEvent<T>(long sequence, T eventBody) where T : class
    {
        ArgumentNullException.ThrowIfNull(eventBody);

        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence),
                "An event sequence is assigned by the database and starts at 1.");
        }

        var operation = new ReplaceEventOperation(Graph, sequence, eventBody);
        _session.QueueOperation(operation);

        return operation.Id;
    }

    /// <summary>
    ///     Permanently remove a set of events by sequence, and any tag rows pointing at them.
    /// </summary>
    /// <remarks>
    ///     Internal for now: this is the destructive half of stream compacting (fisher#10) and has no
    ///     caller outside it. There is deliberately no public "delete these events" surface, because the
    ///     safe uses of one all go through a higher-level operation that decides what replaces them.
    /// </remarks>
    internal void DeleteEvents(long[] sequences)
    {
        ArgumentNullException.ThrowIfNull(sequences);

        if (sequences.Length == 0)
        {
            return;
        }

        _session.QueueOperation(new DeleteEventsOperation(Graph, sequences));
    }
}
