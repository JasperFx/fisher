# Tearing Down Document Storage

```cs
var clean = store.Advanced.Clean;

await clean.DeleteAllDocumentsAsync();     // every fi_doc_* row
await clean.CleanAsync<User>();            // one type
await clean.DeleteAllEventDataAsync();     // events, streams, progression, tags, keys, dead letters
await clean.CompletelyRemoveAllAsync();    // drop every table this store owns

await store.Advanced.ResetAllDataAsync();  // documents + events, in one call
```

::: danger
These are destructive and unguarded. They exist for test fixtures and for development. Do not wire one
to an endpoint.
:::

## Scoping is by table prefix

There is no schema to scope to, so `DatabaseSchemaName`'s prefix **is** the isolation boundary between
two logical stores in one file — cleaning one does not touch the other.

::: warning
Table matching is done in C#, not with `LIKE`. `_` is a single-character wildcard in SQL's `LIKE` and
every Fisher prefix contains one, so `like 'fi_%'` would happily match a table called `fixtures`.
Names come back from `sqlite_master` and are filtered with `StartsWith`.
:::

## CleanAsync matches existing tables

Rather than issuing a blind `delete from`. A document table is created on demand at first write, and
SQLite resolves a table name when it **prepares** a statement — so cleaning a type that has never been
written would fail before any guard in the SQL could run.

::: tip
It is a **real delete even for a soft-deleted type**. Flagging rows would leave a "cleaned" table that
still answers `MaybeDeleted()` and still refuses an insert on a duplicate id.
:::

## DeleteAllEventDataAsync deletes in a fixed order

**Tag tables first, dead letters last.** `fi_event_tag_*` rows have a real foreign key to
`fi_events(seq_id)` and Weasel's default profile turns enforcement on, so clearing events first fails
with `FOREIGN KEY constraint failed`.

It also clears the [natural key](/events/natural-keys) lookups. Leaving them behind is not cosmetic:
the duplicate guard would then fire on data that no longer exists.

`CompletelyRemoveAllAsync` needs no ordering — SQLite does not enforce a foreign key against a dropped
table.

## DeleteAllDocumentsAsync orders by foreign key

Referencing tables first, and the order comes from `pragma_foreign_key_list` rather than from the
store's configuration — so it is the *database's* account of what references what. A table left behind
by an earlier configuration is still enforced even though the store no longer knows about it.

## CompletelyRemoveAllAsync forgets the table cache

Afterwards, Fisher forgets its "this document table already exists" cache. Without that, the cache
would still claim tables that were just dropped, and the next `Store` would skip its migration and
write to nothing.

::: tip
It filters by the `fi_` prefix, so it leaves **EF Core's tables and your own alone**. Fisher owning the
file does not make it Fisher's to clear.
:::

## Multi-tenancy

The whole cleaner surface loops **every** database. Cleaning only the default would leave every other
tenant's data behind while reporting success — and the caller most likely to hit that is a test
fixture.

## Rebuild teardown is a different thing

A [projection rebuild](/events/projections/async-daemon#rebuilds) tears down only what that projection
published, in one transaction with its progression rows. It uses a real delete rather than a soft one,
or the replay would write onto rows it cannot see.

## Simply deleting the file

For a test, the bluntest option is often the best one — a Fisher store is a file:

```cs
await store.DisposeAsync();   // releases this store's pooled connections
File.Delete(path);
```

::: warning
Disposing first matters. Microsoft.Data.Sqlite pools a connection per connection string, and its
`-wal` and `-shm` sidecars are only removed when the last connection closes.

And **never** call `SqliteConnection.ClearAllPools()` to force it — that disposes every pooled
connection in the process, so one test's cleanup takes out another's.
:::
