# Optimistic Concurrency

Two styles, and they are **alternatives**: a type carries one column or the other. Declaring both is
refused at configuration time rather than letting the storage descriptor pick one silently.

| | Column | Guard |
| :--- | :--- | :--- |
| Guid version | `guid_version` | The stored version must equal the one you loaded |
| Numeric revision | `revision` | The supplied revision must be **strictly greater** than the stored one |

## Guid versions

```cs
opts.Schema.For<Order>().UseOptimisticConcurrency();
opts.Policies.AllDocumentsUseOptimisticConcurrency();
```

Or by implementing `IVersioned`:

```cs
public class Order : IVersioned
{
    public Guid Id { get; set; }
    public Guid Version { get; set; }
}
```

::: tip
`IVersioned` **turns optimistic concurrency on**, as on both siblings — with it off the column is
neither written nor read, so mapping a member onto it would mean nothing. The converse does not hold:
`UseOptimisticConcurrency()` alone maps nothing, because there is no member named.
:::

A losing write throws `ConcurrencyException` at `SaveChangesAsync`.

::: tip
How the guard is read is worth knowing: the upsert carries a `where` on the version and ends
`RETURNING id`, and when the guard does not match SQLite returns **no row** and leaves the row
untouched — which is exactly what the operation's postprocessing reads as a concurrency failure.
Verified against SQLite 3.51 before anything was built on it.
:::

## Numeric revisions

```cs
opts.Schema.For<Order>().UseNumericRevisions();
```

Or by implementing `JasperFx.IRevisioned`:

```cs
public class Order : IRevisioned
{
    public Guid Id { get; set; }
    public int Version { get; set; }
}
```

```cs
session.Store(doc, revision: 4);       // fails unless 4 > the stored revision
session.UpdateRevision(doc, 4);
session.TryUpdateRevision(doc, 4);     // no exception if it loses
```

`0` means **auto** — increment whatever is stored.

### The sharp edge

::: warning
The semantics are Marten's, deliberately, and they have a sharp edge. `Store` passes the document's
own `Version` as the expected revision, and the guard requires it to be **strictly greater** than the
stored one.

So re-storing an instance that still carries the revision it was written at is a
`ConcurrencyException`, **not** an increment. The way forward is `UpdateRevision(doc, Version + 1)`,
or resetting `Version` to 0 for auto.
:::

Polecat diverged to an equality rule for its own pipeline's parity; following it here would mean
writing SQL the shared operations do not describe, and would silently disagree with Marten about what
an explicit revision means.

### The revision is always read back

Even when no member is mapped to it — asymmetric with a Guid version, which is dropped from the
query-only projection.

That is on purpose: the revision you will guard the *next* write with is the one the database just
computed, so a read that withheld it would leave every explicit store guessing.

::: tip
The column is INTEGER, and that is load-bearing. A TEXT affinity would sort revision 10 below
revision 9 and turn the "must be greater" guard into nonsense.
:::

## Which to use

Guid versions are the safer default and need nothing from the caller. Numeric revisions are worth it
when the revision is part of your API — an HTTP client sending `If-Match: 4` reads better than one
sending a Guid, and `Fisher.AspNetCore` can [serve an ETag from either](/documents/aspnetcore#etags).

## Event stream concurrency

Streams have their own guard. See [Appending Events](/events/appending#optimistic-concurrency) —
including why `AppendExclusive` **fails** here where the siblings **wait**.
