# Event Subscriptions

A subscription is a daemon shard that hands each range of events to **arbitrary code** rather than to
a projection.

```cs
public class NotifyOnShipment : SubscriptionBase
{
    public override Task<IDaemonChangeListener> ProcessEventsAsync(
        EventRange page, ISubscriptionController controller,
        IDocumentSession operations, CancellationToken token)
    {
        foreach (var e in page.Events.OfType<IEvent<OrderShipped>>())
        {
            // Writes here commit in the batch's own transaction
            operations.Store(new ShipmentNotice { OrderId = e.StreamId });
        }

        return Task.FromResult<IDaemonChangeListener>(NullDaemonChangeListener.Instance);
    }
}
```

Deriving from `SubscriptionBase` rather than implementing `ISubscription` directly is what gives it
the filtering and options surface the daemon reads.

::: tip
**A projection folds events into storage Fisher owns and can rebuild. A subscription does something
Fisher cannot undo** — that is the whole distinction. It gets the same ordered, at-least-once delivery
and the same progress bookkeeping, and none of the rebuild guarantees.
:::

```cs
opts.Projections.Subscribe(new NotifyOnShipment());
opts.Projections.Subscribe<NotifyOnShipment>(options => options.Name = "shipments");
```

A subscription needs the daemon running:

```cs
builder.Services.AddFisher(opts => { … })
    .ApplyAllDatabaseChangesOnStartup()
    .AddAsyncDaemon(DaemonMode.Solo);
```

## Its writes commit with its progress

**The subscription's session is the batch's**, so its own writes commit in the same transaction as the
progression row. It cannot advance past a range whose writes rolled back, nor commit writes for a
range it will replay.

::: warning
**That guarantee stops at Fisher's database.** An HTTP call or a bus publish is at-least-once, and
nothing can make it atomic with a SQLite commit. If your subscription reaches outside, make the
outside idempotent.
:::

## The post-commit listener

Return an `IDaemonChangeListener` to be called after the batch commits — `NullDaemonChangeListener.Instance`
when there is none:

```cs
return Task.FromResult<IDaemonChangeListener>(new MyListener());
```

Do the external work there if it must not happen for a batch that then fails.

::: tip
It runs after the batch's work returns and **outside the resilience pipeline**. A retried
`SQLITE_BUSY` re-executes the whole batch delegate, so a listener invoked inside it would fire twice
for a transaction that already committed.
:::

::: warning
**`WaitForNonStale` does not imply the post-commit listener has run.** The progression row is written
*inside* the batch's transaction, so non-stale is true the moment that commits — strictly before the
listener.

A test that waits on non-staleness and then asserts the listener fired fails roughly one full-suite
run in several. Wait on the listener's own signal.
:::

::: tip
`IDaemonChangeListener` is **JasperFx's**, not a Fisher type. It was lifted out of Polecat as the
canonical shape, so Polecat's local `IChangeListener` is the older spelling of the same thing. New
code should not copy it.
:::

## Naming a subscription

Daemon progression is keyed on the subscription's name. The default is derived from the type name;
name it explicitly if the type may be renamed:

```cs
opts.Projections.Subscribe(new NotifyOnShipment(), options => options.Name = "shipments");
```

## There is no inline equivalent

::: tip
Deliberately. "Inline" would just be code in the caller's own unit of work — you already have that.
:::

## Filtering

A subscription sees every event in the range. Filter in your own code, or use
`SubscriptionBase`'s options to narrow by event type:

```cs
opts.Projections.Subscribe(new NotifyOnShipment(), options =>
{
    options.IncludeType<OrderShipped>();
});
```

## Errors and dead letters

A subscription's failures follow the same shard error policy as a projection's, including
`SkipApplyErrors` quarantining a poison event into
[`fi_dead_letters`](/events/storage#dead-letters) rather than stopping the shard.

## Subscriptions vs projections

| | Projection | Subscription |
| :--- | :--- | :--- |
| Writes | A read model Fisher manages | Whatever you write |
| Rebuild | Tears down and replays | Replays; teardown is yours |
| Reaching outside the database | No | Yes, at-least-once |

If what you want is a read model, use a [projection](/events/projections/). If what you want is a side
effect, a subscription is the shape — and see also
[projection side effects](/events/projections/side-effects) for the message-publishing seam.
