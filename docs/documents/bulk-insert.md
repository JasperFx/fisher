# Bulk Insert

```cs
await store.Advanced.BulkInsertAsync(documents);

await store.Advanced.BulkInsertAsync(documents,
    mode: BulkInsertMode.IgnoreDuplicates,
    batchSize: 1000);
```

## There is no SqlBulkCopy, and none is needed

On SQLite the cost of an insert is dominated by the **transaction**, not by the statement — so a
prepared statement re-executed with rebound parameters inside one transaction is already the fast
path.

The statements are the ones Fisher's ordinary document writes use, reached through a session. A
second set of write SQL is exactly where the positional `?` contract those statements maintain would
drift apart unnoticed.

## batchSize is a lock-hold ceiling, not a throughput knob

::: warning
One writer per file means a single transaction over a very large set blocks **every other writer** for
its whole duration. `batchSize` bounds that.

The trade is that **bulk insert is not atomic across batches**: a failure part way leaves earlier
batches committed. That is a decision rather than an oversight, and it is stated here so it is not
discovered.
:::

## Modes

| Mode | Behaviour |
| :--- | :--- |
| `InsertsOnly` | Plain inserts. A duplicate id fails. |
| `IgnoreDuplicates` | Documents whose id is already stored are skipped. |
| `OverwriteExisting` | Upserts. |

### IgnoreDuplicates

**Fisher filters, where both siblings use a statement.** Marten has `on conflict do nothing` and
Polecat a temp table and a `MERGE`; Fisher's four write statements are consumed by the shared storage
operations *by name*, so a fifth would need a slot on Weasel's own descriptor. Each batch instead
reads which of its ids are already stored and queues only the rest.

Three things about that read:

- **It deliberately ignores the soft-delete and hierarchy filters.** The question is not "can I read
  this" but "would inserting this collide", and a soft-deleted row still holds the primary key. This
  is one of only two places in Fisher where going around the implicit filters is correct. It *does*
  scope by tenant, because a conjoined table keys on `(tenant_id, id)`.
- **Both sides compare as invariant strings.** Microsoft.Data.Sqlite hands an INTEGER column back as
  `long` while an `int` identity's raw value is an `int`, and boxed to `object` those never compare
  equal — so without the normalisation an int-keyed type would find nothing and fail on the very
  constraint the mode exists to avoid.
- **The probe is outside the write transaction, and the window is not silent.** A concurrent writer
  inserting one of the same ids in between makes the insert fail with its unique-constraint violation
  rather than being skipped. Closing the window would mean holding `BEGIN IMMEDIATE` across the probe
  through an enlisted session, which forfeits the busy retry — a worse trade for the operation most
  likely to contend for the write lock.

## Tenancy

```cs
await store.Advanced.BulkInsertAsync(documents, tenantId: "acme");
```

## What bulk insert does not do

It is a document operation. It does not append events, does not run
[inline projections](/events/projections/inline), and does not fire
[session listeners](/documents/listeners) — it is not a unit of work.
