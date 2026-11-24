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

## ProjectLatestForCommit: the aggregate as the commit writes it {#project-latest-for-commit}

`ProjectLatest` folds the pending events as they are, before the commit has given them a version, a
timestamp or the session's metadata. `ProjectLatestForCommit` gives them those now, the same way
`SaveChangesAsync()` will, and the commit keeps what it assigned. So the aggregate it returns is the one
the commit writes: the inline snapshot, a later `FetchLatest`, and the daemon's async snapshot all agree
with it, versions, timestamps and correlation id included. It stores nothing.

<!-- snippet: sample_project_latest_for_commit -->
<!-- endSnippet -->

It is for an endpoint that stores its response in the same transaction as its work (wolverine#5721), and it
follows Marten's `ProjectLatestForCommit` (marten#6149).

- **The stream's version has to be known before the commit.** Start the stream with `StartStream`, fetch it
  with `FetchForWriting`, or `Append` with an expected version. Otherwise it throws
  `InvalidOperationException` and leaves the pending events alone.
- **It takes the stream's id.** For a natural key, fetch the stream with `FetchForWritingByNaturalKey` and pass
  its `Id`.
- **The commit refuses a stream whose expected version changed after the call**, by appending with a new
  expected version or fetching it for writing again, because the aggregate it returned would no longer match.
  Call it again after the last change. Appending more events through the same stream is fine; they are just
  not covered by the call.
- **What the call does not settle**: the event sequence (`IEvent.Sequence`) is assigned at commit, and session
  metadata not set at the call (a correlation id set afterwards, say) is filled in at commit.

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
