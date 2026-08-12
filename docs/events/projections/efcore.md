# EF Core Projections

A projection whose documents are **EF Core entities** rather than Fisher documents.

```shell
dotnet add package Fisher.EntityFrameworkCore
```

```cs
builder.Services.AddFisher(opts =>
{
    opts.Connection(connectionString);

    // Register the storage FIRST — see below
    opts.ProjectToEfCore<OrderSummary, Guid, AppDbContext>(
        tableName: "order_summaries",
        contextFactory: () => new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options));

    opts.Projections.Add(new OrderSummaryProjection(), ProjectionLifecycle.Async);
})
.AddAsyncDaemon(DaemonMode.Solo);
```

## The registration is the whole seam

**That is the divergence from Polecat.** An ordinary `SingleStreamProjection<TDoc, TId>` or
`MultiStreamProjection<TDoc, TId>` with conventional `Apply` methods writes into EF **without knowing
it does** — the projection never mentions EF.

::: tip
Polecat's EF path is reachable only by deriving from `EfCoreSingleStreamProjection` /
`EfCoreMultiStreamProjection`, which makes every EF-backed projection a different *kind* of
projection. Here a projection is an ordinary one that happens to be stored in EF.
:::

Two exceptions, each for a real reason:

- **`EfCoreEventProjection<TContext>`** — the per-event shape genuinely needs a base class, having no
  storage indirection to swap.
- **`EfCoreContext<TDoc, TId, TContext>()`** — the escape hatch on the identity setter, for an `Apply`
  that must reach beyond its own aggregate:

```cs
public void Apply(OrderShipped e, OrderSummary view)
{
    var context = this.EfCoreContext<OrderSummary, Guid, AppDbContext>();
    // null when the projection is not EF-backed, including during live aggregation
}
```

## Order matters, and it is checked

::: warning
**Call `ProjectToEfCore` before registering the projection that produces the document.** Registering a
projection maps its document type, and a mapped type gets a Fisher `fi_doc_*` table in the migration —
so the other order leaves the type with two homes.

It is checked rather than documented, the same "this line has to come first" shape
`SeedInitialDataOnStartup` has.
:::

## The context reads on its own connection and writes on Fisher's

Both halves are forced rather than chosen:

- A projection's storage is resolved **before** the batch has opened the connection it will commit on,
  so there is nothing to build against — the factory takes no connection, and
  `UseSqlite(connectionString)` is the expected body.
- The storage **reads** — every slice loads its current aggregate — long before there is a transaction
  to read in.

What the context must *not* do is **write** on that connection, and it does not: every mutation stays
in EF's change tracker until `SaveChangesAsync`, which runs inside Fisher's transaction on Fisher's
connection.

::: danger
Two connections writing to one SQLite file is a **self-deadlock that presents as a hang**, not an
error. This is the one shape where a second connection is still correct, precisely because it never
writes.
:::

Verified against EF Core 9.0.14 and Microsoft.Data.Sqlite 10.0.9 before anything was built on it: a
context that has already queried through its own connection accepts being moved onto another and
writes through it, and a read on EF's own connection does not block against a `BEGIN IMMEDIATE` held
elsewhere on the file. The second is the one that would have turned an EF-backed projection into a
hang.

## Fisher does not create the EF table

::: warning
Fisher owns the shape of tables it prefixes `fi_`; an entity's shape is the `DbContext`'s, so creating
it is EF's job — an EF migration, or `EnsureCreated`.

The same reasoning keeps `CompletelyRemoveAllAsync` from dropping EF's tables. Fisher owning the file
does not make it Fisher's to clear.
:::

## Rebuild teardown

::: danger
Teardown reads the table name off the storage registry, because the sweep that finds a projection's
tables looks at *mapped* types — and an EF-backed type is deliberately not mapped.

**This is the flat-table lesson one layer over.** Without it, a rebuild replays onto the rows the
previous run left. Test it with a row the replay cannot recreate.
:::

## LoadManyAsync is FindAsync per id

Rather than one `Contains` query, because `Find` answers from the **change tracker first** — so a slice
whose entity this batch already touched comes back as the *same instance*, where a query would
materialise a second one and the two would race at commit.

## Context lifetime

One `DbContext` per batch, enlisted the moment it is created — so whatever the projection does to it
lands in the batch's transaction, and a batch that rolls back takes the entities with it.

::: tip
**The batch disposes the participants it was given.** An EF-backed projection's context is created per
batch and cannot dispose itself: it has to outlive the apply that created it *and* survive a retried
commit. Disposing at the batch boundary covers the failed batch too, which is the case that would
otherwise leak a context per attempt behind a persistently failing shard.
:::

## Retries

::: warning
EF Core's `SaveChangesAsync` accepts its changes when its own command succeeds, not when Fisher
commits — so a retried batch would find a context that believed it had already saved. Fisher's
participant saves with `acceptAllChangesOnSuccess: false`. See
[Transaction Participants](/documents/transaction-participants).
:::

## Packaging

- **`Microsoft.EntityFrameworkCore.Relational` only** — which EF provider your `DbContext` uses is your
  decision.
- **Pinned to EF Core 9.x**, because the package multi-targets net9.0 and net10.0 and EF Core 10 is
  net10-only.
