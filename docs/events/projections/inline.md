# Inline Projections

An inline projection is applied in the **same transaction** as the append that triggered it.

```cs
opts.Projections.Snapshot<Order>(SnapshotLifecycle.Inline);
opts.Projections.Add(new OrderSummaryProjection(), ProjectionLifecycle.Inline);
```

```cs
session.Events.Append(orderId, new OrderShipped(DateTimeOffset.UtcNow));
await session.SaveChangesAsync();

var order = await session.LoadAsync<Order>(orderId);   // already current
```

Events and the read model are consistent, always, with no daemon to run.

## When they are applied

In `SaveChangesAsync`, **before the batch is taken** — because applying a projection queues *further*
operations (the snapshot writes), which have to commit alongside the events that caused them.

## Versions have to be assigned early

An inline projection needs its events to already know their versions, but Fisher normally assigns
those inside the write transaction, where the stream's current version has just been read under the
write lock. So the version is read **early, outside the lock**, to give projections something to read.

::: tip
**That is not a weakened guard.** The same versions are re-derived inside the transaction and the
optimistic concurrency check still runs there, so a racing writer still fails the commit. The early
pass exists only for the projection.
:::

## Document tables are created at commit

A document type can be stored without ever being registered, and a snapshot type is registered by
projection configuration — either way, the first write may be the first time the table is needed. Only
types the schema has already mapped are considered, which is how a document operation is told apart
from an event one.

::: warning
This does not happen for an
[enlisted session](/documents/sessions#enlisting-in-your-own-connection-or-transaction), where running
a migration on a second connection would deadlock against your own write lock. Apply the schema at
startup if you use inline projections with an enlisted session.
:::

## The cost, on SQLite specifically

Everything an inline projection does happens inside the write transaction, which holds the file's one
write lock. So:

- **A single-stream projection is cheap** — one document write per append.
- **A [multi-stream](/events/projections/multi-stream-projections) projection is not necessarily.**
  Unrelated commands end up writing the same rows, contending with each other on top of already
  contending for the write lock. Async is usually the better lifecycle for those.
- **A [flat table](/events/projections/flat) projection is cheap** — one upsert.

## Side effects

By default an inline projection cannot publish messages. Opt in:

```cs
opts.Events.EnableSideEffectsOnInlineProjections = true;
```

See [Side Effects](/events/projections/side-effects) — and note the default outbox drops every
message.

## Inline or async?

| Choose inline when | Choose async when |
| :--- | :--- |
| A read immediately after the write must see it | Eventual consistency is fine |
| The projection is cheap and single-stream | The projection is multi-stream or expensive |
| You do not want to host a daemon | You want projection work off the request path |
| | You want to be able to rebuild without downtime |

::: tip
Switching between them is a one-line change and a rebuild. Nothing at the call sites changes, because
`FetchForWriting` and `LoadAsync` behave the same way either way.
:::

## Live is the third option

If the read model is only *sometimes* needed, [live aggregation](/events/projections/live-aggregates)
stores nothing at all.
