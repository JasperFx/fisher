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
/// <remarks>
///     The <see cref="JasperFx.Events.IStorageOperations" /> half is what lets Fisher's session types
///     close JasperFx's aggregation and projection generics, which constrain the write session to be
///     both the read session and a storage-operations surface. Its members are the projection write
///     path — see <c>Fisher.Internal.FisherSession</c> for which of them are live today.
/// </remarks>
public interface IDocumentSession : IQuerySession, JasperFx.Events.IStorageOperations
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
