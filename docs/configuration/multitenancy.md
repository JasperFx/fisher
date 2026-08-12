# Multi-Tenancy

Fisher supports the two tenancy styles its siblings do, and one of them is a substantially better fit
here than on either of them.

| Style | What it means |
| :--- | :--- |
| **Single** | No tenancy. The default. |
| **Conjoined** | One set of tables with a `tenant_id` column. |
| **Database per tenant** | One SQLite **file** per tenant. |

## Conjoined tenancy

Documents opt in per type, or by policy:

```cs
opts.Schema.For<Order>().MultiTenanted();
opts.Policies.AllDocumentsAreMultiTenanted();
```

Events opt in for the whole store:

```cs
opts.Events.TenancyStyle = TenancyStyle.Conjoined;
```

::: warning
`TenancyStyle.Conjoined` must be set **before the schema is created**. The streams and events tables
read it when they build their columns and their primary key, so it is a schema decision rather than a
runtime one. Set it inside the `DocumentStore.For` / `AddFisher` lambda, ahead of any migration.
:::

Then open a session for a tenant:

```cs
await using var session = store.LightweightSession("acme");
```

Every read and every write is scoped to that tenant. The scoping is applied as a **statement-level
pass**, not by wrapping each caller predicate — see
[Tenant scoping in LINQ](/documents/multi-tenancy#how-the-filter-is-applied) for why that distinction
was worth a bug.

### Writing across tenants

**This is where SQLite's single-writer model is the advantage rather than the constraint.** One
`SaveChangesAsync` can write several tenants' rows in one transaction:

```cs
await using var session = store.LightweightSession("acme");

session.Store(new Order { /* … */ });                      // acme's
session.ForTenant("globex").Store(new Order { /* … */ });  // globex's

await session.SaveChangesAsync();                          // one transaction
```

The alternative is a session and a transaction per tenant, which on one file means taking the write
lock N times in sequence and leaves a part-written admin operation if the process dies between two of
them. See [Multi-Tenanted Documents](/documents/multi-tenancy#writing-across-tenants).

## Database per tenant

**Arguably SQLite's best tenancy story rather than its worst.** The usual objection —
database-per-tenant is heavyweight to provision — inverts here: a tenant is a *file*. Creating one is
a file plus a migration, deleting one is deleting a file, backing one up is copying it, and one
tenant's data cannot leak into another's because there is no shared table to leak through.

It also answers the sharpest structural constraint. Under conjoined tenancy every tenant contends for
one write lock; under file-per-tenant they write concurrently. **That makes it a performance feature
as much as an isolation one**, which is not true on either sibling.

### A fixed set of tenants

```cs
opts.MultiTenantedDatabases(tenants =>
{
    // The convention: one file per tenant in a directory
    tenants.InDirectory("/var/lib/app/tenants")
           .AddTenants("acme", "globex", "initech");

    // Or name a connection string explicitly
    tenants.AddTenant("special", "Data Source=/mnt/fast/special.db");
});
```

::: tip
`StoreOptions.ConnectionString` becomes **optional** under this tenancy, because there is no
store-level file — a store-level connection string would be a database nothing writes to.
:::

### Tenants that appear at runtime

Provisioning a tenant is cheap enough to do on first use, which makes "a tenant appears without a
restart" a reasonable offer rather than an operational event:

```cs
// Any tenant id resolves to <directory>/<id>.db, whether the file exists yet or not.
opts.MultiTenantedDatabasesInDirectory("/var/lib/app/tenants");

// Or push your own set
opts.MultiTenantedDatabasesFrom(new MyTenantSource());
```

Implement `ITenantSource` to drive it from your own tenants table:

```cs
public sealed record TenantRegistration(string TenantId, string ConnectionString, bool IsActive = true);

public interface ITenantSource
{
    bool TryFind(string tenantId, out TenantRegistration registration);
    ValueTask<IReadOnlyList<TenantRegistration>> AllAsync(CancellationToken token = default);
}
```

::: tip
`TryFind` is synchronous and `AllAsync` is not, for a reason: the hot path — resolving a tenant while
opening a session — has to answer without I/O, which the directory convention manages trivially.
Enumerating every tenant is a startup and daemon concern, where an `await` is available.
:::

The two supplied sources differ deliberately:

| Source | Unknown tenant id |
| :--- | :--- |
| `DirectoryTenantSource` | **Resolves it**, creating the file on first use. Enumeration reports only files that exist. |
| `InMemoryTenantSource` | **Refuses it.** For an application pushing its own tenants list. |

### Migration per tenant

A new tenant's file is migrated the first time a connection is opened to it — not when the tenant is
resolved, because `ITenancy.DatabaseFor` is reached from the synchronous `OpenSession` and a
migration is asynchronous.

::: tip
The result is **not cached until it succeeds**. A transient failure remembered as done would leave
the tenant permanently unusable with nothing to say why.
:::

Migrating an existing set runs **sequentially**, and reports per database:

```cs
await store.ApplyAllConfiguredChangesToDatabaseAsync();
```

A failure part way leaves mixed versions whatever it throws, so `TenantMigrationException` reports
*which* databases are current. Sequential rather than parallel because each migration takes its own
file's write lock — parallelism wins nothing on the DDL and holds N connections against a pool
ceiling that sizes one file.

### Suspending and forgetting tenants

A tenant is switched off through the **source**, which is what owns the set:

```cs
var source = new DirectoryTenantSource("/var/lib/app/tenants");
opts.MultiTenantedDatabasesFrom(source);

source.Suspend("acme");                    // DisabledTenantException on use
source.Resume("acme");
```

`InMemoryTenantSource` spells the same thing `SetActive(id, false)`, and `Remove(id)` drops it
entirely.

Forgetting a tenant a process is finished with — which releases its pooled connections and their file
handles — is on the tenancy:

```cs
var tenancy = (DynamicTenancy)store.Tenancy;
await tenancy.ForgetTenantAsync("acme");
```

::: warning
**Fisher suspends; it never deletes.** Deleting a tenant here means deleting a file — the cheapest
deprovisioning of any Critter Stack store, and the most irreversible — and Fisher cannot know whether
that file is backed up. Remove the file yourself.
:::

`DisabledTenantException` is distinct from `UnknownTenantException` on purpose: "switched off" and
"never heard of it" are different operational situations, and an application handling one should not
have to guess which it got.

### The daemon under database-per-tenant

The [async daemon](/events/projections/async-daemon) runs **one instance per tenant database**:

```cs
var daemons = await store.BuildProjectionDaemonsAsync();   // all of them
var one = await store.BuildProjectionDaemonAsync("acme");  // one
```

`AddAsyncDaemon()` hosts them all. N daemons over N files do not contend — the same property that
makes this tenancy a performance feature. Under `DynamicMultiple` tenancy the hosted service polls
for new tenants every minute; a new tenant's *sessions* work immediately either way, since resolution
does not go through the hosted service.

::: tip
Shard names did **not** have to become (projection, tenant) pairs. `fi_event_progression` lives in
each tenant's own file, so every database already has its own high-water mark and its own progress
row per shard.
:::

### Cleaning

`ResetAllDataAsync` and the whole `IDocumentCleaner` surface loop every database. Cleaning only the
default would leave every other tenant's data behind while reporting success — and the caller most
likely to hit that is a test fixture.

## Why not ATTACH?

`ATTACH DATABASE` would let one connection see several tenants. It is not used, because an attachment
has per-connection lifecycle to re-establish on every pooled checkout — exactly what folding the
logical schema into the table prefix exists to avoid.
