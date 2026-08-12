# Multi Stream Projections

One document built from events across **many** streams.

```cs
public partial class SalesByCustomer : MultiStreamProjection<CustomerSales, string>
{
    public SalesByCustomer()
    {
        Identity<OrderPlaced>(e => e.Customer);
        Identity<OrderRefunded>(e => e.Customer);
    }

    public void Apply(OrderPlaced e, CustomerSales view) => view.Total += e.Total;
    public void Apply(OrderRefunded e, CustomerSales view) => view.Total -= e.Amount;
}
```

```cs
opts.Projections.Add(new SalesByCustomer(), ProjectionLifecycle.Async);
```

::: warning
The class must be **`partial`** — the dispatcher is source-generated into it.
:::

## Grouping

Three ways to say which document an event belongs to:

```cs
// One document
Identity<OrderPlaced>(e => e.Customer);

// Several documents
Identities<OrderShipped>(e => e.CustomerIds);

// Fan out a collection member into one event per element
FanOut<OrderPlaced, OrderLine>(e => e.Lines);
```

And for anything those cannot express, a custom grouper via `CustomGrouping(...)`.

## Lifecycles

Both [inline](/events/projections/inline) and [async](/events/projections/async-daemon) work.

::: tip
**Async is usually the right choice for a multi-stream projection**, and on SQLite there is an extra
reason. An inline multi-stream projection turns every append into a write to a document that other
streams' appends also write to — so unrelated commands contend for the same rows, on top of already
contending for the file's single write lock. The daemon does that work on one thread, out of the
request path.
:::

## Slices and the operation queue

The daemon fans a batch's slices out concurrently, and they all queue onto **the same** session.

::: warning
That is why the session's operation queue is guarded. `List<T>.Add` is not thread-safe and fails
silently here: two concurrent adds can leave the count incremented once, so one slice's document write
never reaches the batch — which then commits the progression row for a range whose documents were only
partly written.

It presented as a multi-stream rebuild intermittently missing one slice's document. If you are
extending Fisher, the queue's guard is not optional.
:::

## Rebuilds

```cs
await daemon.RebuildProjectionAsync("SalesByCustomer", token);
```

A multi-stream projection almost always needs a rebuild when its grouping changes, because the
grouping decides which document an event contributed to.

::: warning
Test the rebuild with a row the replay **cannot** recreate. A replay rewrites every document it can
still produce, so a broken teardown is invisible against live data.
:::

## Reading the result

An ordinary document:

```cs
var sales = await session.LoadAsync<CustomerSales>("acme");

var top = await session.Query<CustomerSales>()
    .OrderByDescending(x => x.Total)
    .Take(10)
    .ToListAsync();
```

To wait for the daemon to catch up:

```cs
var top = await session.Query<CustomerSales>()
    .QueryForNonStaleData(TimeSpan.FromSeconds(5))
    .OrderByDescending(x => x.Total)
    .ToListAsync();
```

## Aggregate caching

Within a batch, slices for the same document reuse an in-flight entity rather than re-reading it. That
is also what lets a [composite projection's](/events/projections/composite) later stage see an earlier
stage's work — the cache, not the database.
