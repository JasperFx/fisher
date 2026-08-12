using Fisher.Internal;
using JasperFx.Events;

namespace Fisher.Events;

/// <summary>
///     The event store surface on a Fisher session, implementing JasperFx's shared
///     <see cref="IEventStoreOperations" /> — the interface the cross-store compliance suites run
///     everything through.
/// </summary>
/// <remarks>
///     Stream actions are accumulated here and turned into storage operations by
///     <see cref="AppendPlanner" /> during <c>SaveChangesAsync</c>, at which point the current server
///     version of each existing stream is known and event versions can be assigned.
/// </remarks>
public partial class EventOperations : IEventStoreOperations
{
    private readonly Dictionary<object, StreamAction> _streams = new();
    private readonly FisherSession _session;
    private readonly string? _tenantId;

    internal EventOperations(FisherSession session, string? tenantId = null)
    {
        _session = session;
        _tenantId = tenantId;
    }

    /// <summary>
    ///     The tenant every append and every read here is scoped to.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The session's own, unless this is a tenant scope's event surface — see
    ///         <see cref="ITenantOperations" /> (fisher#33). Everything in this class reads it rather
    ///         than the session's, which is the entire mechanism: an appended <c>StreamAction</c> is
    ///         stamped with it here, and <c>AppendPlanner</c> already writes each stream's own tenant
    ///         rather than the session's, so a cross-tenant append needed no change to the write path.
    ///     </para>
    ///     <para>
    ///         A tenant scope gets its own <c>EventOperations</c>, and therefore its own
    ///         <see cref="_streams" /> dictionary. That is not tidiness: the dictionary is keyed by
    ///         stream id, and under conjoined tenancy the same id in two tenants is two different
    ///         streams — sharing one dictionary would silently merge them.
    ///     </para>
    /// </remarks>
    internal string TenantId
    {
        get
        {
            if (_tenantId is null)
            {
                return _session.TenantId;
            }

            // The event-store half of the rule FisherSession.StorageFor applies to documents: with
            // TenancyStyle.Single there is no tenant_id on fi_streams or fi_events, so a scoped append
            // would write the one unscoped stream space and a scoped read would answer about it — both
            // silently. Refused rather than ignored.
            if (Graph.TenancyStyle != JasperFx.MultiTenancy.TenancyStyle.Conjoined)
            {
                throw new InvalidOperationException(
                    $"The event store is not multi-tenanted, so it cannot be reached through ForTenant("
                    + $"\"{_tenantId}\"): fi_streams and fi_events have no tenant_id column, and both the "
                    + "append and the read would use the one stream space every tenant shares. Set "
                    + "StoreOptions.Events.TenancyStyle = TenancyStyle.Conjoined before the schema is "
                    + "created, or use the session itself.");
            }

            return _tenantId;
        }
    }

    private EventGraph Graph => _session.EventGraph;

    /// <summary>
    ///     Every stream touched in this unit of work, keyed by stream id or key.
    /// </summary>
    /// <remarks>
    ///     Public because an integration has to read it <b>before</b> the commit. Wolverine's fast event
    ///     forwarding publishes appended events as messages from an
    ///     <c>IDocumentSessionListener.BeforeSaveChangesAsync</c>, and its append tracking notifies the
    ///     runtime observer from the same hook; both need the pending streams, and
    ///     <see cref="Services.IChangeSet.GetStreams" /> only exists afterwards. Marten and Polecat both
    ///     expose the equivalent, which is what let wolverine#3907 port the two listeners unchanged.
    ///     Reading it does not commit or clear anything.
    /// </remarks>
    public IReadOnlyCollection<StreamAction> PendingStreams => _streams.Values;

    internal void ClearPendingStreams() => _streams.Clear();

    /// <summary>
    ///     Wrap raw event data in an <see cref="IEvent" /> envelope carrying its type metadata,
    ///     without appending it. Use this when the envelope's metadata — correlation id, headers, tags
    ///     — has to be set before the event is appended.
    /// </summary>
    public IEvent BuildEvent(object eventData) => Graph.BuildEvent(eventData);

