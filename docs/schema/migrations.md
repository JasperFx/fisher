# Schema Migrations

Weasel compares the configured schema against the database and applies the difference.

```cs
await store.ApplyAllConfiguredChangesToDatabaseAsync();
```

Applying the same configuration twice is a no-op.

## What SQLite can and cannot alter

This is the part that differs from both siblings, because SQLite's `ALTER TABLE` is narrow.

| Change | SQLite |
| :--- | :--- |
| Add a column | `ALTER TABLE … ADD COLUMN` |
| Rename a column | `ALTER TABLE … RENAME COLUMN` (3.25+) |
| Drop a column | `ALTER TABLE … DROP COLUMN` (3.35+), with restrictions |
| Add or drop an index | fine |
| **Add a constraint** | **not possible** — the table must be recreated |
| **Change a column's type** | **not possible** — the table must be recreated |

::: warning
Adding a [foreign key](/documents/indexing/foreign-keys) to a type whose table already exists means
**recreating the table**. Weasel reports that rather than attempting it, so you can decide whether to
migrate the data yourself or start the table again.
:::

## Adding a duplicated field is free

A [duplicated field](/documents/indexing/duplicated-fields) is a `VIRTUAL` generated column computed
from `data`, so adding one to a table that already has rows makes **every existing row correct at
once**. There is no backfill, because nothing writes the column.

Both siblings, whose duplicated columns are written, need one.

::: warning
Generated columns are also where Weasel's delta detection needed a Fisher override:
`pragma_table_info` does not list them, so without it every duplicated column reads as missing and the
migration emits `ALTER TABLE … ADD COLUMN` for it *every time* — and the second run fails with
`duplicate column name`. Tracked as [weasel#426](https://github.com/JasperFx/weasel/issues/426).
:::

## Adding a metadata column

Enabling an [opt-in metadata column](/documents/metadata) is an added column, so it migrates cleanly.

::: warning
Turning an enabled one back **off throws**. A column is created by the migration, and dropping one that
may hold data is a migration rather than a configuration flag.
:::

## Changing tenancy or event options

::: danger
`Events.TenancyStyle`, `MultiTenanted()` and the four `Events.Enable*` flags are **schema decisions**.
They change columns and, for tenancy, the **primary key** — so set them before the tables are created.

Changing tenancy on a store that already holds data is a data migration, not a configuration change.
:::

## Production migrations

For a controlled deployment, generate the script rather than migrating at startup:

```cs
opts.AutoCreateSchemaObjects = AutoCreate.None;
```

```cs
await store.Advanced.WriteCreationScriptToFileAsync("schema.sql");
```

See [Exporting Schema Definition](/schema/exporting).

::: tip
For an embedded store this matters less than it does on a server — the database ships with the
application. Migrating at startup is usually right; `AutoCreate.None` is for when you want the schema
to be somebody else's decision.
:::

## Multi-tenant migrations

Under [database-per-tenant](/configuration/multitenancy#database-per-tenant), migration is per database
and runs **sequentially** — each migration takes its own file's write lock, so parallelism wins
nothing on the DDL and holds N connections against a pool ceiling that sizes one file.

A failure part way leaves mixed versions whatever it throws, so `TenantMigrationException` reports
*which* databases are current.

A tenant that appears at runtime is migrated when a connection is first opened to its file, and the
result is **not cached until it succeeds** — a transient failure remembered as done would leave the
tenant permanently unusable with nothing to say why.
