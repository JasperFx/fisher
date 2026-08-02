using Fisher.Internal;
using JasperFx.Events;

namespace Fisher.Events;

/// <summary>
///     The event store write surface on a Fisher session, implementing JasperFx's shared
///     <see cref="IEventOperations" />.
/// </summary>
/// <remarks>
///     Stream actions are accumulated here and turned into storage operations by
///     <see cref="AppendPlanner" /> during <c>SaveChangesAsync</c>, at which point the current server
///     version of each existing stream is known and event versions can be assigned.
/// </remarks>
public class EventOperations : IEventOperations
{
    private readonly Dictionary<object, StreamAction> _streams = new();
    private readonly FisherSession _session;

    internal EventOperations(FisherSession session)
    {
        _session = session;
    }

    private EventGraph Graph => _session.EventGraph;

    /// <summary>
    ///     Every stream touched in this unit of work, keyed by stream id or key.
    /// </summary>
    internal IReadOnlyCollection<StreamAction> PendingStreams => _streams.Values;

    internal void ClearPendingStreams() => _streams.Clear();

    // ---- StartStream ----

    public StreamAction StartStream<TAggregate>(Guid id, params object[] events) where TAggregate : class
        => StartStream(typeof(TAggregate), id, events);

    public StreamAction StartStream(Type aggregateType, Guid id, IEnumerable<object> events)
        => StartStream(aggregateType, id, events.ToArray());

    public StreamAction StartStream(Type aggregateType, Guid id, params object[] events)
    {
        var stream = StartStream(id, events);
        stream.AggregateType = aggregateType;
        return stream;
    }

    public StreamAction StartStream<TAggregate>(string streamKey, IEnumerable<object> events) where TAggregate : class
        => StartStream(typeof(TAggregate), streamKey, events.ToArray());

    public StreamAction StartStream<TAggregate>(string streamKey, params object[] events) where TAggregate : class
        => StartStream(typeof(TAggregate), streamKey, events);

    public StreamAction StartStream(Type aggregateType, string streamKey, IEnumerable<object> events)
        => StartStream(aggregateType, streamKey, events.ToArray());

    public StreamAction StartStream(Type aggregateType, string streamKey, params object[] events)
    {
        var stream = StartStream(streamKey, events);
        stream.AggregateType = aggregateType;
        return stream;
    }

    public StreamAction StartStream(Guid id, IEnumerable<object> events) => StartStream(id, events.ToArray());

    public StreamAction StartStream(Guid id, params object[] events)
    {
        AssertGuidIdentity();

        var stream = StreamAction.Start(Graph, id, events);
        stream.TenantId = _session.TenantId;

        return Track(id, stream);
    }

    public StreamAction StartStream(string streamKey, IEnumerable<object> events)
        => StartStream(streamKey, events.ToArray());

    public StreamAction StartStream(string streamKey, params object[] events)
    {
        AssertStringIdentity();

        var stream = StreamAction.Start(Graph, streamKey, events);
        stream.TenantId = _session.TenantId;

        return Track(streamKey, stream);
    }

    public StreamAction StartStream<TAggregate>(IEnumerable<object> events) where TAggregate : class
        => StartStream(typeof(TAggregate), events.ToArray());

    public StreamAction StartStream<TAggregate>(params object[] events) where TAggregate : class
        => StartStream(typeof(TAggregate), events);

    public StreamAction StartStream(Type aggregateType, IEnumerable<object> events)
        => StartStream(aggregateType, events.ToArray());

    public StreamAction StartStream(Type aggregateType, params object[] events)
    {
        var stream = StartStream(events);
        stream.AggregateType = aggregateType;
        return stream;
    }

    public StreamAction StartStream(IEnumerable<object> events) => StartStream(events.ToArray());

    public StreamAction StartStream(params object[] events)
        => Graph.StreamIdentity == StreamIdentity.AsGuid
            ? StartStream(Guid.NewGuid(), events)
            : StartStream(Guid.NewGuid().ToString(), events);

    // ---- Append ----

    public StreamAction Append(Guid stream, IEnumerable<object> events) => Append(stream, events.ToArray());

    public StreamAction Append(Guid stream, params object[] events)
    {
        AssertGuidIdentity();
        return AppendTo(stream, () => StreamAction.Append(Graph, stream, events), events);
    }

    public StreamAction Append(string stream, IEnumerable<object> events) => Append(stream, events.ToArray());

    public StreamAction Append(string stream, params object[] events)
    {
        AssertStringIdentity();
        return AppendTo(stream, () => StreamAction.Append(Graph, stream, events), events);
    }

    public StreamAction Append(Guid stream, long expectedVersion, params object[] events)
    {
        var action = Append(stream, events);
        action.ExpectedVersionOnServer = expectedVersion;
        return action;
    }

    public StreamAction Append(string stream, long expectedVersion, IEnumerable<object> events)
        => Append(stream, expectedVersion, events.ToArray());

    public StreamAction Append(string stream, long expectedVersion, params object[] events)
    {
        var action = Append(stream, events);
        action.ExpectedVersionOnServer = expectedVersion;
        return action;
    }

    /// <summary>
    ///     Append to a stream already tracked in this unit of work, or start tracking it.
    /// </summary>
    /// <remarks>
    ///     Two appends to the same stream in one session must merge into one <see cref="StreamAction" />.
    ///     Leaving them separate would produce two stream-row writes for the same stream in one
    ///     transaction, the second of which would fail its expected-version guard.
    /// </remarks>
    private StreamAction AppendTo(object key, Func<StreamAction> create, object[] events)
    {
        if (_streams.TryGetValue(key, out var existing))
        {
            existing.AddEvents(events.Select(Graph.BuildEvent).ToArray());
            return existing;
        }

        var stream = create();
        stream.TenantId = _session.TenantId;

        return Track(key, stream);
    }

    private StreamAction Track(object key, StreamAction stream)
    {
        _streams[key] = stream;
        return stream;
    }

    private void AssertGuidIdentity()
    {
        if (Graph.StreamIdentity != StreamIdentity.AsGuid)
        {
            throw new InvalidOperationException(
                "This event store is configured for string stream identity (StreamIdentity.AsString); " +
                "use the string stream key overloads.");
        }
    }

    private void AssertStringIdentity()
    {
        if (Graph.StreamIdentity != StreamIdentity.AsString)
        {
            throw new InvalidOperationException(
                "This event store is configured for Guid stream identity (StreamIdentity.AsGuid); " +
                "use the Guid stream id overloads.");
        }
    }
}
