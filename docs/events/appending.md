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
// The stream is at version 4; after this one event it will be at 5.
session.Events.Append(streamId, expectedVersion: 5, new OrderShipped(…));
```

`expectedVersion` is the version of the stream **after** the append, as on Marten and Polecat. A losing
write throws `EventStreamUnexpectedMaxEventIdException` at `SaveChangesAsync`.

::: warning Changed in Fisher 2.0
Fisher 1.x read this argument as the version **before** the append. To migrate, add the number of events
being appended to the version you were passing. A value smaller than the number of events throws
`ArgumentOutOfRangeException` at the call, so `Append(id, 0, e)` on a new stream is now `Append(id, 1, e)`.
`FetchForWriting` and `AppendOptimistic` are unaffected.
:::

```cs
session.Events.AppendOptimistic(streamId, new OrderShipped(…));   // reads the version for you
```

### Exclusive writes {#exclusive-writes}

`AppendExclusive`, `FetchForExclusiveWriting` and `WriteExclusivelyToAggregate` take the database's write lock
(`BEGIN IMMEDIATE`) before they read, and the session holds it until `SaveChangesAsync` commits or the session is
disposed. A competing exclusive writer waits for its turn, as on Marten and Polecat, rather than failing on the
version guard. The natural key forms resolve the key inside the lock, so the key cannot move before the commit.

```cs
await using var session = store.LightweightSession();

// Waits here while another session holds the lock, then reads the stream inside it
var stream = await session.Events.FetchForExclusiveWriting<Order>(orderId);
stream.AppendOne(new OrderShipped(…));

// Commits and gives the lock back
await session.SaveChangesAsync();
```

::: warning SQLite locks the file, not the stream
Marten takes an advisory lock and Polecat a row lock, so a writer to another stream carries on. SQLite has one writer
per database file, so while a session holds the lock **every other writer on the file waits**, whatever it writes.
Readers are not blocked under WAL. Hold the lock for as short a time as the work allows: fetch, decide, append,
save. Don't call out to another service between the fetch and `SaveChangesAsync`.
:::

A few things follow from holding the lock across the session:

- A writer that waits longer than the busy timeout and every retry in `StoreOptions.ResiliencePipeline` gets
  `JasperFx.Events.StreamLockedException`, naming the stream.
- `SaveChangesAsync` writes inside the transaction the fetch opened, then commits it. A failed commit rolls it back,
  and disposing the session without saving rolls it back too. Either way the lock is released. A save with nothing
  to write still releases it.
- The event tables, the aggregate's document table and every inline projection's tables are created before the lock
  is taken. A document type the store has never written, first stored after the lock is taken, can't have its table
  created then, because that migration runs on another connection and would wait on the lock. Fisher throws instead,
  naming the type. Apply the schema at startup and this never comes up.
- A Hi-Lo document can't reserve a new block of ids while the lock is held, for the same reason. An id the current
  block can still supply is fine.
- A session enlisted in a transaction you own (`SessionOptions.ForTransaction`) takes no lock of its own. Your
  transaction already decides that.

::: tip Changed in Fisher 2.0
Fisher 1 made these the *optimistic* methods: a competing writer failed with
`EventStreamUnexpectedMaxEventIdException` instead of waiting (fisher#689).
:::

### When the file itself is contended

Optimistic concurrency is about two writers reaching the *same stream*. A different thing can happen
first: SQLite permits **one writer per database file**, so a commit can fail to take the write lock at
all, whatever streams it was writing.

That is retried — jittered exponential backoff through `StoreOptions.ResiliencePipeline`, recorded on
the current span and on `fisher.write_lock.retries`. When the retries are not enough and the unit of
work was appending events, `SaveChangesAsync` throws `JasperFx.Events.StreamLockedException` — the type Marten and Polecat throw
for their own lock contention, so a Wolverine policy reads the same on all three:

```cs
opts.OnException<JasperFx.Events.StreamLockedException>().RetryWithCooldown(/* … */);
```

::: tip
`StreamId` carries the first stream in the unit of work, which is the whole of it for the
aggregate-handler shape and is what the siblings can express. `StreamIds` names every stream in the
unit of work, because on Fisher the lock that was lost is the **file's** — they all lost it together.
:::

A unit of work that appended no events keeps the raw `SqliteException`: a `StreamLockedException`
naming no stream would say less than the driver already does.

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

**`FetchForWriting` folds the stream on every call**, whatever the aggregate's projection lifecycle.
That is deliberate and differs from [`FetchLatest`](/events/snapshots), which reads an `Inline`
aggregate's projected document: the two ask different questions. `FetchLatest` reports current state,
while this is the read half of a read-modify-write whose guard is the stream's version — so folding
the stream is what the version it hands back has to agree with.

::: warning
Fisher tracks pending streams in a **dictionary keyed by identity**, where Polecat uses a list. So
`FetchForWriting` reuses an already-tracked `StreamAction` rather than constructing a fresh one —
replacing the dictionary entry would silently drop events an earlier `Append` had queued for the same
stream in the same session.
:::

### Caching the aggregate between fetches

A hot stream re-folds its whole history on every command. `CacheAggregatesForWriting<T>()` keeps
recently fetched snapshots in a node-local cache, so a later fetch folds only the events after the
cached one:

```cs
opts.Events.CacheAggregatesForWriting<Order>();          // off for every type by default
opts.Events.CacheAggregatesForWriting<Order>(sizeLimit: 5000);
```

**The cached snapshot is only ever a baseline.** The stream's version and every event after the cached
one are still read on every call, and the optimistic concurrency assertion on append is untouched — so
a stale entry costs a larger fold, never a wrong aggregate and never a suppressed concurrency failure.
Turning it on is unobservable except in latency.

::: tip
**This is worth more on Fisher than on Marten or Polecat.** There the cache removes a snapshot *load*;
here it removes the fold itself, because `FetchForWriting` folds by design. It is opt-in per aggregate
type because the win is proportional to how often one stream is fetched for writing — real on a hot
aggregate under load, and only overhead on one written once.
:::

The contract is `JasperFx.Events.Fetching.IAggregateWriteCache`, shared with Marten and Polecat, so a
consumer targeting more than one store configures caching once. Supply your own implementation — an
adapter over a shared `IMemoryCache`, say — through `opts.Events.AggregateWriteCaching.Cache`.

::: warning
**A cached baseline is derived state, and [event rewriting](/events/rewriting) does not reach derived
state.** Masking or overwriting an event body below a cached baseline leaves that baseline holding
what the old body produced, exactly as it leaves an already-written snapshot, document or flat table
holding it. The cache is node-local by design — masking on one node could not evict another's — so
this is the same caveat masking already carries, one place further along. Leave an aggregate whose
history you rewrite unenrolled, or treat a rewrite as requiring a restart of the processes holding
it.
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
