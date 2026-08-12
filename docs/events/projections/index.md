# Projections Overview

A projection builds a read model from events. Fisher supports every shape the Critter Stack defines,
across every lifecycle — and because the abstractions are JasperFx's, a projection written for Marten
or Polecat runs here unaltered.

## The shapes

| Shape | Builds |
| :--- | :--- |
| [Single stream](/events/projections/single-stream-projections) | One document per stream |
| [Multi stream](/events/projections/multi-stream-projections) | One document from events across many streams |
| [Event projection](/events/projections/event-projections) | Arbitrary writes per event |
| [Flat table](/events/projections/flat) | Rows in a plain relational table |
| [Composite](/events/projections/composite) | Several projections as ordered stages under one shard |

## The lifecycles

| Lifecycle | When | Consistency |
| :--- | :--- | :--- |
| [Live](/events/projections/live-aggregates) | Folded on demand | Always current; nothing stored |
| [Inline](/events/projections/inline) | The append's own transaction | Strong |
| [Async](/events/projections/async-daemon) | A background daemon | Eventual |

## Registering

```cs
opts.Projections.Snapshot<Order>(SnapshotLifecycle.Inline);
opts.Projections.Add(new OrdersByCustomer(), ProjectionLifecycle.Async);
opts.Projections.CompositeProjectionFor("reporting", c => { … });
opts.Projections.Subscribe(new NotifyOnShipment());
```

## Conventional methods are source-generated

```cs
public class Order
{
    public Guid Id { get; set; }
    public bool Shipped { get; set; }

    public static Order Create(OrderPlaced e) => new() { … };
    public void Apply(OrderShipped e) => Shipped = true;
    public bool ShouldDelete(OrderCancelled e) => true;
}
```

::: warning
The dispatcher is emitted by `JasperFx.Events.SourceGenerator`, and **there is no runtime fallback**.

- The project defining the aggregate or projection needs a reference to that package.
- A conventional-method **projection class** must be declared `partial`.
- The aggregate needs an identity member, because the generator keys the dispatcher on `(TDoc, TId)`.
- `TId` is the aggregate's **own** id type — a strong-typed id is a wrapper struct, and the generated
  dispatcher is keyed on the wrapper.
:::

## Rebuilds

```cs
var daemon = await store.BuildProjectionDaemonAsync();
await daemon.RebuildProjectionAsync("Order", CancellationToken.None);
```

A rebuild tears down the projection's existing state and replays from the beginning of the event
store.

::: danger
**Teardown is where projection bugs hide.** A replay rewrites every row it can still produce, so a
surviving row is invisible except where the replay *cannot* recreate it — a row whose backing events
are gone, archived or compacted.

That means an ordinary rebuild test passes even when teardown is broken. Every place Fisher's teardown
had to learn something — flat tables, composite members, EF Core-backed documents — is pinned with a
row the replay cannot recreate, and any projection you write with unusual storage should be too.
:::

Both halves of teardown — the progression rows and the documents — run in **one transaction**, because
clearing progress without clearing documents replays a projection on top of rows it already wrote.

::: tip
Teardown checks for the table in C#, not in SQL. SQLite resolves a table name when it *prepares* a
statement, so a `where exists (select 1 from sqlite_master …)` guard on the delete fails before the
guard could run. Names come back from `sqlite_master` first, and missing tables are skipped.
:::

## Errors

```cs
opts.Projections.Errors.SkipApplyErrors = true;
```

A skipped poison event is quarantined into [`fi_dead_letters`](/events/storage#dead-letters) rather
than stopping its shard.

## Side effects

A projection can publish messages through an [outbox seam](/events/projections/side-effects) — and the
default outbox drops every message, which is the end state rather than a placeholder. Fisher ships no
delivery mechanism.

## Testing

```cs
await store.Advanced.EventProjectionScenarioAsync(scenario =>
{
    scenario.Append(streamId, new OrderPlaced(…));
    scenario.DocumentShouldExist<Order>(streamId);
});
```

See [Integration Testing](/testing/integration).
