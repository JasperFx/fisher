using JasperFx.Events;

namespace Fisher.Batching;

/// <summary>
///     Query plan for the high level metadata of a single event stream, identified by either a Guid
///     stream id or a string stream key. Yields null if the stream does not exist.
/// </summary>
/// <remarks>
///     <para>
///         Parity with Polecat's <c>FetchStreamStatePlan</c> (polecat#370, marten#5053), and enrolled
///         in <c>StreamQueryPlanCompliance</c>. Implements <b>both</b> <see cref="IQueryPlan{T}" /> and
///         <see cref="IBatchQueryPlan{T}" />, which matters beyond convenience: a plan that is only an
///         <see cref="IBatchQueryPlan{T}" /> produces uncompilable generated code through Wolverine's
///         fetch-specification feature.
///     </para>
///     <para>
///         <b>The batched half forwards to the unbatched one, where Polecat's composes SQL.</b> Its
///         batch reaches an <c>Events</c> surface that contributes a fragment to a combined command;
///         Fisher's <see cref="IBatchedQuery" /> executes each item in turn on one connection, because
///         SQLite is embedded and there are no round trips to collapse — the batch exists so DCB and
///         document code ports between the stores unchanged. So there is one query here rather than
///         two implementations that could drift, and the compliance suite's <c>batched</c> axis is
///         asserting sameness that Fisher gets structurally.
///     </para>
/// </remarks>
public class FetchStreamStatePlan : IQueryPlan<StreamState?>, IBatchQueryPlan<StreamState?>
{
    private readonly Guid _streamId;
    private readonly string? _streamKey;

    /// <summary>
    ///     Fetch the stream state for the stream identified by <paramref name="streamId" />.
    /// </summary>
    public FetchStreamStatePlan(Guid streamId)
    {
        _streamId = streamId;
    }

    /// <summary>
    ///     Fetch the stream state for the stream identified by <paramref name="streamKey" />.
    /// </summary>
    public FetchStreamStatePlan(string streamKey)
    {
        _streamKey = streamKey ?? throw new ArgumentNullException(nameof(streamKey));
    }

    public Task<StreamState?> Fetch(IQuerySession session, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(session);

        return _streamKey is not null
            ? session.Events.FetchStreamStateAsync(_streamKey, token)
            : session.Events.FetchStreamStateAsync(_streamId, token);
    }

    /// <inheritdoc cref="FetchStreamStatePlan" />
    public Task<StreamState?> Fetch(IBatchedQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        return query.QueryByPlan(this);
    }
}

/// <summary>
///     Query plan for the raw events of a single event stream, identified by either a Guid stream id
///     or a string stream key, carrying <c>FetchStream</c>'s optional <c>version</c> /
///     <c>timestamp</c> / <c>fromVersion</c> filters. Yields an empty list if the stream does not
///     exist.
/// </summary>
/// <inheritdoc cref="FetchStreamStatePlan" path="/remarks" />
public class FetchStreamPlan : IQueryPlan<IReadOnlyList<IEvent>>, IBatchQueryPlan<IReadOnlyList<IEvent>>
{
    private readonly Guid _streamId;
    private readonly string? _streamKey;
    private readonly long _version;
    private readonly DateTimeOffset? _timestamp;
    private readonly long _fromVersion;

    /// <summary>
    ///     Fetch the events for the stream identified by <paramref name="streamId" />.
    /// </summary>
    /// <param name="streamId">The stream's identity.</param>
    /// <param name="version">If set, queries for events up to and including this version.</param>
    /// <param name="timestamp">If set, queries for events captured on or before this timestamp.</param>
    /// <param name="fromVersion">If set, queries for events on or from this version.</param>
    public FetchStreamPlan(Guid streamId, long version = 0, DateTimeOffset? timestamp = null,
        long fromVersion = 0)
    {
        _streamId = streamId;
        _version = version;
        _timestamp = timestamp;
        _fromVersion = fromVersion;
    }

    /// <inheritdoc cref="FetchStreamPlan(Guid, long, DateTimeOffset?, long)" />
    public FetchStreamPlan(string streamKey, long version = 0, DateTimeOffset? timestamp = null,
        long fromVersion = 0)
    {
        _streamKey = streamKey ?? throw new ArgumentNullException(nameof(streamKey));
        _version = version;
        _timestamp = timestamp;
        _fromVersion = fromVersion;
    }

    public Task<IReadOnlyList<IEvent>> Fetch(IQuerySession session, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(session);

        return _streamKey is not null
            ? session.Events.FetchStreamAsync(_streamKey, _version, _timestamp, _fromVersion, token)
            : session.Events.FetchStreamAsync(_streamId, _version, _timestamp, _fromVersion, token);
    }

    /// <inheritdoc cref="FetchStreamStatePlan.Fetch(IBatchedQuery)" />
    public Task<IReadOnlyList<IEvent>> Fetch(IBatchedQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        return query.QueryByPlan(this);
    }
}
