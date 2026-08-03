using JasperFx.Events;

namespace Fisher.Events;

/// <summary>
///     The writable view of one stream handed back by <c>FetchForWriting</c>: the aggregate as it
///     stands, plus an append surface whose events land in the session's unit of work.
/// </summary>
/// <remarks>
///     This wraps a <see cref="StreamAction" /> that is already tracked by
///     <see cref="EventOperations" />, so appending through it and appending through the session reach
///     the same place. The expected version was captured when the stream was fetched, which is what
///     makes the eventual write optimistic.
/// </remarks>
internal class EventStream<T> : IEventStream<T> where T : class
{
    private readonly EventOperations _operations;
    private readonly Func<object, IEvent> _wrapper;
    private StreamAction _stream;

    private EventStream(EventOperations operations, StreamAction stream, T? aggregate,
        CancellationToken cancellation, Action<IEvent> stamp)
    {
        _operations = operations;
        _stream = stream;
        _stream.AggregateType = typeof(T);

        _wrapper = data =>
        {
            var @event = operations.BuildEvent(data);
            stamp(@event);
            return @event;
        };

        Aggregate = aggregate;
        Cancellation = cancellation;
    }

    internal static EventStream<T> ForGuid(EventOperations operations, StreamAction stream, Guid streamId,
        T? aggregate, CancellationToken cancellation)
        => new(operations, stream, aggregate, cancellation, e => e.StreamId = streamId);

    internal static EventStream<T> ForString(EventOperations operations, StreamAction stream, string streamKey,
        T? aggregate, CancellationToken cancellation)
        => new(operations, stream, aggregate, cancellation, e => e.StreamKey = streamKey);

    public T? Aggregate { get; }

    public Guid Id => _stream.Id;

    public string? Key => _stream.Key;

    public long? StartingVersion => _stream.ExpectedVersionOnServer;

    public long? CurrentVersion => _stream.ExpectedVersionOnServer is null
        ? null
        : _stream.ExpectedVersionOnServer.Value + _stream.Events.Count;

    public CancellationToken Cancellation { get; }

    public IReadOnlyList<IEvent> Events => _stream.Events;

    public bool AlwaysEnforceConsistency
    {
        get => _stream.AlwaysEnforceConsistency;
        set => _stream.AlwaysEnforceConsistency = value;
    }

    public void AppendOne(object @event) => _stream.AddEvent(_wrapper(@event));

    public void AppendMany(params object[] events) => AppendMany((IEnumerable<object>)events);

    public void AppendMany(IEnumerable<object> events)
        => _stream.AddEvents(events.Select(_wrapper).ToArray());

    /// <summary>
    ///     Re-arm this stream for another round of appends after its events were committed, dropping
    ///     the expected-version guard that the commit already satisfied.
    /// </summary>
    public void TryFastForwardVersion()
    {
        if (_operations.IsTracking(_stream))
        {
            return;
        }

        _stream = _stream.FastForward();
        _operations.TrackFetched(_stream);
    }
}
