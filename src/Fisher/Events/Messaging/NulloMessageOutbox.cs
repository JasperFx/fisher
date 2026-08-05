using JasperFx.Events;

namespace Fisher.Events.Messaging;

/// <summary>
///     The default outbox and batch: drops every message and fires no hooks.
/// </summary>
/// <remarks>
///     <para>
///         An application with no message bus — most of them — pays nothing for this seam existing. It
///         is a singleton because it holds no state, and it is both halves at once because there is
///         nothing for the factory to build.
///     </para>
///     <para>
///         <strong>Dropping is deliberate, and it is not the same as throwing.</strong> Before this
///         existed, a projection calling <c>PublishMessage</c> failed at runtime. Now it succeeds and
///         goes nowhere, which is the behaviour Marten and Polecat have: publishing is meaningful only
///         once a bus is wired in, and until then a projection's side effect is configuration the
///         application has not finished. A store that threw instead would make every projection that
///         merely <em>might</em> publish untestable without a bus.
///     </para>
/// </remarks>
internal sealed class NulloMessageOutbox : IMessageOutbox, IMessageBatch
{
    public static readonly NulloMessageOutbox Instance = new();

    private NulloMessageOutbox()
    {
    }

    public ValueTask<IMessageBatch> CreateBatch(IDocumentSession session) => new(this);

    public ValueTask PublishAsync<T>(T message, string tenantId) => ValueTask.CompletedTask;

    public ValueTask PublishAsync<T>(T message, MessageMetadata metadata) => ValueTask.CompletedTask;

    public Task BeforeCommitAsync(CancellationToken token) => Task.CompletedTask;

    public Task AfterCommitAsync(CancellationToken token) => Task.CompletedTask;
}
