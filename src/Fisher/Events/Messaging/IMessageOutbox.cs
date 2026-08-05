namespace Fisher.Events.Messaging;

/// <summary>
///     Factory for <see cref="IMessageBatch" />, registered on
///     <see cref="EventStoreOptions.MessageOutbox" />.
/// </summary>
/// <remarks>
///     A fresh batch is created at most once per unit of work, and only when something in it actually
///     publishes — so a store with no bus integration never calls this at all.
/// </remarks>
public interface IMessageOutbox
{
    /// <summary>
    ///     Build a batch for the session persisting the write.
    /// </summary>
    /// <remarks>
    ///     The session is handed over so an outbox can enlist its own rows in the same unit of work,
    ///     which is what makes the <see cref="IMessageBatch.BeforeCommitAsync" /> guarantee reachable.
    /// </remarks>
    ValueTask<IMessageBatch> CreateBatch(IDocumentSession session);
}
