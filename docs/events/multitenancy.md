# Event Store Multi-Tenancy

```cs
opts.Events.TenancyStyle = TenancyStyle.Conjoined;   // or Single
```

::: warning
**This must be set before the schema is created.** `fi_streams` and `fi_events` read it when they
build their columns and their **primary key**, so it is a schema decision rather than a runtime one.
Set it inside the `DocumentStore.For` / `AddFisher` lambda, ahead of any migration.
:::

Unlike documents, event tenancy is a **store-wide** setting rather than a per-type one.

## Conjoined tenancy

`fi_streams` and `fi_events` gain a `tenant_id` column, which joins the streams table's primary key —
so two tenants may reuse a stream id.

```cs
await using var session = store.LightweightSession("acme");

var stream = session.Events.StartStream<Order>(new OrderPlaced(…));
await session.SaveChangesAsync();

var events = await session.Events.FetchStreamAsync(stream.Id);   // acme's only
```

::: tip
Without the flag, two tenants using the same stream id collide on append with
`ExistingStreamIdCollisionException` — which is the correct behaviour for a single-tenant store, and
exactly what you would see if the flag were set too late.
:::

## Cross-tenant appends

```cs
await using var session = store.LightweightSession("acme");

session.Events.Append(acmeStream, new OrderShipped(…));
session.ForTenant("globex").Events.StartStream<Order>(globexStream, new OrderPlaced(…));

await session.SaveChangesAsync();   // one transaction
```

The append path needed nothing for this: the planner already writes the *stream action's* tenant
rather than the session's, so a cross-tenant append works the moment the action is stamped.

::: tip
The scopes' event operations are **not** shared with the parent's, because the pending-stream
dictionary is keyed by stream id and would otherwise merge two tenants' same-id streams into one.
Pending streams are gathered by the parent at commit rather than queued as operations, because
planning happens inside the write transaction.
:::

See [Writing across tenants](/documents/multi-tenancy#writing-across-tenants).

## Database per tenant

The alternative, and on SQLite a strong one — a tenant is a **file**, so provisioning is cheap and
tenants write **concurrently** instead of contending for one write lock.

```cs
opts.MultiTenantedDatabasesInDirectory("/var/lib/app/tenants");
```

The daemon then runs **one instance per tenant database**, which is what makes that concurrency real
for projections too:

```cs
var daemons = await store.BuildProjectionDaemonsAsync();
```

::: tip
**Shard names did not have to become (projection, tenant) pairs.** `fi_event_progression` lives in each
tenant's own file, so every database already has its own high-water mark and its own progress row per
shard. Two tenants running one projection are two daemons writing the same shard name to two
different tables — a second key would draw a distinction the file boundary already draws.
:::

See [Multi-Tenancy](/configuration/multitenancy#database-per-tenant).

## The daemon and tenant resolution

Under database-per-tenant, every read and write the daemon does is resolved against **the database it
was given** rather than the store's default.

::: warning
That parameter used to be ignored, on the true-enough grounds that a Fisher store was one file. Under
database-per-tenant it is not, and ignoring it would read one tenant's events and write **every**
tenant's documents from them. There is now a single place that resolution happens; a null falls back
to the default database, which is right for every store that is not database-per-tenant.
:::

## Cleaning

`ResetAllDataAsync` and the whole cleaner surface loop every database. Cleaning only the default would
leave every other tenant's data behind while reporting success — and the caller most likely to hit
that is a test fixture.
