# Batched Queries

```cs
var batch = session.Events.CreateBatchQuery();

var user = batch.Load<User>(userId);
var orders = batch.Query<Order>(q => q.Where(x => x.CustomerId == userId));
var exists = batch.CheckExists<Invoice>(invoiceId);

await batch.Execute();

var theUser = await user;
var theOrders = await orders;
```

Each method hands back a task that does **not** complete until `Execute` runs, matching the siblings'
contract.

## What a batch is for here, said plainly

::: warning
**This exists for API parity, not for speed, and that is a deliberate choice rather than an unfinished
one.** In Marten and Polecat a batch earns its keep by collapsing several network round trips into
one. SQLite is embedded, so there are no round trips to collapse and Fisher gains essentially nothing
in throughput.

Do not reach for this expecting it to be faster than the same reads issued directly. It is not, and
it is not trying to be.
:::

It is here so that code written against one Critter Stack store runs unchanged against another — and
so Fisher can honestly enroll in the shared batched-query compliance tests with a real implementation
rather than a test-only shim.

The one property that does hold: **the reads run back to back against one connection with nothing
interleaved**, so a set of [DCB boundaries](/events/dcb) is established against a coherent view rather
than drifting apart as each is fetched. It is implemented without statement coalescing on purpose.

## The surface

```cs
Task<T?> Load<T>(Guid|string|int|long id);
Task<IReadOnlyList<T>> LoadMany<T>(params Guid[]|string[] ids);
Task<bool> CheckExists<T>(Guid|string id);
Task<IReadOnlyList<T>> Query<T>(Func<IQuerySession, IQueryable<T>> query);
Task<T> QueryByPlan<T>(IQueryPlan<T> plan);

Task<bool> EventsExist(EventTagQuery query);
Task<IEventBoundary<T>> FetchForWritingByTags<T>(EventTagQuery query);

Task Execute(CancellationToken token = default);
```

The two event methods are the [DCB](/events/dcb) half, which is where a batch is most useful — several
boundaries opened against one coherent view.

## Failure handling

::: tip
**A failing item neither stops the batch nor vanishes.** Every item runs, each task is completed or
faulted, and `Execute` then throws for what failed.

Both halves are load-bearing. Stopping at the first failure would leave later items' tasks
uncompleted, so a caller awaiting one *hangs* rather than seeing an error. Faulting only the item's
task would let a caller who never awaits that particular item conclude the batch succeeded.
:::

One failure rethrows as itself; several become an `AggregateException`.

## Query plans

A reusable read, shared between the batched and unbatched paths:

Derive from `QueryListPlan<T>` and write the query once:

```cs
public class OpenOrdersFor : QueryListPlan<Order>
{
    public required Guid CustomerId { get; init; }

    public override IQueryable<Order> Query(IQuerySession session) =>
        session.Query<Order>().Where(x => x.CustomerId == CustomerId && x.Open);
}
```

```cs
var orders = await session.QueryByPlanAsync(new OpenOrdersFor { CustomerId = id });
var orders = batch.QueryByPlan(new OpenOrdersFor { CustomerId = id });
```

`QueryListPlan<T>` implements both `IQueryPlan<T>` and `IBatchQueryPlan<T>` from that one method,
which keeps the batched and unbatched paths from drifting into two different queries with one name.
For anything that is not a plain LINQ query, implement either interface directly — each has a single
`Fetch` method.
