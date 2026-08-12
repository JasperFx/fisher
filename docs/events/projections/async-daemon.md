# Asynchronous Projections

The async daemon runs projections in the background, off the request path.

```cs
builder.Services.AddFisher(opts =>
{
    opts.Connection("Data Source=app.db");
    opts.Projections.Snapshot<Order>(SnapshotLifecycle.Async);
    opts.Projections.Add(new SalesByCustomer(), ProjectionLifecycle.Async);
})
.ApplyAllDatabaseChangesOnStartup()
.AddAsyncDaemon(DaemonMode.Solo);
```

Or build one by hand:

```cs
var daemon = await store.BuildProjectionDaemonAsync();
await daemon.StartAllAsync();
```

::: danger
**`DaemonMode.HotCold` is refused, and it is a real limitation rather than an omission.** Hot-cold
failover means several nodes competing for a leadership lease through the database, and a Fisher store
is a file SQLite does not make safe to share across nodes. Accepting the mode and running `Solo` would
give an application the opposite of the guarantee it asked for — every node projecting at once.
:::

## WAL matters here

::: warning
**WAL is what lets the daemon read while a session writes.** It is on by default; if you turn it off,
Fisher logs a warning at startup rather than refusing to run, because a non-WAL store projects
correctly — it just serialises the daemon against every writer, which presents as a *slow projection*
rather than as a misconfiguration.

The [health check](/documents/aspnetcore#health-check) is how an operator finds out the warning
mattered.
:::

## The high-water mark is simply max(seq_id)

Marten and Polecat must distinguish the highest sequence *issued* from the highest safe to *read*,
because a PostgreSQL sequence or a SQL Server IDENTITY hands out numbers outside the transaction — a
writer can hold 7 uncommitted while 8 commits ahead of it.

On SQLite, one writer per file plus `BEGIN IMMEDIATE` means a transaction's sequences fully commit
before the next writer allocates any, and a rollback returns the number. **Committed sequences are
contiguous**, so there is no separate answer to give.

::: warning
If you are extending Fisher: **do not reintroduce gap-skipping.** It would guard a state that cannot
occur.
:::

## Liveness

```cs
opts.Events.HighWaterLivenessInterval = TimeSpan.FromSeconds(5);   // 0 turns it off
```

The mark's row moves when the mark *advances*, which is a different question from whether the loop is
*running* — a quiet store advances nothing and would otherwise be indistinguishable from a dead
daemon. So the high-water agent re-stamps its row on an idle cycle, and **that is the only liveness
signal there is**.

::: warning
The extended progression `heartbeat` column does **not** answer it. JasperFx returns early for the
high-water shard, so nothing ever writes that column for that row — and a health check reading it
looks like it has a signal it does not have.
:::

::: tip
It is **throttled**, where Marten's per-tenant equivalent writes on every cycle. That difference is
SQLite's: a write takes the file's one write lock, so touching at the slow-polling interval would make
an otherwise read-only store a permanent 1 Hz writer with a WAL to checkpoint.
:::

## The batch is atomic

Each batch commits the projection's document writes **and** the progression row in one transaction.

::: danger
Splitting them lets a crash between the two either replay events already applied or skip events never
applied — permanently, and with nothing to signal it.
:::

Sessions are collected rather than merged, and each flushes its own operations into the shared
transaction, because an operation is configured against a session as its storage context and that is
what carries tenancy.

## Unknown event types

The daemon **does not** skip an event whose type it cannot resolve, where a
[stream read does](/events/querying#unknown-event-types). Silently skipping one would leave the
projection permanently wrong.

```cs
opts.Projections.Errors.SkipUnknownEvents = true;   // if you really want that
```

Otherwise it throws, and the exception is classified as a shard failure without the daemon needing to
know Fisher's exception types.

## Errors and dead letters

```cs
opts.Projections.Errors.SkipApplyErrors = true;
```

A skipped poison event is quarantined into [`fi_dead_letters`](/events/storage#dead-letters) rather
than stopping the shard.

::: tip
The dead letter write goes on **its own connection, outside the failing batch's transaction** — that
batch is about to roll back, and a dead letter written inside it would roll back with the very failure
it is recording.
:::

Read them back:

```cs
foreach (var db in eventStore.AllDatabases())
{
    var letters = await db.QueryDeadLetterEventsAsync(…);
}
```

## Rebuilds

```cs
var daemon = await store.BuildProjectionDaemonAsync();
await daemon.RebuildProjectionAsync("SalesByCustomer", token);
await daemon.RebuildProjectionAsync<Order>(token);
```

Teardown clears the projection's documents *and* its progression rows in **one transaction** — doing
one without the other replays a projection on top of rows it already wrote.

::: warning
Test a rebuild with a row the replay **cannot** recreate. A replay rewrites every row it can still
produce, so a broken teardown is invisible against live data.
:::

## Catching up and waiting

```cs
await daemon.WaitForNonStaleData(TimeSpan.FromSeconds(5));
```

Or from a query:

```cs
await session.Query<CustomerSales>().QueryForNonStaleData(TimeSpan.FromSeconds(5)).ToListAsync();
```

::: warning
**Non-stale does not imply a post-commit listener has run.** The progression row is written *inside*
the batch's transaction, so non-stale is true the moment that commits — strictly before any listener.
Wait on the listener's own signal instead.
:::

## Event-emitting projections

A projection can raise events, and they are appended inside the batch's own transaction.

Two SQLite-shaped decisions in that:

- **The version comes from a read under the write lock.** A slice pre-assigns versions client-side from
  its own event count, which is only the stream's real version when the projection has seen every event
  on it. Fisher re-reads inside the batch's `BEGIN IMMEDIATE` and the optimistic guard runs there, so a
  projection raising events onto a stream another writer has moved on **fails the batch** instead of
  writing a wrong version.
- **Raised events are taggable.** They are routed through Fisher's own append operation, which is the
  only thing that supplies the sequence a [tag row](/events/dcb) is keyed by. Queuing bare per-event
  operations would silently make raised events untaggable.

::: warning
Polecat **no-ops** the equivalent members rather than throwing, so an event-raising projection there
drops its events with no signal. Fisher does not.
:::

## Multi-tenancy

Under [database-per-tenant](/configuration/multitenancy#database-per-tenant) the daemon runs one
instance per tenant database:

```cs
var all = await store.BuildProjectionDaemonsAsync();
var one = await store.BuildProjectionDaemonAsync("acme");
```

`AddAsyncDaemon()` hosts them all. N daemons over N files do not contend, which is the same property
that makes that tenancy a performance feature.

::: warning
The no-argument `BuildProjectionDaemonAsync()` projects the **default** file and says nothing about the
others.
:::

## What Fisher supplies, and what it does not

The daemon itself is JasperFx's — coordinator, subscription agents, shard tracker, throttled and
resilient loaders, roughly 10,500 lines. What Fisher supplies is the storage seam: progress reads and
writes, the high-water detector, the event loader, the projection batch, and the session and shard
plumbing. That is why a projection ports between the three stores unchanged.
