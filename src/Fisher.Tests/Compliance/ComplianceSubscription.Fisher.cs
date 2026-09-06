using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace JasperFx.Events.ComplianceTests;

/*
 * The per-consumer half of SubscriptionCompliance's subscription — the second shared type in the
 * compliance package that an alias alone cannot reach, the same shape as
 * ComplianceFlatTableProjection and for the same reason. Everything portable — the recording, the
 * lock around it, the wait, the commit probe — lives in the shared partial; each store supplies the
 * one method, because every store declares its own ISubscription.
 *
 * Fisher's is the closest of the three to being writable once: its ProcessEventsAsync returns
 * JasperFx's own IDaemonChangeListener rather than a product-local IChangeListener, since fisher#21
 * took the lifted type instead of copying Polecat's older spelling. The shared library still cannot
 * assume that of every store, so the partial is the price either way.
 *
 * Delivery is at-least-once by contract, and every assertion in the suite is written to survive
 * that: the tests assert on the union of pages, never on page boundaries. `each_event_is_delivered
 * _once` is not in tension with it — a redelivered page after a rolled-back batch is allowed, and
 * what that test pins is that a *committed* range is not handed over twice, which is the property
 * FisherProjectionBatch gets from writing the progression row inside the batch's own transaction.
 *
 * 2.64.0 deepened the suite (jasperfx#768) and two of its additions land here rather than in the
 * shared half:
 *
 * - The notes. `operations` is the batch's own session, enlisted in the batch's transaction, so a
 *   Store here commits with the progression row or not at all. That is the whole subject of
 *   `writes_through_the_supplied_session_are_committed_with_the_batch`, and a store handing out a
 *   session it never commits would pass every delivery fact and lose these silently.
 * - The listener. The return value used to be NullDaemonChangeListener, which is exactly the shape
 *   a store that ignored the return value produces — so the suite now counts commits through it.
 *   Fisher runs it after ExecuteAsync returns and outside the resilience pipeline (fisher#21), which
 *   is what makes the shared CommitProbe's read of committed state meaningful.
 */

public partial class ComplianceSubscription : Fisher.Subscriptions.ISubscription
{
    public Task<IDaemonChangeListener> ProcessEventsAsync(
        EventRange page,
        ISubscriptionController controller,
        Fisher.IDocumentSession operations,
        CancellationToken cancellationToken)
    {
        Record(page.Events);

        foreach (var note in NotesFor(page))
        {
            operations.Store(note);
        }

        return Task.FromResult<IDaemonChangeListener>(new FisherCommitListener(this));
    }

    /// <summary>
    ///     Forwards the daemon's post-commit callback to the shared recorder.
    /// </summary>
    /// <remarks>
    ///     A separate type rather than making the subscription its own listener: the shared partial
    ///     is deliberately not an <c>IDaemonChangeListener</c>, since that interface is JasperFx's
    ///     here and a product-local one elsewhere.
    /// </remarks>
    private sealed class FisherCommitListener : IDaemonChangeListener
    {
        private readonly ComplianceSubscription _subscription;

        internal FisherCommitListener(ComplianceSubscription subscription) => _subscription = subscription;

        public Task AfterCommitAsync(CancellationToken token) => _subscription.RecordCommitAsync();
    }
}
