# How Documents are Stored

## Table naming

SQLite has no schemas, so the logical schema name folds into the table **prefix**:

| `DatabaseSchemaName` | `Order` documents | Events |
| :--- | :--- | :--- |
| `main` | `fi_doc_order` | `fi_events` |
| `reporting` | `reporting_fi_doc_order` | `reporting_fi_events` |

Every `DbObjectName` uses the SQLite schema `main`, so nothing renders as qualified SQL. That is what
gives two logical stores real isolation inside one file with no `ATTACH` lifecycle to re-establish on
every pooled connection.

The `fi_` prefix marks a table **Fisher owns the shape of**. A
[flat-table projection](/events/projections/flat) does not get it, because a flat table's shape is the
projection's.

## The document table

```sql
create table fi_doc_order (
    id            TEXT not null primary key,
    data          TEXT not null,
    doc_type      TEXT,
    dotnet_type   TEXT,
    last_modified TEXT not null default (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
```

::: warning
A column `DEFAULT` that is an **expression must be parenthesized**. `DEFAULT strftime(...)` is a
CREATE TABLE syntax error in SQLite.
:::

Optional columns arrive with the feature that asks for them — see
[Database Storage](/documents/storage).

## Type mapping

| .NET | SQLite | Notes |
| :--- | :--- | :--- |
| `Guid` | TEXT | Lowercase canonical, case-sensitive collation |
| `DateTimeOffset` | TEXT | Fixed-width UTC, so a string sort is a time sort |
| `bool` | INTEGER | 0/1 |
| `int`, `long` | INTEGER | |
| `decimal` | REAL | `json_extract` returns REAL for any JSON number |
| `string` | TEXT | |

::: warning
**The declared type is a column's comparison affinity**, so declaring a numeric
[duplicated field](/documents/indexing/duplicated-fields) as TEXT makes it sort as text. A column whose
affinity disagrees with its own generated expression is the one shape that cannot be right.
:::

## Generated columns

A duplicated field is a `VIRTUAL` generated column over `data` — computed on read, so it cannot drift,
needs no backfill, and costs index space rather than row space.

::: warning
`pragma_table_info` does **not** list generated columns; only `pragma_table_xinfo` does. Fisher
overrides Weasel's delta-detection query for exactly this, tracked as
[weasel#426](https://github.com/JasperFx/weasel/issues/426).
:::

## Indexes

An [`Index(...)`](/documents/indexing/indexes) is a SQLite **expression index** — no column is added at
all. `idx_<table>_<members>`, mirroring Weasel's formula, and deliberately indistinguishable in
`sqlite_master` from a duplicated field's index.

## Foreign keys

[Real and enforced](/documents/indexing/foreign-keys), over the generated child column. Enforcement is
per-connection and on for every connection Fisher opens.

::: warning
The `REFERENCES` clause **cannot be schema-qualified** in SQLite. Since Fisher's logical schema is
already part of the table name, the rendered name is the whole name — so two logical stores in one file
each reference their own table.
:::

## Event tables

See [Event Storage](/events/storage) — including why `fi_events.seq_id` must be `AUTOINCREMENT`.

## Inspecting

```cs
var ddl = store.Advanced.ToDatabaseScript();
```

Or point any SQLite tool at the file.
