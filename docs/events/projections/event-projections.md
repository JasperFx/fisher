# Event Projections

An event projection does arbitrary work per event, rather than folding a stream into one document.
Reach for it when one event should write several documents, or documents of different types.

```cs
public partial class ShipmentProjection : EventProjection
{
    public ShipmentNotice Create(OrderShipped e) =>
        new() { Id = Guid.NewGuid(), ShippedAt = e.ShippedAt };

    public void Project(OrderPlaced e, IDocumentOperations ops)
    {
        ops.Store(new CustomerActivity { … });
        ops.Store(new SalesLedgerEntry { … });
    }
}
```

```cs
opts.Projections.Add(new ShipmentProjection(), ProjectionLifecycle.Async);
```

::: warning
The class must be **`partial`** — the dispatcher is source-generated into it, and there is no runtime
fallback.
:::

## Create and Project

| Method | Behaviour |
| :--- | :--- |
| `TDoc Create(TEvent e)` | The returned document is stored |
| `void Project(TEvent e, IDocumentOperations ops)` | You decide what to write |

A `Create` result is stored onto the session the events are committing in, so it lands in the **same
transaction** as the event that produced it.

## The document type still needs a mapping

::: warning
Fisher only creates tables for types the schema has **mapped**, so a document an event projection
stores needs to be reachable — either registered, or written once through an ordinary session so the
on-demand creation runs.

This is the ordinary on-demand rule rather than a projection quirk, but it is where people meet it.
:::

```cs
opts.Schema.For<ShipmentNotice>();
```

## Deleting

```cs
public void Project(OrderCancelled e, IDocumentOperations ops)
{
    ops.Delete<ShipmentNotice>(e.NoticeId);
    ops.DeleteWhere<CustomerActivity>(x => x.OrderId == e.OrderId);
}
```

## Rebuilds

Teardown clears the document types the projection is known to publish.

::: danger
**An event projection is where teardown is most likely to be incomplete**, because the set of types it
writes is whatever your `Project` methods happen to touch. If it writes a type the projection does not
declare, a rebuild replays on top of the previous run's rows.

Declare them:

```cs
opts.Projections.Add(projection, options => options.DeleteViewTypeOnTeardown<CustomerActivity>());
```
:::

And test the rebuild with a row the replay cannot recreate — a replay rewrites everything it can still
produce, so a broken teardown is invisible against live data.

## Lifecycles

All three work. [Inline](/events/projections/inline) is genuinely useful here when the extra documents
must be visible the moment the event commits.

## EF Core entities

If the documents are EF entities rather than Fisher documents, derive from
`EfCoreEventProjection<TContext>` — the one place the EF integration needs a base class, because a
per-event projection has no storage indirection to swap. See
[EF Core Projections](/events/projections/efcore).

## When not to use one

If the output is one document per stream, a
[single stream projection](/events/projections/single-stream-projections) is simpler and rebuilds more
cleanly. If it is one document from many streams, use a
[multi stream projection](/events/projections/multi-stream-projections) — its grouping is declarative,
where an event projection's is code you have to keep consistent.
