# Database Storage

## Table naming

SQLite has no schemas, so Fisher folds the logical schema name into the table **prefix**:

| `DatabaseSchemaName` | Events | Streams | `Order` documents |
| :--- | :--- | :--- | :--- |
| `main` (default) | `fi_events` | `fi_streams` | `fi_doc_order` |
| `reporting` | `reporting_fi_events` | `reporting_fi_streams` | `reporting_fi_doc_order` |

Every `DbObjectName` uses the SQLite schema `main`, so nothing renders as qualified SQL. That is what
gives logical-store and test isolation inside one database file with no `ATTACH` lifecycle to
re-establish on every pooled connection.

The `fi_` prefix marks a table **Fisher owns the shape of**. A
[flat-table projection](/events/projections/flat) does not get it, because a flat table's shape is
the projection's.

## Document table shape

```sql
create table fi_doc_user (
    id            TEXT    not null primary key,
    data          TEXT    not null,
    doc_type      TEXT,
    dotnet_type   TEXT,
    last_modified TEXT    not null default (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
```

Optional columns arrive with the feature that needs them:

| Column | Added by |
| :--- | :--- |
| `tenant_id` | `MultiTenanted()` — and it joins the primary key |
| `guid_version` | `UseOptimisticConcurrency()` |
| `revision` | `UseNumericRevisions()` |
| `is_deleted`, `deleted_at` | `SoftDeleted()` |
| `created_at`, `correlation_id`, `causation_id`, `last_modified_by`, `headers` | [metadata opt-ins](/documents/metadata) |
| one generated column per member | [`Duplicate(...)`](/documents/indexing/duplicated-fields) |

## Type mapping

| .NET | SQLite | Notes |
| :--- | :--- | :--- |
| `Guid` | TEXT | Lowercase canonical. Case-sensitive collation, so casing matters. |
| `DateTimeOffset` | TEXT | Fixed-width UTC ISO-8601, so a string sort is a time sort. |
| `bool` | INTEGER | 0/1. |
| `int` / `long` | INTEGER | |
| `decimal` | REAL | `json_extract` hands back REAL for any JSON number. |
| `string` | TEXT | |
| document body | TEXT | Exactly what System.Text.Json wrote. |

## The write statements

Four statements are generated per document type, and their **column order and `?` order are one
contract** because the shared storage operations bind by position:

| Statement | Order |
| :--- | :--- |
| upsert / insert / overwrite | `[tenant,] id, data, client-side binders`, then the concurrency guard |
| update | `data, client-side binders, id, [tenant]`, then the guard |

The id moves from the front to the back in an update, because there it is a `WHERE` term rather than
a value.

The `DO UPDATE SET` clause assigns from `excluded.*` for every column rather than repeating each
binder's expression, so the update branch cannot drift from the insert branch. That is also why
[storing a soft-deleted document undeletes it](/documents/deletes#undeleting) with nothing arranged,
and why `created_at` is filled by a column DEFAULT rather than by a write binder — a `created_at` in
the write list would move forward on every save.

## Generated columns

A [duplicated field](/documents/indexing/duplicated-fields) is a SQLite `VIRTUAL` generated column
over `data`. That is the single largest divergence from both siblings, whose duplicated columns are
*written* on every upsert:

- It **cannot drift**, because nothing writes it — and adding one to a table that already has rows
  needs no backfill.
- It costs **index space, not row space**: `VIRTUAL` computes on read, and only the index
  materialises.
- The write path is untouched — no extra binder, no shift in the positional `?` contract.

::: warning
`pragma_table_info` does **not** list generated columns; only `pragma_table_xinfo` does. Fisher
overrides Weasel's delta-detection query for exactly this, tracked as
[weasel#426](https://github.com/JasperFx/weasel/issues/426). Without it, every duplicated column reads
as missing and the migration emits `ALTER TABLE … ADD COLUMN` for it every time.
:::

## Event tables

See [Event Storage](/events/storage).

## Inspecting the schema

```cs
var ddl = store.Advanced.ToDatabaseScript();
await store.Advanced.WriteCreationScriptToFileAsync("schema.sql");
```

See [Exporting Schema Definition](/schema/exporting).
