# Database Management

Fisher manages its own schema through [Weasel.Sqlite](https://weasel.jasperfx.net/). All DDL
goes through Weasel's table definitions and migrations — there is no hand-written `CREATE TABLE`
anywhere — which is what makes `AutoCreate.None` honoured everywhere for free rather than at each call
site's discretion.

It also means Fisher has the same four ways of getting the schema into place as every other Critter Stack store:

| Approach | How | Typical use |
| :--- | :--- | :--- |
| Lazily, on first use | `AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate`, the default | Development, and most embedded deployments |
| Once at startup | `.ApplyAllDatabaseChangesOnStartup()` on `AddFisher()`, or `services.AddResourceSetupOnStartup()` for every JasperFx [stateful resource](https://shared-libs.jasperfx.net/cli/resources.html) in the application at once | Development, tests, and applications that own their database file |
| As a deployment step | `dotnet run -- `[`db-apply`](https://weasel.jasperfx.net/cli/db-apply.html) or `dotnet run -- resources setup`, with the application on `AutoCreate.None` | A server deployment where the schema is applied before the new version starts |
| From reviewed SQL | `dotnet run -- `[`db-patch`](https://weasel.jasperfx.net/cli/db-patch.html) or [`db-dump`](https://weasel.jasperfx.net/cli/db-dump.html), applied by something other than the application | When the schema should be reviewed and checked in |

`dotnet run -- `[`db-assert`](https://weasel.jasperfx.net/cli/db-assert.html) checks without changing anything, whatever the approach.
Fisher's own behaviour under each is below; the Weasel documentation has the engine-level view of the
[migration model and `AutoCreate` policy](https://weasel.jasperfx.net/core/schema-migrations.html).

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

## From the command line

With `RunJasperFxCommands(args)` as the last line of `Program.cs`, a host that calls `AddFisher(...)` has
Weasel's [command line tools](https://weasel.jasperfx.net/cli/) and JasperFx's `resources` command:

```bash
dotnet run -- db-list              # the databases this host manages; one per tenant under database-per-tenant
dotnet run -- db-apply             # apply every outstanding migration
dotnet run -- db-assert            # non-zero exit if any database differs from the configuration
dotnet run -- db-patch patch.sql   # the delta against the live database, plus a rollback file
dotnet run -- db-dump schema.sql   # the whole schema as DDL

dotnet run -- resources list       # every stateful resource in the application
dotnet run -- resources check      # verify each one
dotnet run -- resources setup      # create or migrate each one
```

| | |
| :--- | :--- |
| [`db-apply`](https://weasel.jasperfx.net/cli/db-apply.html), [`db-assert`](https://weasel.jasperfx.net/cli/db-assert.html), [`db-patch`](https://weasel.jasperfx.net/cli/db-patch.html), [`db-dump`](https://weasel.jasperfx.net/cli/db-dump.html), [`db-list`](https://weasel.jasperfx.net/cli/db-list.html) | Weasel's, and act on Weasel databases only. Fisher registers every database it manages with Weasel |
| [`resources`](https://shared-libs.jasperfx.net/cli/resources.html) | JasperFx's stateful resource model. The Fisher store is one resource; on a Wolverine host its message storage and any broker queues are others, and one `resources setup` provisions all of them |

::: tip The command line itself
`RunJasperFxCommands` and the rest of the command line come from JasperFx; its
[command line documentation](https://shared-libs.jasperfx.net/cli/) covers setup,
[writing your own commands](https://shared-libs.jasperfx.net/cli/writing-commands),
[arguments and flags](https://shared-libs.jasperfx.net/cli/arguments-flags),
[environment checks](https://shared-libs.jasperfx.net/cli/environment-checks) and `describe`. Since JasperFx 2.101.0
the commands also run from a Native AOT binary: see [Native AOT](https://shared-libs.jasperfx.net/cli/#native-aot)
there and [the command line](/configuration/native-aot#the-command-line) on Fisher's Native AOT page.
:::

`db-apply` and `resources setup` are an *explicit* instruction to migrate, so both treat `AutoCreate.None` as
`CreateOrUpdate`. `db-assert` and `resources check` never change anything.

::: tip
On SQLite the "database" is a file, so `db-apply` against a path that does not exist yet creates the file
and the schema in one step. Under database-per-tenant every known tenant file is walked, one at a time.
:::

## AutoCreate

```cs
opts.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
```

| Value | Behaviour |
| :--- | :--- |
| `CreateOrUpdate` | Create what is missing, and make what exists match the configuration, **including dropping** columns and indexes it no longer declares. The default. |
| `CreateOnly` | Create what is missing. A migration that would change an existing object fails instead. |
| `All` | Drop and recreate. |
| `None` | Never change the schema implicitly. An explicit apply still migrates. |

::: warning
**`CreateOrUpdate` drops.** Removing a `Duplicate(...)`, `Index(...)` or metadata column from the
configuration removes it from the database on the next migration. Use `CreateOnly` where a deployment
must never lose a schema object. It refuses the migration rather than applying it.
:::

::: tip
**`None` stops implicit migrations, not explicit ones.** The first-use creation of a document table or
of the event tables checks and throws under `None`. But `ApplyAllConfiguredChangesToDatabaseAsync()`,
[`db-apply`](https://weasel.jasperfx.net/cli/db-apply.html) and [`resources setup`](https://shared-libs.jasperfx.net/cli/resources.html)
treat `None` as `CreateOrUpdate`, because calling them is the instruction to apply.

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
