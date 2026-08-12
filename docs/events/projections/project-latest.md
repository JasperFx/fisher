# ProjectLatest — Include Pending Events

`ProjectLatest` folds the session's **uncommitted** events on top of the committed state, so a command
handler can see the effect of what it has just appended without saving first.

```cs
await using var session = store.LightweightSession();

session.Events.Append(orderId, new OrderLineAdded("SKU-1", 2));
session.Events.Append(orderId, new OrderLineAdded("SKU-2", 1));

// Includes both pending events
var order = await session.Events.ProjectLatest<Order>(orderId);

if (order!.LineCount > 10)
{
    session.Events.Append(orderId, new OrderFlaggedForReview());
}

await session.SaveChangesAsync();
```

## FetchLatest vs ProjectLatest

| | Reads |
| :--- | :--- |
| `FetchLatest<T>(id)` | The committed state — a stored snapshot if there is one, otherwise a fold |
| `ProjectLatest<T>(id)` | The committed state **plus** this session's pending events |

## When it is the right tool

- A handler appending several events that each depend on the state after the previous one.
- A validation that must consider what this unit of work is about to write.
- Returning the resulting state from a command handler without a second round of work.

## When FetchForWriting is better

For the ordinary fetch-decide-append shape, [`FetchForWriting`](/events/appending#fetchforwriting) is
the tool — it carries the optimistic concurrency guard, and appending through the stream it hands back
keeps the version bookkeeping correct:

```cs
var stream = await session.Events.FetchForWriting<Order>(orderId);
stream.AppendOne(new OrderShipped(DateTimeOffset.UtcNow));
await session.SaveChangesAsync();
```

`ProjectLatest` is a **read**. It does not open a boundary and does not guard anything.

## Pending events are tracked per stream

Fisher tracks them in a dictionary keyed by stream identity, so several `Append` calls for one stream
in one session accumulate onto the same action rather than replacing each other.

::: warning
That is also why `FetchForWriting` reuses an already-tracked action rather than constructing a fresh
one — replacing the entry would silently drop events an earlier `Append` had queued for the same
stream.
:::

## Tenancy

A [tenant scope](/documents/multi-tenancy#writing-across-tenants) keeps its **own** pending-stream map,
because the map is keyed by stream id and two tenants may reuse one. So
`session.ForTenant("globex").Events.ProjectLatest<Order>(id)` sees globex's pending events, not
acme's.
