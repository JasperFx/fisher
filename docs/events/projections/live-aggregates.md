# Live Aggregations

Fold a stream on demand. Nothing is stored, so the answer is always current and costs a replay.

```cs
var order = await session.Events.AggregateStreamAsync<Order>(streamId);
var atVersion = await session.Events.AggregateStreamAsync<Order>(streamId, version: 10);
var lastKnown = await session.Events.AggregateStreamToLastKnownAsync<Order>(streamId);
```

## The aggregate

```cs
public class Order
{
    public Guid Id { get; set; }
    public string Customer { get; set; } = "";
    public bool Shipped { get; set; }

    public static Order Create(OrderPlaced e) => new() { Customer = e.Customer };
    public void Apply(OrderShipped e) => Shipped = true;
    public bool ShouldDelete(OrderCancelled e) => true;
}
```

No registration is needed — a self-aggregating type is discovered.

::: warning
Conventional `Apply` / `Create` / `ShouldDelete` dispatch is **compile-time only**. JasperFx's source
generator emits the dispatcher and there is no runtime fallback, so:

- the generator runs in the assembly that **defines** the aggregate, so that assembly is the one that
  has to reference Fisher — the package carries `JasperFx.Events.SourceGenerator` inside it, so there
  is nothing to add yourself;
- the aggregate needs an **identity member**, because the generator keys the dispatcher on
  `(TDoc, TId)` and resolves `TId` from it.

Fisher requires the identity member and says so, rather than defaulting to the stream identity
primitive and failing later with a message about a missing generated dispatcher.
:::

::: tip
`TId` is the aggregate's **own** id type, not the stream identity primitive. They coincide for a plain
`Guid Id`, but a [strong-typed id](/documents/identity#strong-typed-identities) is a wrapper struct
and the dispatcher is keyed on the wrapper.
:::

## Registering wins over discovery

Auto-discovery is the fallback. If a projection is registered for the type, that one is used — which
is what makes switching a type from live to `Inline` or `Async` a one-line change with no call-site
edits:

```cs
opts.Projections.Snapshot<Order>(SnapshotLifecycle.Async);
```

## When live is the right answer

- The stream is short.
- Reads are rare relative to writes.
- You need the state *exactly* as of now, and cannot accept eventual consistency.
- You are still deciding on the read model's shape — live costs nothing to change.

## When it stops being the right answer

A long stream is folded on every read. Two ways out, and they solve different problems:

| Problem | Fix |
| :--- | :--- |
| The stream is read often | A stored [snapshot](/events/snapshots) — `Inline` or `Async` |
| The stream is **long** | [Compacting](/events/rewriting#stream-compacting) |

::: tip
Compacting helps live aggregation directly and needs no code change: JasperFx's aggregator
fast-forwards from a `Compacted<T>` before folding, so the stream starts from that state and applies
only what follows.
:::

## Live aggregation reads pending events too

`ProjectLatest` folds the session's uncommitted events on top of the committed state. See
[ProjectLatest](/events/projections/project-latest).

## FetchForWriting uses whichever exists

```cs
var stream = await session.Events.FetchForWriting<Order>(orderId);
```

A stored snapshot if there is one, live aggregation otherwise — so your command handlers do not
change when the lifecycle does.

## Unknown event types are skipped

A stream read skips an event whose `dotnet_type` this deployment cannot resolve, so an application can
still read streams containing events it does not know about. The
[async daemon does not](/events/projections/async-daemon), for the opposite and equally good reason.
