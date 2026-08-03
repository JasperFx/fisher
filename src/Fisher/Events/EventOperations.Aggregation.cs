using Fisher.Storage;

namespace Fisher.Events;

/// <summary>
///     Live aggregation: fold a stream's events into an aggregate in memory, without persisting
///     anything.
/// </summary>
public partial class EventOperations
{
    /// <summary>
    ///     Fold a stream's events into an aggregate of type <typeparamref name="T" />.
    /// </summary>
    /// <remarks>
    ///     Nothing is stored — the aggregate is rebuilt from the events on every call. The aggregate
    ///     type is discovered by convention (its own <c>Create</c> / <c>Apply</c> / <c>ShouldDelete</c>
    ///     methods) the first time it is asked for, and the resulting aggregator is cached on the event
    ///     graph.
    /// </remarks>
    /// <param name="streamId">The stream to fold.</param>
    /// <param name="version">When greater than 0, fold only up to and including this version.</param>
    /// <param name="timestamp">When supplied, fold only events at or before this time.</param>
    /// <param name="state">An aggregate to continue from, rather than starting from nothing.</param>
    /// <param name="fromVersion">
    ///     When greater than 0, fold only from this version onward. Meaningful with
    ///     <paramref name="state" />, which supplies everything before it.
    /// </param>
    /// <returns>
    ///     The aggregate, or null when the stream has no events and no <paramref name="state" /> was
    ///     supplied, or when the fold deleted it.
    /// </returns>
    public Task<T?> AggregateStreamAsync<T>(Guid streamId, long version = 0, DateTimeOffset? timestamp = null,
        T? state = null, long fromVersion = 0, CancellationToken token = default) where T : class
    {
        AssertGuidIdentity();
        return AggregateStreamAsync((object)streamId, version, timestamp, state, fromVersion, token);
    }

    /// <inheritdoc
    ///     cref="AggregateStreamAsync{T}(Guid,long,System.Nullable{DateTimeOffset},T,long,CancellationToken)" />
    public Task<T?> AggregateStreamAsync<T>(string streamKey, long version = 0, DateTimeOffset? timestamp = null,
        T? state = null, long fromVersion = 0, CancellationToken token = default) where T : class
    {
        AssertStringIdentity();
        return AggregateStreamAsync((object)streamKey, version, timestamp, state, fromVersion, token);
    }

    private async Task<T?> AggregateStreamAsync<T>(object streamId, long version, DateTimeOffset? timestamp,
        T? state, long fromVersion, CancellationToken token) where T : class
    {
        var events = await FetchStreamAsync(streamId, version, timestamp, fromVersion, token).ConfigureAwait(false);

        if (events.Count == 0)
        {
            return state;
        }

        var aggregate = await Graph.AggregatorFor<T>()
            .BuildAsync(events, _session, state, token).ConfigureAwait(false);

        if (aggregate is null)
        {
            return null;
        }

        // The aggregator folds events; it does not know the stream it folded. An aggregate whose
        // Create method never assigns Id would otherwise come back with a default one.
        AggregateIdentity.TrySetIdentity(aggregate, streamId);

        return aggregate;
    }
}
