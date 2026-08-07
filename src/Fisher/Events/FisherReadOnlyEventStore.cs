using JasperFx.Events;

namespace Fisher.Events;

/// <summary>
///     Fisher's <see cref="IReadOnlyEventStore" /> — the read-only slice of the event store that
///     monitoring tools reach through <c>IEventStore.OpenReadOnlyEventStore()</c>. CritterWatch's Event
///     Explorer is the caller.
/// </summary>
/// <remarks>
///     <para>
///         Every member is already implemented on <see cref="EventOperations" />; this type exists only
///         to own session lifetime. <b>That is the divergence from Polecat</b>, whose
///         <c>OpenReadOnlyEventStore()</c> returns <c>QuerySession().Events</c> directly — capturing a
///         session that nothing ever disposes, because <see cref="IReadOnlyEventStore" /> is not
///         <see cref="IDisposable" /> and so a caller has no way to.
///     </para>
///     <para>
///         Fisher cannot afford the same shape. A <c>FisherSession</c> caches its
///         <c>SqliteConnection</c> for its whole lifetime and releases it only in
///         <c>DisposeAsync</c>, so a captured session is a pooled connection against a single database
///         file held until the process ends — one per call to a method whose whole purpose is to be
///         called by a polling monitoring tool. Opening and disposing a session per read costs a pool
///         checkout, which for an embedded database is a rounding error next to the leak.
///     </para>
///     <para>
///         This is also why the type holds the <see cref="DocumentStore" /> rather than a session: there
///         is no session to hold.
///     </para>
/// </remarks>
internal sealed class FisherReadOnlyEventStore : IReadOnlyEventStore
{
    private readonly DocumentStore _store;

    internal FisherReadOnlyEventStore(DocumentStore store) => _store = store;

    public Task<IReadOnlyList<IEvent>> FetchStreamAsync(Guid streamId, long version = 0,
        DateTimeOffset? timestamp = null, long fromVersion = 0, CancellationToken token = default)
        => ReadAsync(events => events.FetchStreamAsync(streamId, version, timestamp, fromVersion, token));

    public Task<IReadOnlyList<IEvent>> FetchStreamAsync(string streamKey, long version = 0,
        DateTimeOffset? timestamp = null, long fromVersion = 0, CancellationToken token = default)
        => ReadAsync(events => events.FetchStreamAsync(streamKey, version, timestamp, fromVersion, token));

    public Task<StreamState?> FetchStreamStateAsync(Guid streamId, CancellationToken token = default)
        => ReadAsync(events => events.FetchStreamStateAsync(streamId, token));

    public Task<StreamState?> FetchStreamStateAsync(string streamKey, CancellationToken token = default)
        => ReadAsync(events => events.FetchStreamStateAsync(streamKey, token));

    public Task<PagedEvents> QueryEventsAsync(EventQuery query, CancellationToken token = default)
        => ReadAsync(events => events.QueryEventsAsync(query, token));

    /// <summary>
    ///     Run one read against a session of its own, disposed before the result is handed back.
    /// </summary>
    /// <remarks>
    ///     The result is awaited inside rather than returned as a task, so the session outlives the read
    ///     it is servicing. Returning <c>read(...)</c> unawaited would dispose the session — and with it
    ///     the connection the reader is still walking — while the read was in flight.
    /// </remarks>
    private async Task<T> ReadAsync<T>(Func<EventOperations, Task<T>> read)
    {
        await using var session = _store.LightweightSession();

        return await read((EventOperations)session.Events).ConfigureAwait(false);
    }
}
