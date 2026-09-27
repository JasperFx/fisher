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
///     <para>
///         <b>A tenant is held rather than passed per read</b> (jasperfx#885). Opening the tier for a
///         tenant is what makes its tenant-<em>less</em> members — <see cref="FetchStreamAsync(Guid, long, DateTimeOffset?, long, CancellationToken)" />
///         and <see cref="FetchStreamStateAsync(Guid, CancellationToken)" /> and their string twins —
///         usable under conjoined tenancy at all: a shared stream id has a different version in each
///         tenant, and those signatures carry nowhere to say which one is meant. That the tenant lands
///         on the session rather than on each statement is not a shortcut: a session already scopes
///         every read it serves to its own tenant, so there is no per-member tenant term for a later
///         member to forget. <c>QueryStreamStates</c> and <c>EventQuery.TenantId</c> keep taking their
///         own, being the two members that already had somewhere to put one.
///     </para>
/// </remarks>
internal sealed class FisherReadOnlyEventStore : IReadOnlyEventStore
{
    private readonly DocumentStore _store;
    private readonly string? _tenantId;

    internal FisherReadOnlyEventStore(DocumentStore store, string? tenantId = null)
    {
        _store = store;
        _tenantId = tenantId;
    }

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
    ///     The streams table as a composable <see cref="IQueryable{T}" /> of
    ///     <see cref="StreamState" /> (jasperfx#740). See <see cref="StreamStateQueryProvider" /> for
    ///     the translation; execution opens a session per terminator, the same lifetime rule as
    ///     <see cref="ReadAsync{T}" /> — a queryable that captured one would pin a pooled connection
    ///     for as long as the caller keeps composing on it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>An omitted <paramref name="tenantId" /> falls back to the tenant this tier was opened
    ///         for</b> (jasperfx#885), which is what keeps the two halves of a tenant-scoped reader
    ///         agreeing. Without it, <c>OpenReadOnlyEventStore("north").QueryStreamStates()</c> would
    ///         read the *default* tenant — a silent wrong answer under conjoined tenancy, and the wrong
    ///         database file entirely under database-per-tenant. An explicit argument still wins, since
    ///         this member is one of the two on the tier that has somewhere to name a tenant.
    ///     </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    ///     A non-null tenant on a store with no tenant dimension at all — neither conjoined events nor a
    ///     database per tenant. Refused rather than ignored, per the contract: the unscoped streams table
    ///     would read as one tenant's, which is the jasperfx#737 silently-unfiltered failure mode.
    /// </exception>
    public IQueryable<StreamState> QueryStreamStates(string? tenantId = null)
    {
        var scope = tenantId ?? _tenantId;

        if (scope is not null && !_store.IsTenanted())
        {
            throw new NotSupportedException(
                $"This event store is not multi-tenanted, so QueryStreamStates cannot scope to tenantId "
                + $"'{scope}': fi_streams has no tenant dimension, and the unscoped streams would read "
                + "as that tenant's. Set StoreOptions.Events.TenancyStyle = TenancyStyle.Conjoined before "
                + "the schema is created, configure a database per tenant, or omit the tenant id.");
        }

        return new StreamStateQueryProvider(_store, scope).CreateRoot();
    }

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
        await using var session = _store.LightweightSession(_tenantId);

        return await read((EventOperations)session.Events).ConfigureAwait(false);
    }
}
