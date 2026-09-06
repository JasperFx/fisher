# Database Management

Fisher manages its own schema through [Weasel.Sqlite](https://github.com/JasperFx/weasel). All DDL
goes through Weasel's table definitions and migrations — there is no hand-written `CREATE TABLE`
anywhere — which is what makes `AutoCreate.None` honoured everywhere for free rather than at each call
site's discretion.

## Applying the schema

```cs
// At startup
builder.Services.AddFisher(opts => { … }).ApplyAllDatabaseChangesOnStartup();

// Or explicitly
await store.ApplyAllConfiguredChangesToDatabaseAsync();
```

## Asserting the schema

The read half of applying it: throw if the database does not already match the configuration, and
change nothing.

```cs
// At startup — the host refuses to start against a drifted database
builder.Services.AddFisher(opts => { … }).AssertDatabaseMatchesConfigurationOnStartup();

// Or explicitly
await store.AssertDatabaseMatchesConfigurationAsync();
```

For a deployment that applies its schema out of band, and for the CI step that proves the deployed
database still matches what the code configures. It spans **every** database — under
database-per-tenant, asserting one file and calling the store verified is the answer most likely to
be wrong — and stops at the first mismatch.

::: warning
`AssertDatabaseMatchesConfigurationOnStartup()` and `ApplyAllDatabaseChangesOnStartup()` are
**alternatives**, and asking for both throws. Applying the changes at startup would make the
assertion a check on the schema that same startup just wrote.
:::

::: tip
`AutoCreate.None` is deliberately *not* consulted here. That setting says the schema is not Fisher's
to change, and asserting changes nothing — declining to verify because the store was told not to
write would make the strictest configuration the one with the fewest guarantees.
:::

To see the difference rather than throw on it, see
[Previewing a migration](/schema/migrations#previewing-a-migration).

## AutoCreate

```cs
opts.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
```

| Value | Behaviour |
| :--- | :--- |
| `CreateOrUpdate` | Create what is missing, migrate what exists. The default. |
| `CreateOnly` | Create what is missing; never alter. |
| `All` | Drop and recreate. |
| `None` | Never touch the schema. |

::: tip
`AutoCreate.None` **wins over** `ApplyAllDatabaseChangesOnStartup()`. The hosted service starts and
does nothing, rather than the registration quietly overriding your policy.
:::

## What gets created

| | |
| :--- | :--- |
| `fi_events`, `fi_streams`, `fi_event_progression` | The event store |
| `fi_dead_letters` | Poison events |
| `fi_event_tag_*` | One per [DCB tag](/events/dcb) type |
| `fi_natural_key_*` | One per [natural key](/events/natural-keys) definition |
| `fi_hilo` | Numeric identity sequences |
| `fi_doc_*` | One per registered document type |
| flat tables | One per [flat-table projection](/events/projections/flat) |

## Document tables can also be created on demand

A document type can be stored without ever being registered, and a snapshot type is registered by
projection configuration — so the first write may be the first time the table is needed. Fisher
creates it at commit.

A **read** does the same: `Query<T>()` or `LoadAsync<T>` against a type nothing has written yet
provisions its table and answers empty, rather than failing. That matters more than it sounds, because
it is what every cold start does — resolving a cache before anything has populated it, listing a
collection on a fresh install.

::: warning
An [enlisted session](/documents/sessions#enlisting-in-your-own-connection-or-transaction) is the one
exception, on reads as on writes: running a migration on a second connection from inside your
transaction would deadlock against your own write lock, so a missing table throws by name instead.
Apply the schema before enlisting.
:::

## Multi-tenancy

Under [database-per-tenant](/configuration/multitenancy#database-per-tenant), migration is per
database and runs **sequentially** — each takes its own file's write lock, so parallelism wins nothing
on the DDL. A failure part way leaves mixed versions whatever it throws, so
`TenantMigrationException` reports *which* databases are current.

A tenant that appears at runtime is migrated the first time a connection is opened to its file, and
the result is not cached until it succeeds.

## In this section

| | |
| :--- | :--- |
| [How Documents are Stored](/schema/storage) | Table shapes and column types |
| [Schema Migrations](/schema/migrations) | Deltas, and what SQLite cannot alter |
| [Exporting Schema Definition](/schema/exporting) | Generating DDL scripts |
| [Tearing Down Document Storage](/schema/cleaning) | Cleaning, resetting, dropping |
