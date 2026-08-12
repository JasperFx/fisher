# Projection Side Effects

A projection can publish messages as it runs.

```cs
public partial class OrderSummaryProjection : SingleStreamProjection<OrderSummary, Guid>
{
    public void Apply(OrderShipped e, OrderSummary view, IProjectionBatch batch)
    {
        view.Shipped = true;
        batch.PublishMessage(new NotifyCustomer(view.Customer));
    }
}
```

::: danger
**A published side effect goes nowhere until you supply an `IMessageOutbox`.** The default outbox
drops every message.

That is a stated contract, not a gap — see below.
:::

## Fisher will not ship a delivery mechanism

::: tip
The no-op outbox is the **intended end state**. It drops messages rather than throwing, which is the
sibling behaviour: publishing means something only once a bus is wired in, and a store that threw would
make every projection that *might* publish untestable without one.

Marten and Polecat both delegate to [Wolverine](https://wolverinefx.net), and this seam is what a bus
integration plugs into. A second answer here would be a Fisher-only subsystem — retry policy, poison
handling, drainer coordination — that projection code could not port to the siblings.
:::

Type names match Polecat's exactly, because messaging is not dialect-specific and projection code
should port between the stores unchanged.

## Supplying an outbox

```cs
opts.Events.MessageOutbox = new MyOutbox();
```

```cs
public interface IMessageOutbox
{
    ValueTask<IMessageBatch> CreateBatch(IStorageSession session);
}

public interface IMessageBatch : IMessageSink
{
    Task BeforeCommitAsync();
    Task AfterCommitAsync();
}
```

## When the hooks fire

Both of Fisher's commit paths bracket their transaction the same way — a session's `SaveChangesAsync`
and the async daemon's projection batch:

| Hook | Position | Visible to another connection? |
| :--- | :--- | :--- |
| `BeforeCommitAsync` | The last thing inside the transaction | **no** |
| `AfterCommitAsync` | After the commit | **yes** |

Hook *order* is not the invariant — both would fire in order even if both ran before the commit. What
is pinned is what the rest of the database can see when each runs.

**Which hook you flush in is your delivery guarantee.** Flushing in `BeforeCommitAsync` puts the
messages in the same transaction as the projection's writes (an outbox table in the same file);
flushing in `AfterCommitAsync` sends only after the data is durable, at-least-once.

::: warning
`AfterCommitAsync` runs **outside** the resilience pipeline. A retried `SQLITE_BUSY` re-executes the
whole delegate, so a post-commit publish inside it would fire twice for a transaction that had already
committed.
:::

::: warning
The same property bit the daemon batch's own input, and that one was **silent**: everything the retried
delegate reads has to survive being read twice, so the session's operations are taken *before* the
pipeline and executed from that snapshot inside it. Draining them inside left a retry with nothing to
write while the progression row still committed — advancing a projection past events whose documents
were never written, with no error anywhere.
:::

## The batch is lazy and never reused

A session that publishes nothing never asks the outbox for a batch, so both hooks stay no-ops for the
common case. The batch is cleared after commit, which is what stops a second `SaveChangesAsync`
re-flushing the first one's messages.

## Inline projections

Off by default:

```cs
opts.Events.EnableSideEffectsOnInlineProjections = true;
```

## Generic dispatch

`IProjectionBatch.PublishMessageAsync` hands over an `object` while `IMessageSink.PublishAsync<T>` is
generic, so Fisher closes it over the runtime message type and caches the compiled delegate per type.
Polecat does the same.

## The alternative: a subscription

If what you want is arbitrary code over the event feed rather than messages out of a projection, a
[subscription](/events/subscriptions) is the shape — and its writes commit in the batch's own
transaction.

::: warning
That guarantee stops at Fisher's database either way. An HTTP call or a bus publish is at-least-once,
and nothing can make it atomic with a SQLite commit. Make the outside idempotent.
:::
