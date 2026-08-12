# Storing Documents

## The unit of work

Nothing is written until `SaveChangesAsync()`:

```cs
await using var session = store.LightweightSession();

session.Store(user);
session.Store(order);
session.Delete<Invoice>(invoiceId);

await session.SaveChangesAsync();   // one transaction
```

Documents, [events](/events/appending), [patches](/documents/partial-updates-patching),
[raw SQL commands](/documents/querying/raw-sql), inline projection writes and other tenants' rows all
commit together.

::: tip
**The unit of work is strictly sequential**, where Marten runs its operations in parallel and
aggregates the failures. One writer per file means there is nothing to gain from parallelism, and
each queued operation is executed as its own command with its own reader, sharing only the
transaction.
:::

## Store, Insert, Update

```cs
session.Store(document);       // upsert — insert or update
session.Insert(document);      // insert only; a duplicate id fails
session.Update(document);      // update only; a missing row fails
session.Store(doc1, doc2, doc3);
```

`Store` is the one you want almost always. The upsert is a single
`INSERT … ON CONFLICT … DO UPDATE … RETURNING` statement, as on Marten — SQLite has had upsert syntax
since 3.24.

::: tip
The document is **serialized when the batch runs, not when `Store` is called**, so mutating it in
between still takes effect. That matches Marten.
:::

## What happens at commit

In order:

1. Session listeners' `BeforeSaveChanges` / dirty-tracking change detection
2. Inline projections are applied — which queues further operations, which is why it happens before
   the batch is taken
3. Document tables that do not exist yet are created
4. `BEGIN IMMEDIATE`, and every queued operation runs
5. [DCB boundary checks](/events/dcb), event appends, tag rows, natural key rows
6. `ITransactionParticipant.BeforeCommitAsync` and the outbox's — the last things inside
7. **Commit**
8. `AfterCommitAsync` hooks, the append observer, listeners — all outside the resilience pipeline

## Deleting

See [Deleting Documents](/documents/deletes).

## Storing with a revision

If a type uses [numeric revisions](/documents/concurrency#numeric-revisions):

```cs
session.Store(doc, revision: 4);
session.UpdateRevision(doc, 4);
session.TryUpdateRevision(doc, 4);   // no exception if it loses
```

## Cross-tenant writes

```cs
session.ForTenant("globex").Store(order);
```

See [Multi-Tenanted Documents](/documents/multi-tenancy#writing-across-tenants).

## Bulk loading

For a lot of documents at once, `Store` in a loop is not the tool — see
[Bulk Insert](/documents/bulk-insert).

## Initial data

To seed reference data at startup, see [Initial Baseline Data](/documents/initial-data).

## Storing without registering

You do not have to register a document type. The first `Store` of an unregistered type creates its
table.

::: warning
The exception is an [enlisted session](/documents/sessions#enlisting-in-your-own-connection-or-transaction),
where a missing table throws by name rather than being created — running a migration on a second
connection from inside your transaction would deadlock against your own write lock.

The other place this bites is a query: SQLite resolves a table name when it *prepares* a statement,
so `Query<T>()` against a type that has never been written fails with `no such table` rather than
returning an empty list. Register the type, or apply the schema at startup.
:::
