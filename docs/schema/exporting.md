# Exporting Schema Definition

```cs
var ddl = store.Advanced.ToDatabaseScript();

await store.Advanced.WriteCreationScriptToFileAsync("schema.sql");
```

The script creates every table, index and constraint the store's configuration describes.

## Use it for a controlled deployment

```cs
opts.AutoCreateSchemaObjects = AutoCreate.None;
```

Then apply `schema.sql` as part of your release, and Fisher never touches the schema at runtime.

::: tip
For an embedded store this matters less than on a server, because the database ships with the
application — migrating at startup is usually the right answer. Reach for this when the schema should
be reviewed, checked in, or applied by something other than the application.
:::

## Generating the script in a build step

```cs
await using var store = DocumentStore.For(opts =>
{
    opts.Connection("Data Source=:memory:");
    // …your real configuration…
});

await store.Advanced.WriteCreationScriptToFileAsync(args[0]);
```

An in-memory connection string is enough — the script describes the *configuration*, not an existing
database.

## What it does not do

::: warning
This generates a **creation** script, not a **migration** script. It describes the schema as
configured, not the difference between two versions. For an existing database, apply the configuration
and let Weasel compute the delta.
:::

::: warning
And note what SQLite cannot alter: adding a constraint or changing a column's type means recreating the
table. See [Schema Migrations](/schema/migrations#what-sqlite-can-and-cannot-alter).
:::

## Verifying it

The assertion worth making is that the script produces the same schema the migration does — apply it
to a fresh file and compare `sqlite_master`:

```cs
await using var fresh = new SqliteConnection("Data Source=fresh.db");
await fresh.OpenAsync();
await new SqliteCommand(ddl, fresh).ExecuteNonQueryAsync();
```

That the string contains a table name is not an assertion worth having.

## Where it comes from

`ToDatabaseScript()` is **Weasel's**, inherited rather than reimplemented. Polecat writes its own only
because it needs `GO` separators, which SQLite has no equivalent of.
