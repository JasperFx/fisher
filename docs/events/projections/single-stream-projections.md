# Single Stream Projections

One document per stream. The commonest shape by a wide margin.

## As a snapshot

If the document is the aggregate itself, `Snapshot<T>` is the whole registration:

```cs
opts.Projections.Snapshot<Order>(SnapshotLifecycle.Inline);
```

```cs
public class Order
{
    public Guid Id { get; set; }
    public bool Shipped { get; set; }

    public static Order Create(OrderPlaced e) => new() { … };
    public void Apply(OrderShipped e) => Shipped = true;
}
```

See [Snapshots](/events/snapshots).

## As a separate read model

When the document is *not* the aggregate — a summary, a view shaped for one screen — write the
projection:

```cs
public partial class OrderSummaryProjection : SingleStreamProjection<OrderSummary, Guid>
{
    public static OrderSummary Create(OrderPlaced e) =>
        new() { Customer = e.Customer, Total = e.Total };

    public void Apply(OrderLineAdded e, OrderSummary view) => view.LineCount++;

    public void Apply(OrderShipped e, OrderSummary view) => view.Shipped = true;

    public bool ShouldDelete(OrderCancelled e) => true;
}
```

```cs
opts.Projections.Add(new OrderSummaryProjection(), ProjectionLifecycle.Async);
```

::: warning
The class must be **`partial`** — the dispatcher is source-generated into it, and there is no runtime
fallback.
:::

## The type parameters

`SingleStreamProjection<TDoc, TId>` — and `TId` is the **document's own identity type**, not the
stream identity primitive. They coincide for a plain `Guid Id`; a
[strong-typed id](/documents/identity#strong-typed-identities) is a wrapper, and the generated
dispatcher is keyed on the wrapper.

## Conventional methods

| Method | Purpose |
| :--- | :--- |
| `static TDoc Create(TEvent e)` | The first event creates the document |
| `void Apply(TEvent e, TDoc view)` | Mutate |
| `TDoc Apply(TEvent e, TDoc view)` | Return a new instance — for records |
| `bool ShouldDelete(TEvent e)` | Delete the document |

Each can also take an `IEvent<T>` to reach metadata:

```cs
public void Apply(IEvent<OrderShipped> e, OrderSummary view)
{
    view.ShippedAt = e.Timestamp;
    view.ShippedBy = e.UserName;
}
```

## Lifecycles

```cs
opts.Projections.Add(new OrderSummaryProjection(), ProjectionLifecycle.Inline);
opts.Projections.Add(new OrderSummaryProjection(), ProjectionLifecycle.Async);
opts.Projections.Add(new OrderSummaryProjection(), ProjectionLifecycle.Live);
```

[Inline](/events/projections/inline) writes in the append's own transaction;
[Async](/events/projections/async-daemon) is the background daemon.

## The document table

A projected document gets an ordinary `fi_doc_*` table, so everything on the document side applies —
[indexes](/documents/indexing/), [soft delete](/documents/deletes),
[LINQ](/documents/querying/linq/), the [JSON reads](/documents/querying/query-json):

```cs
opts.Schema.For<OrderSummary>().Index(x => x.Customer);
```

::: tip
The table is created by the migration when the projection is registered, and on demand at first write
otherwise. Register the type if you will `Query<T>()` it before anything has been written — SQLite
resolves a table name when it *prepares* a statement, so a query against a never-written type fails
with `no such table` rather than returning empty.
:::

## Rebuilds

```cs
var daemon = await store.BuildProjectionDaemonAsync();
await daemon.RebuildProjectionAsync("OrderSummaryProjection", token);
```

Teardown truncates the document table and clears the progression rows, in **one transaction**.

::: warning
Do not test a rebuild only by checking that a live aggregate is correct — a replay rewrites every row
it can still produce, so a broken teardown is invisible there. Plant a row the replay **cannot**
recreate, such as one for a stream whose events are gone.
:::

## Naming

Shard progression is keyed on the projection's name, defaulted from the type name.

::: warning
Renaming a projection, or changing its `Version`, **orphans every progression row already written** —
the shard starts from zero. That is sometimes what you want; it should never be an accident.
:::
