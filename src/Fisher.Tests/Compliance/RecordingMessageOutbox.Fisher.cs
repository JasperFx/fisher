namespace JasperFx.Events.ComplianceTests;

/*
 * The per-consumer half of ProjectionSideEffectCompliance's recording outbox (jasperfx#763) — the
 * third shared type in the compliance package an alias alone cannot reach, after
 * ComplianceFlatTableProjection and ComplianceSubscription, and for the same reason as the second:
 * IMessageOutbox and IMessageBatch are per-product interfaces whose members genuinely differ.
 * Marten's IMessageBatch derives from its own IChangeListener, so its hooks take
 * (IDocumentSession, IChangeSet, CancellationToken); Fisher's and Polecat's take a bare token.
 *
 * Everything portable — the recording, the lock around it, the batch list, the commit probe — is the
 * shared partial's. This file is the two interface implementations and nothing else.
 *
 * The IMessageSink half is already implemented on the shared RecordingMessageBatch, because
 * IMessageSink is JasperFx's own type and Fisher's IMessageBatch derives from it directly.
 */

public partial class RecordingMessageOutbox : Fisher.Events.Messaging.IMessageOutbox
{
    public ValueTask<Fisher.Events.Messaging.IMessageBatch> CreateBatch(Fisher.IDocumentSession session)
        => new(NewBatch());
}

public partial class RecordingMessageBatch : Fisher.Events.Messaging.IMessageBatch
{
    public Task BeforeCommitAsync(CancellationToken token) => RecordBeforeCommitAsync();

    public Task AfterCommitAsync(CancellationToken token) => RecordAfterCommitAsync();
}
