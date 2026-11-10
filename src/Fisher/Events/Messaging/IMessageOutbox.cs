namespace Fisher.Events.Messaging;

/// <summary>
///     Factory for <see cref="IMessageBatch" />, registered on
///     <see cref="EventStoreOptions.MessageOutbox" />.
/// </summary>
/// <remarks>
///     A fresh batch is created at most once per unit of work, and only when something in it actually
///     publishes — so a store with no bus integration never calls this at all.
/// <para>
///     Derives from the shared <see cref="JasperFx.Events.Documents.IMessageOutbox" /> (jasperfx#1045), whose
///     member is satisfied here by forwarding to <see cref="CreateBatch(IDocumentSession)" />, so the shared
///     <c>ProjectionBatchBase</c> can be handed a Fisher outbox as it is. Kept for one release; move to the
///     shared interface.
/// </para>
/// </remarks>
public interface IMessageOutbox : JasperFx.Events.Documents.IMessageOutbox
{
    async ValueTask<JasperFx.Events.Documents.IMessageBatch> JasperFx.Events.Documents.IMessageOutbox.CreateBatch(
        JasperFx.Events.Documents.IDocumentSessionOperations session)
        => await CreateBatch((IDocumentSession)session).ConfigureAwait(false);

    /// <summary>
    ///     Build a batch for the session persisting the write.
    /// </summary>
    /// <remarks>
    ///     The session is handed over so an outbox can enlist its own rows in the same unit of work,
    ///     which is what makes the <see cref="IMessageBatch.BeforeCommitAsync" /> guarantee reachable.
    /// </remarks>
    ValueTask<IMessageBatch> CreateBatch(IDocumentSession session);
}
