using JasperFx.Events;

namespace Fisher.Events.Messaging;

/// <summary>
///     A message-publishing surface scoped to one unit of work — one session's
///     <c>SaveChangesAsync</c>, or one projection daemon batch.
/// </summary>
/// <remarks>
///     <para>
///         A projection calls <see cref="IMessageSink.PublishAsync{T}(T, string)" /> (or the metadata
///         overload) to emit a side effect. The batch buffers those and flushes them in whichever of
///         the two hooks its delivery guarantee calls for:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <see cref="BeforeCommitAsync" /> runs <em>inside</em> the write transaction, just
///                 before <c>COMMIT</c>. That is where an outbox persists its rows, so the messages
///                 and the projection write are atomic.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="AfterCommitAsync" /> runs once the write is durably committed — where a
///                 best-effort publish straight to a broker belongs, since it must not fire for a
///                 transaction that rolled back.
///             </description>
///         </item>
///     </list>
///     <para>
///         Fisher ships neither guarantee itself. The default <see cref="IMessageOutbox" /> is
///         <c>NulloMessageOutbox</c>, which drops every message; a bus integration supplies a real one.
///         Same division as Polecat, where Wolverine is the canonical implementer — the concern is not
///         dialect-specific, so the shape is deliberately identical and DCB/projection code ports
///         between the two stores unchanged.
///     </para>
/// </remarks>
public interface IMessageBatch : IMessageSink
{
    /// <summary>
    ///     Called inside the write transaction, immediately before <c>COMMIT</c>.
    /// </summary>
    Task BeforeCommitAsync(CancellationToken token);

    /// <summary>
    ///     Called once the write is durably committed.
    /// </summary>
    Task AfterCommitAsync(CancellationToken token);
}
