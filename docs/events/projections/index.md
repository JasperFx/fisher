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
opts.Projections.Add<OrdersByCustomer>(ProjectionLifecycle.Async);
opts.Projections.CompositeProjectionFor("reporting", c => { … });
opts.Projections.Subscribe(new NotifyOnShipment());
```

`Add<T>(lifecycle)` constructs the projection for you and is the spelling Marten and Polecat use, so a
registration block reads identically against all three stores. Pass an instance instead when the
projection needs constructor arguments:

```cs
opts.Projections.Add(new OrdersByCustomer(connectionString), ProjectionLifecycle.Async);
```

Both forms take an optional `AsyncOptions` lambda for rebuild and batching behaviour:

```cs
opts.Projections.Add<OrdersByCustomer>(ProjectionLifecycle.Async, o => o.BatchSize = 1000);
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

- **The Fisher package carries the generator**, so referencing Fisher is enough — no analyzer
  reference of your own. The generator runs in the assembly that *defines* the aggregate or
  projection, so that assembly is the one that has to reference Fisher.
- A conventional-method **projection class** must be declared `partial`.
- The aggregate needs an identity member, because the generator keys the dispatcher on `(TDoc, TId)`.
- `TId` is the aggregate's **own** id type — a strong-typed id is a wrapper struct, and the generated
  dispatcher is keyed on the wrapper.
:::

The generator reports these mistakes at build time. JasperFx's
[Build Diagnostics](https://shared-libs.jasperfx.net/codegen/diagnostics) page lists each error, including
what a missing `partial`, a non-public `Apply` method or a missing identity member produces.

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

When an async projection fails on an event, the daemon does one of three things:

- **Park** (the default in Fisher 2): the identity that failed is set aside in `fi_parked_identities`,
  its document stays at the last good state, and the shard keeps going for every other identity.
- **Pause**: the shard stops. This is what happens for a projection that cannot park, such as an event
  projection or a flat table.
- **Skip**: the event is quarantined into [`fi_dead_letters`](/events/storage#dead-letters) and the
  shard carries on. This was Fisher 1's default.

```cs
opts.Projections.Errors.ApplyErrors = ProjectionErrorAction.Park;   // the default
opts.Projections.Errors.SkipApplyErrors = true;                     // Skip; false means Pause
opts.RestoreV1Defaults();                                           // Skip, with Fisher 1's other defaults
```

A full rebuild never parks: it runs under `Projections.RebuildErrors`, which pauses on a failing
event. See [Parking a failing identity](/events/projections/async-daemon#parking-a-failing-identity)
for which projections can park and how to replay or discard a parked identity, and the JasperFx
documentation's [Turning it on](https://shared-libs.jasperfx.net/events/parked-identities#turning-it-on)
for how the three settings relate.

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
