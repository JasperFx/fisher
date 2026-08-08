using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace JasperFx.Events.ComplianceTests;

/*
 * The per-consumer half of SubscriptionCompliance's subscription — the second shared type in the
 * compliance package that an alias alone cannot reach, the same shape as
 * ComplianceFlatTableProjection and for the same reason. Everything portable — the recording, the
 * lock around it, the wait — lives in the shared partial; each store supplies the one method,
 * because every store declares its own ISubscription.
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

        return Task.FromResult<IDaemonChangeListener>(NullDaemonChangeListener.Instance);
    }
}