    /// <summary>
    ///     Mark a stream and all of its events archived, so the async daemon skips them. Queued until
    ///     <c>SaveChangesAsync</c>.
    /// </summary>
    public void ArchiveStream(Guid streamId)
    {
        AssertGuidIdentity();
        _session.QueueOperation(Graph.ArchiveStreamOperation(streamId, TenantId, true));
    }

    /// <inheritdoc cref="ArchiveStream(Guid)" />
    public void ArchiveStream(string streamKey)
    {
        AssertStringIdentity();
        _session.QueueOperation(Graph.ArchiveStreamOperation(streamKey, TenantId, true));
    }

    /// <summary>
    ///     Reverse <see cref="ArchiveStream(Guid)" />.
    /// </summary>
    public void UnArchiveStream(Guid streamId)
    {
        AssertGuidIdentity();
        _session.QueueOperation(Graph.ArchiveStreamOperation(streamId, TenantId, false));
    }

    /// <inheritdoc cref="UnArchiveStream(Guid)" />
    public void UnArchiveStream(string streamKey)
    {
        AssertStringIdentity();
        _session.QueueOperation(Graph.ArchiveStreamOperation(streamKey, TenantId, false));
    }

    /// <summary>
    ///     Hard-delete a stream and every event in it. Queued until <c>SaveChangesAsync</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Unlike <see cref="ArchiveStream(Guid)" />, which only sets a flag, this destroys data —
    ///         the events are gone and cannot be re-read or re-projected. Archiving is what you want
    ///         for a stream that is merely finished.
    ///     </para>
    ///     <para>
    ///         The deleted events keep their <c>seq_id</c> values reserved, because
    ///         <c>AUTOINCREMENT</c> never reuses a sequence number. That is what stops a tombstoned
    ///         stream from hiding later events behind an async projection's high-water mark — see
    ///         CLAUDE.md.
    ///     </para>
    /// </remarks>
    public void TombstoneStream(Guid streamId)
    {
        AssertGuidIdentity();
        _session.QueueOperation(Graph.TombstoneStreamOperation(streamId, TenantId));
    }

    /// <inheritdoc cref="TombstoneStream(Guid)" />
    public void TombstoneStream(string streamKey)
    {
        AssertStringIdentity();
        _session.QueueOperation(Graph.TombstoneStreamOperation(streamKey, TenantId));
    }

    // ---- StartStream ----

    public StreamAction StartStream<TAggregate>(Guid id, params object[] events) where TAggregate : class
        => StartStream(typeof(TAggregate), id, events);

    public StreamAction StartStream<TAggregate>(Guid id, IEnumerable<object> events) where TAggregate : class
        => StartStream(typeof(TAggregate), id, events.ToArray());

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
        stream.TenantId = TenantId;

        return Track(id, stream);
    }

    public StreamAction StartStream(string streamKey, IEnumerable<object> events)
        => StartStream(streamKey, events.ToArray());

    public StreamAction StartStream(string streamKey, params object[] events)
    {
        AssertStringIdentity();

        var stream = StreamAction.Start(Graph, streamKey, events);
        stream.TenantId = TenantId;

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

    public StreamAction Append(Guid stream, long expectedVersion, IEnumerable<object> events)
        => Append(stream, expectedVersion, events.ToArray());

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
        stream.TenantId = TenantId;

        return Track(key, stream);
    }

    private StreamAction Track(object key, StreamAction stream)
    {
        _streams[key] = stream;
        return stream;
    }

    /// <summary>
    ///     Whether this stream action is the one currently tracked for its stream.
    /// </summary>
    internal bool IsTracking(StreamAction stream)
        => _streams.TryGetValue(KeyFor(stream), out var tracked) && ReferenceEquals(tracked, stream);

    /// <summary>
    ///     Re-track a stream action that replaced the one previously held for its stream — how
    ///     <c>IEventStream.TryFastForwardVersion</c> re-arms a stream after its events were committed.
    /// </summary>
    internal void TrackFetched(StreamAction stream) => Track(KeyFor(stream), stream);

    private object KeyFor(StreamAction stream)
        => IsGuidIdentity ? stream.Id : stream.Key!;

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
