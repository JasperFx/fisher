using Fisher.Events;

namespace Fisher;

/// <summary>
///     A read-only Fisher session.
/// </summary>
public interface IQuerySession : IAsyncDisposable
{
    /// <summary>
    ///     The tenant every operation in this session is scoped to.
    /// </summary>
    string TenantId { get; }
}

/// <summary>
///     A writable Fisher session: a unit of work over the event store, flushed by
///     <see cref="SaveChangesAsync" />.
/// </summary>
public interface IDocumentSession : IQuerySession
{
    /// <summary>
    ///     The event store write surface for this session.
    /// </summary>
    EventOperations Events { get; }

    /// <summary>
    ///     Commit every queued operation in a single transaction.
    /// </summary>
    Task SaveChangesAsync(CancellationToken token = default);
}
