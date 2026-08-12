# Appending Events

## Starting a stream

```cs
// Fisher assigns the id. StartStream returns a StreamAction — its Id is the stream's identity.
var stream = session.Events.StartStream<Order>(new OrderPlaced(…), new OrderLineAdded(…));
var streamId = stream.Id;

// Or you name it
session.Events.StartStream<Order>(orderId, new OrderPlaced(…));

// Without an aggregate type
session.Events.StartStream(streamId, new SomethingHappened(…));
```

## Appending to an existing stream

```cs
session.Events.Append(streamId, new OrderShipped(DateTimeOffset.UtcNow));
session.Events.Append(streamId, evt1, evt2, evt3);

await session.SaveChangesAsync();
```

Nothing is written until `SaveChangesAsync`. Events commit in the same transaction as documents,
patches, raw SQL commands and inline projection writes.

## How the append works

Fisher uses **QuickAppend** — direct `INSERT` statements, no stored procedures. Version numbers are
assigned **client-side** from the stream's current version, read inside the write transaction under
`BEGIN IMMEDIATE`.

The sequence numbers SQLite assigns come back via a trailing `SELECT` by stream and version range,
where Marten uses a bulk function and Polecat `OUTPUT … INTO`. That read-back is what supplies the
`seq_id` a [tag row](/events/dcb) is keyed by.

## Optimistic concurrency

```cs
session.Events.Append(streamId, expectedVersion: 5, new OrderShipped(…));
```

A losing write throws `EventStreamUnexpectedMaxEventIdException` at `SaveChangesAsync`.

```cs
session.Events.AppendOptimistic(streamId, new OrderShipped(…));   // reads the version for you
```

### The exclusive methods fail where the siblings wait

::: warning
`AppendExclusive`, `FetchForExclusiveWriting` and `WriteExclusivelyToAggregate` are the **optimistic**
methods on Fisher.

Marten takes an advisory lock and Polecat a row lock, so a competing session **waits** its turn.
SQLite has no row locks and one writer per file, so the equivalent would mean holding a
`BEGIN IMMEDIATE` open from the fetch until `SaveChangesAsync` — blocking every other writer in the
process for as long as the caller holds the session.

**The safety property is unchanged**: the version guard still runs inside the write transaction, so
there is no lost update. What differs is that a loser gets `EventStreamUnexpectedMaxEventIdException`
instead of waiting.
:::

## FetchForWriting

The command-handling shape: fetch, decide, append, commit.

```cs
var stream = await session.Events.FetchForWriting<Order>(orderId);

if (stream.Aggregate is { Shipped: false })
{
    stream.AppendOne(new OrderShipped(DateTimeOffset.UtcNow));
}

await session.SaveChangesAsync();
```

Or with the callback form:

```cs
await session.Events.WriteToAggregate<Order>(orderId, stream =>
{
    stream.AppendOne(new OrderShipped(DateTimeOffset.UtcNow));
});
```

`FetchForWriting` returns the aggregate from a snapshot if one is stored, and by live aggregation
otherwise.

::: warning
Fisher tracks pending streams in a **dictionary keyed by identity**, where Polecat uses a list. So
`FetchForWriting` reuses an already-tracked `StreamAction` rather than constructing a fresh one —
replacing the dictionary entry would silently drop events an earlier `Append` had queued for the same
stream in the same session.
:::

### By natural key or strong-typed id

```cs
var stream = await session.Events.FetchForWriting<Order, OrderId>(orderId);
var stream = await session.Events.FetchForWritingByNaturalKey<Order>("INV-2026-0042");
```

::: tip
Where the two readings coincide — a string id on a string-identity store — **the stream identity type
wins**, and the string is read as the stream key. Which reading applies must not depend on whichever
aggregate types happen to declare a natural key. `FetchForWritingByNaturalKey` is the unambiguous
spelling.
:::

See [Natural Keys](/events/natural-keys).

### By tags

```cs
var boundary = await session.Events.FetchForWritingByTags<Basket>(query);
```

See [DCB](/events/dcb).

## FetchLatest and ProjectLatest

```cs
var order = await session.Events.FetchLatest<Order>(orderId);
```

`ProjectLatest` folds the session's **pending** events on top of the committed state — see
[ProjectLatest](/events/projections/project-latest).

## Event metadata

The session's correlation id, causation id, user name and headers are copied onto each event that does
not already carry its own, each gated on its `Enable*` option. See [Event Metadata](/events/metadata).

## Cross-tenant appends

```cs
session.ForTenant("globex").Events.StartStream<Order>(id, new OrderPlaced(…));
```

The append path needed nothing for this: the planner already writes the *stream action's* tenant
rather than the session's. See [Writing across tenants](/documents/multi-tenancy#writing-across-tenants).

## An append observer

```cs
opts.Events.AppendObserver = events => { … };
```

Fires after commit, so "everyone can see this now" is true when it runs — which is why it does **not**
fire for an [enlisted session](/documents/sessions#enlisting-in-your-own-connection-or-transaction),
where Fisher is not told when you commit.

## Versions and inline projections

An [inline projection](/events/projections/inline) needs its events to already know their versions,
but Fisher normally assigns those inside the write transaction. So the version is read early, *outside*
the lock, for the projection to read.

::: tip
**That is not a weakened guard.** The same versions are re-derived inside the transaction and the
optimistic concurrency check still runs there, so a racing writer still fails the commit. The early
pass exists only to give projections something to read.
:::
