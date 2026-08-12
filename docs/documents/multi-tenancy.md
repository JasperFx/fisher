# Multi-Tenanted Documents

```cs
opts.Schema.For<Order>().MultiTenanted();
opts.Policies.AllDocumentsAreMultiTenanted();
```

The table gains a `tenant_id` column, which joins the primary key — so `(tenant_id, id)` is unique
rather than `id` alone, and two tenants may reuse an identity.

```cs
await using var session = store.LightweightSession("acme");

session.Store(order);                              // acme's
var orders = await session.Query<Order>().ToListAsync();   // acme's only
```

For a database file per tenant instead, see
[Multi-Tenancy](/configuration/multitenancy#database-per-tenant).

## How the filter is applied

As **one statement-level pass**, not by wrapping each caller predicate. That distinction is worth
knowing because getting it wrong is a silent cross-tenant read:

::: warning
Composing the tenant term into a per-predicate wrapper repeats it once per predicate *and omits it
entirely from a query with no `Where` at all*. `Query<T>()` on a conjoined type would then return
every tenant's rows.

Silent, and asymmetric in the way that makes it hard to spot: the tenant owning most of the data sees
a correct-looking answer with extras, and a tenant with none sees somebody else's.
:::

`LoadAsync` and `LoadManyAsync` were never affected, because they bake the tenant term into SQL built
once in the storage's constructor. It was the composed path that could drop it — and the
[hierarchy filter](/documents/hierarchies) had already learned the identical lesson.

All three implicit filters are now statement-level passes. If you are extending Fisher, do not fold
any of them back into a per-predicate wrapper.

## Querying across tenants

```cs
session.Query<Order>().AnyTenant()
session.Query<Order>().TenantIsOneOf("acme", "globex")
```

Both **replace** the tenant term rather than composing with it — which is only possible because it is
its own pass.

Both are refused against a type that is not `MultiTenanted()`: there is no column to have an opinion
about. Same rule the soft-delete operators follow.

## Writing across tenants

**This is the one place SQLite's single-writer model is the advantage rather than the constraint.**

```cs
await using var session = store.LightweightSession("acme");

session.Store(acmeOrder);
session.ForTenant("globex").Store(globexOrder);
session.ForTenant("globex").Events.StartStream<Order>(globexId, new OrderPlaced(…));

await session.SaveChangesAsync();     // one transaction, both tenants
```

The alternative is a session and a transaction per tenant, which on one database file means taking
the write lock N times in sequence and leaves a part-written admin operation if the process dies
between two of them. A database-per-tenant store would need a distributed transaction to match what
falls out here for free.

### What a tenant scope is

A real session, not a delegating facade.

::: tip
Polecat's equivalent is 250 lines of one-line delegation, and a delegation site that forgot to pass
the tenant would be a silent cross-tenant write. Everything a scope does differently is "read a
different `TenantId`", and a session already reads its own everywhere — so a second session is the
version with no per-member correctness to get wrong.
:::

### What is shared and what is not

| Shared | Not shared | Shared by delegation |
| :--- | :--- | :--- |
| The connection lifetime | The event operations' pending-stream map | Correlation id |
| The operation queue | The identity map | Causation id |
| | The change trackers | User name |
| | The version tracker | Headers |

Each for a reason. Sharing the connection and the queue *is* the feature. The pending-stream
dictionary is keyed by stream id and would merge two tenants' same-id streams into one. The maps and
trackers are keyed by document identity, which is unique per tenant rather than globally. The last
column describes the unit of work rather than the tenant.

### The rules

- **A scope cannot commit.** `SaveChangesAsync` on one throws, naming the parent.
- **A scope disposes nothing**, so `await using` out of habit does no harm.
- **`ForTenant` twice is the same scope**, cached per tenant — so its queued events are collected
  once. A scope of a scope is a scope of the session, so the parent has one level to walk.
- **`ITenantOperations` deliberately does not offer `ForTenant`.** The flattening exists for whoever
  finds the cast.
- **A single-tenant type is refused**, from the single choke point every read and every write resolves
  storage through. A type without `MultiTenanted()` has no `tenant_id` column, so a write "for another
  tenant" would land in the one shared table and look like it worked.
- **The scopes' [DCB boundaries](/events/dcb) are checked by the parent inside its transaction**, for
  the same reason their operations are written there: a guard checked in no transaction guards
  nothing.

## Events

Event tenancy is a store-wide setting rather than a per-type one:

```cs
opts.Events.TenancyStyle = TenancyStyle.Conjoined;
```

See [Event Multi-Tenancy](/events/multitenancy). It must be set **before the schema is created**.

## Tenant id on the document

```cs
opts.Schema.For<Order>().Metadata(m => m.TenantId.MapTo(x => x.TenantId));
```

Enabling that column creates nothing — `MultiTenanted()` does that. It decides only whether the value
is projected back onto a member. See [Fisher Metadata](/documents/metadata).
