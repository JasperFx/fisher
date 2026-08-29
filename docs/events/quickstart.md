# Event Store Quick Start

## 1. Define events and an aggregate

```cs
public record OrderPlaced(string Customer, decimal Total);
public record OrderLineAdded(string Sku, int Quantity);
public record OrderShipped(DateTimeOffset ShippedAt);

public class Order
{
    public Guid Id { get; set; }
    public string Customer { get; set; } = "";
    public decimal Total { get; set; }
    public List<string> Skus { get; set; } = [];
    public bool Shipped { get; set; }

    public void Apply(OrderPlaced e)
    {
        Customer = e.Customer;
        Total = e.Total;
    }

    public void Apply(OrderLineAdded e) => Skus.Add(e.Sku);

    public void Apply(OrderShipped e) => Shipped = true;
}
```

::: warning
The `Apply` dispatcher is **source-generated**, and there is no runtime fallback. Two consequences:

- The generator runs in the assembly that **defines the aggregate**, so that assembly is the one that
  has to reference Fisher. The Fisher package carries `JasperFx.Events.SourceGenerator` inside it, so
  there is no analyzer reference to add yourself.
- A conventional-method **projection class** must be declared `partial`.

An aggregate also needs an identity member, because the generator keys the dispatcher on
`(TDoc, TId)`. Fisher requires one and says so, rather than failing later with a message about a
missing generated dispatcher.
:::

## 2. Register the store

```cs
builder.Services.AddFisher(opts =>
{
    opts.Connection("Data Source=app.db");
})
.ApplyAllDatabaseChangesOnStartup();
```

## 3. Start a stream

```cs
app.MapPost("/orders", async (PlaceOrder command, IDocumentSession session) =>
{
    // StartStream hands back a StreamAction; its Id is the stream's identity.
    var stream = session.Events.StartStream<Order>(
        new OrderPlaced(command.Customer, command.Total));

    await session.SaveChangesAsync();
    return Results.Ok(stream.Id);
});
```

Or name the id yourself:

```cs
session.Events.StartStream<Order>(orderId, new OrderPlaced(…));
```

## 4. Append to it

```cs
app.MapPost("/orders/{id:guid}/lines", async (Guid id, AddLine command, IDocumentSession session) =>
{
    session.Events.Append(id, new OrderLineAdded(command.Sku, command.Quantity));
    await session.SaveChangesAsync();
});
```

## 5. Read it back

Three ways, and which you want depends on how often you read it:

```cs
// Live aggregation — replay every time
var order = await session.Events.AggregateStreamAsync<Order>(id);

// Fetch, decide, append — the command-handling shape
var stream = await session.Events.FetchForWriting<Order>(id);
if (!stream.Aggregate!.Shipped)
{
    stream.AppendOne(new OrderShipped(DateTimeOffset.UtcNow));
}
await session.SaveChangesAsync();

// A stored snapshot, kept current by a projection
var order = await session.LoadAsync<Order>(id);
```

## 6. Keep a snapshot

```cs
opts.Projections.Snapshot<Order>(SnapshotLifecycle.Inline);   // same transaction
opts.Projections.Snapshot<Order>(SnapshotLifecycle.Async);    // background daemon
```

Those are the only two values. For no storage at all, register nothing and fold the stream on demand
as in step 5 — see [Live Aggregations](/events/projections/live-aggregates).

With `Async`, host the daemon:

```cs
builder.Services.AddFisher(opts => { … })
    .ApplyAllDatabaseChangesOnStartup()
    .AddAsyncDaemon(DaemonMode.Solo);
```

See [Snapshots](/events/snapshots) and [the async daemon](/events/projections/async-daemon).

## 7. Serve it over HTTP

```cs
dotnet add package Fisher.AspNetCore
```

```cs
// Folds the stream, with an ETag read *before* folding — so a matching
// If-None-Match answers 304 having read one row.
app.MapGet("/orders/{id:guid}", (Guid id, IQuerySession session) =>
    session.StreamAggregate<Order>(id));

// The raw stream
app.MapGet("/orders/{id:guid}/events", (Guid id, IQuerySession session) =>
    session.StreamEvents(id));
```

## What next

- [Appending Events](/events/appending) — concurrency, `WriteToAggregate`, the exclusive methods
- [Projections](/events/projections/) — every shape, every lifecycle
- [DCB](/events/dcb) — consistency boundaries that are not one stream
- [Subscriptions](/events/subscriptions) — arbitrary code over the event feed
