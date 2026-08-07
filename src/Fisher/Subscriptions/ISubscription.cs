using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;

namespace Fisher.Subscriptions;

/// <summary>
///     Arbitrary user code driven by the async daemon over each range of events — the seam for feeding
///     a message bus, an external index or a webhook without pretending to be a projection
///     (fisher#21).
/// </summary>
/// <remarks>
///     <para>
///         <see cref="IDaemonChangeListener" /> is JasperFx's, not a Fisher type: it was lifted out of
///         Polecat as the canonical shape, so Polecat's own local <c>IChangeListener</c> is the older
///         spelling of the same thing and new code should not copy it.
///     </para>
///     <para>
///         A projection folds events into storage Fisher owns and can rebuild. A subscription does
///         something Fisher cannot undo, which is the whole distinction: it gets the same ordered,
///         at-least-once delivery and the same progress bookkeeping, and none of the rebuild
///         guarantees.
///     </para>
///     <para>
///         <b>Writes through <paramref name="operations" /> commit in the same transaction as the
///         progression row</b>, so a subscription that persists through the session it is handed gets
///         exactly-once semantics against Fisher's own database. Anything <em>outside</em> that
///         database — an HTTP call, a bus publish — is at-least-once, because nothing can make it
///         atomic with a SQLite commit. Do the external work in the returned
///         <see cref="IDaemonChangeListener" />'s <c>AfterCommitAsync</c> if it must not happen for a batch
///         that then fails, and make it idempotent either way.
///     </para>
/// </remarks>
public interface ISubscription
{
    /// <summary>
    ///     Handle one page of events.
    /// </summary>
    /// <param name="page">The range, in global sequence order.</param>
    /// <param name="controller">The shard, for reporting a poison event or requesting a stop.</param>
    /// <param name="operations">A session enrolled in the batch's transaction.</param>
    /// <returns>
    ///     A listener for post-commit work, or <see cref="NullDaemonChangeListener.Instance" /> when
    ///     there is none.
    /// </returns>
    Task<IDaemonChangeListener> ProcessEventsAsync(
        EventRange page,
        ISubscriptionController controller,
        IDocumentSession operations,
        CancellationToken cancellationToken);
}

/// <summary>
///     Convenience base for a subscription, giving it the filtering and options surface the daemon
///     reads.
/// </summary>
/// <remarks>
///     Deriving from this rather than implementing <see cref="ISubscription" /> directly is what lets a
///     subscription filter the events it is sent (<c>IncludeType&lt;T&gt;()</c>), name itself, and set
///     its own <c>AsyncOptions</c>. A bare <see cref="ISubscription" /> is wrapped in one of these at
///     registration, so the two are the same thing to the daemon.
/// </remarks>
public abstract class SubscriptionBase
    : JasperFxSubscriptionBase<IDocumentSession, IQuerySession, ISubscription>, ISubscription
{
    protected SubscriptionBase()
    {
        Name = GetType().Name;
    }

    /// <inheritdoc cref="ISubscription.ProcessEventsAsync" />
    public abstract Task<IDaemonChangeListener> ProcessEventsAsync(
        EventRange page,
        ISubscriptionController controller,
        IDocumentSession operations,
        CancellationToken cancellationToken);
}

/// <summary>
///     Presents a bare <see cref="ISubscription" /> to the daemon as a <see cref="SubscriptionBase" />.
/// </summary>
internal sealed class SubscriptionWrapper : SubscriptionBase
{
    private readonly ISubscription _subscription;

    internal SubscriptionWrapper(ISubscription subscription)
    {
        _subscription = subscription;
        Name = subscription.GetType().Name;
    }

    public override Task<IDaemonChangeListener> ProcessEventsAsync(EventRange page,
        ISubscriptionController controller, IDocumentSession operations, CancellationToken cancellationToken)
        => _subscription.ProcessEventsAsync(page, controller, operations, cancellationToken);
}
