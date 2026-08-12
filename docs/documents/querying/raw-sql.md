# Raw SQL

Two halves: `QueueSqlCommand` writes inside the unit of work, and `session.AdvancedSql` reads with
typed results.

**Both are worth more here than on either sibling**, for a structural reason. An application using
Fisher keeps its own tables in the *same file*, and SQLite permits one writer per file — so without
`QueueSqlCommand`, your statements and Fisher's are two transactions on one file, and "my rows and
Fisher's, or neither" means taking the write lock twice and contending with yourself. On PostgreSQL
or SQL Server the same method is a convenience.

## Writing: QueueSqlCommand

```cs
session.QueueSqlCommand(
    "insert into audit_log (id, message, at) values (?, ?, ?)",
    Guid.NewGuid(), "user updated", DateTimeOffset.UtcNow);

session.Store(user);

await session.SaveChangesAsync();   // your row and Fisher's, one transaction
```

The statement is enrolled in the unit of work and runs in the same transaction as everything else.

### Placeholders

`?` is the default. If your SQL needs a literal `?`, use the alternate-placeholder overload:

```cs
session.QueueSqlCommand('^', "update t set expr = '? maybe' where id = ^", id);
```

::: warning
A bare `?` that Fisher does not treat as a placeholder is still **SQLite's own anonymous parameter
marker**, so it fails with "must add values for the following parameters" rather than passing through
as text. Only a `?` inside a string literal is safe.
:::

## Reading: AdvancedSql

```cs
// Scalars and tuples
var counts = await session.AdvancedSql.QueryAsync<string, int>(
    "select species, count(*) from fi_doc_catch group by species", token);

// Documents
var users = await session.AdvancedSql.QueryAsync<User>(
    $"select {string.Join(", ", session.AdvancedSql.SelectFieldsFor<User>())} from fi_doc_user where …",
    token);

// Streaming
await foreach (var row in session.AdvancedSql.StreamAsync<string>("select name from …", token))
{
    …
}
```

Up to three result types per row are supported.

::: tip
`SelectFieldsFor<T>()` exists so you do not have to guess which columns a document needs. A document
can only be the **first** result type of a query, because the storage selector reads from fixed
positions starting at column 0 — anywhere else throws naming the restriction, rather than producing
the cast error a misaligned read would.
:::

### A document comes back as its real sub-class

`AdvancedSql` materializes a document through its storage's own selector, not by deserializing the
`data` column. That matters for a [hierarchy](/documents/hierarchies): the selectors resolve
`doc_type` to the real sub-class, where hand-deserializing to the declared type would return the base
for every row, quietly missing whatever the sub-class added.

Polecat's reader hand-deserializes at an offset; on Fisher that would be silently wrong.

### Scalars are read with GetFieldValue

Not `GetValue` + `Convert.ChangeType`. Polecat can use the latter because SQL Server hands back the
CLR type; Fisher stores a Guid as text, and `Convert.ChangeType(string, typeof(Guid))` throws
outright — `Guid` is not `IConvertible`.

This is the one Fisher read path that leans on provider coercion **by choice**. The row readers
convert explicitly for round-trip symmetry; raw SQL has nothing to protect, since you name arbitrary
columns including ones Fisher never wrote.

### StreamAsync runs outside the resilience pipeline

::: warning
A retried `SQLITE_BUSY` re-executes the whole delegate, so a live reader yielded to you would resume
against a disposed connection. `StreamAsync` therefore surfaces a busy database to the caller;
`QueryAsync` stays inside the pipeline. Materialising first would not be streaming, so this is the
trade.
:::

## Parameter binding

**This is the one path where your value reaches a parameter with no conversion in between** — every
other write path converts explicitly. Three CLR types bind to something Fisher never wrote, so Fisher
converts them for you. Each was verified against Microsoft.Data.Sqlite, and each conversion was
verified load-bearing by removing it:

| Type | Raw binding | What Fisher stores |
| :--- | :--- | :--- |
| `Guid` | `D3F1…` UPPERCASE | `d3f1…` lowercase canonical |
| `DateTimeOffset` | `2026-08-08 18:45:30.123+00:00` | `2026-08-08T18:45:30.123Z` |
| `decimal` | text | REAL |

::: tip
The `DateTimeOffset` case is worth knowing precisely, because a one-sided range test does not catch
it. At index 10 the stored form has `T` (0x54) and the raw binding a space (0x20), so a stored value
sorts **after any same-date raw bound** whatever time it names — a `>` comparison against an earlier
bound passes with the conversion removed.
:::

::: tip
The `decimal` case is the likely one rather than the exotic one. Column affinity rescues a comparison
against a *declared* column, and there is no affinity inside `json_extract` — which is how every
undeclared document member is read.
:::

Everything else is already right and is passed through: `bool` → INTEGER 1/0, an enum → its integer
value, and the rest are what Fisher stores.

::: tip
A declared `SqliteType` does **not** coerce the value — the provider binds by the CLR type of `Value`
regardless — so Weasel stamping every placeholder TEXT is harmless rather than a fourth problem.
:::

## Raw SQL bypasses the identity map

`AdvancedSql` resolves the query-only storage flavor directly, so a
[tracking session](/documents/sessions#tracking-modes) does not map what it returns. That is Marten's
behaviour too, and for a real reason: a raw query names its own columns and may select no identity at
all.

## Raw SQL does not carry the implicit filters

You are writing the SQL, so the tenant, soft-delete and hierarchy terms are yours to add. If you want
them applied for you, use [`Query<T>()`](/documents/querying/linq/).
