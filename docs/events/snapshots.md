# Snapshots

A snapshot is an aggregate stored as a document and kept current from its stream.

```cs
opts.Projections.Snapshot<Order>(SnapshotLifecycle.Inline);
opts.Projections.Snapshot<Order>(SnapshotLifecycle.Async);
```

`SnapshotLifecycle` carries those two values and no more. Storing nothing is the third option, and it
needs no registration at all — see [Live Aggregations](/events/projections/live-aggregates).

| Lifecycle | When it is updated | Read with |
| :--- | :--- | :--- |
| `Inline` | The same transaction as the append | `LoadAsync<Order>(id)` |
| `Async` | A background daemon, shortly after | `LoadAsync<Order>(id)` |
| *no registration* | Never stored — folded on demand | [Live aggregation](/events/projections/live-aggregates) |

## What `Snapshot<T>` does

It closes a single-stream projection over the aggregate's **own identity type** — the same rule
[live aggregation](/events/projections/live-aggregates) follows, for the same source-generator reason
— and registers the document mapping, so the snapshot's table is created with the schema.

```cs
public class Order
{
    public Guid Id { get; set; }
    public bool Shipped { get; set; }

    public void Apply(OrderShipped e) => Shipped = true;
}
```

::: warning
The `Apply` dispatcher is source-generated with no runtime fallback, keyed on `(TDoc, TId)`. The
aggregate needs an identity member, and the generator runs in the assembly that **defines** it — so
that assembly is the one that has to reference Fisher. The package carries
`JasperFx.Events.SourceGenerator` inside it; there is no analyzer reference to add yourself.
:::

## Inline snapshots

Applied in `SaveChangesAsync` **before the batch is taken**, because applying a projection queues
further operations — the snapshot writes — that have to commit alongside the events that caused them.

```cs
session.Events.Append(orderId, new OrderShipped(DateTimeOffset.UtcNow));
await session.SaveChangesAsync();

var order = await session.LoadAsync<Order>(orderId);   // already current
```

See [Inline Projections](/events/projections/inline).

## Async snapshots

```cs
builder.Services.AddFisher(opts =>
{
    opts.Connection("Data Source=app.db");
    opts.Projections.Snapshot<Order>(SnapshotLifecycle.Async);
})
.ApplyAllDatabaseChangesOnStartup()
.AddAsyncDaemon(DaemonMode.Solo);
```

Eventually consistent. To wait:

```cs
var orders = await session.Query<Order>()
    .QueryForNonStaleData(TimeSpan.FromSeconds(5))
    .ToListAsync();
```

See [the async daemon](/events/projections/async-daemon).

## Live

Not a `SnapshotLifecycle` value: there is nothing to register. Without a `Snapshot<T>` call nothing is
stored, no table is created, and every read folds the stream:

```cs
var order = await session.Events.AggregateStreamAsync<Order>(orderId);
```

See [Live Aggregations](/events/projections/live-aggregates).

## FetchForWriting reads whichever exists

```cs
var stream = await session.Events.FetchForWriting<Order>(orderId);
```

It uses a stored snapshot if there is one, and live aggregation otherwise — so switching a lifecycle
does not change your command handlers.

## Compacting a long stream

If replay cost is the problem rather than read frequency,
[stream compacting](/events/rewriting#stream-compacting) replaces a stream's events with a single
snapshot event, and every reader inherits the fast start with no code change — JasperFx's aggregator
fast-forwards from it before folding.

::: warning
Compacting is **one-way**. A projection rebuilt afterwards rebuilds from the snapshot rather than from
the history that produced it.
:::

## Snapshots and masking

::: warning
[Event data masking](/events/rewriting#event-data-masking) does **not** reach a snapshot already
written. The daemon's high-water mark is a sequence and masking does not move it, so a projection that
already folded the unmasked body keeps what it derived — a snapshot holding protected information
still holds it until that projection is rebuilt.

Marten behaves the same way, and it is why masking is a data-at-rest operation rather than a
correction.
:::
