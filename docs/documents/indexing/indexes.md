# Declared Indexes

```cs
opts.Schema.For<Catch>()
    .Index(x => x.Species)
    .UniqueIndex(x => x.Tag);

// Composite
opts.Schema.For<Catch>()
    .Index([x => (object?)x.Species, x => (object?)x.Landed]);
```

```cs
public class Catch
{
    [Index] public string Species { get; set; } = "";
    [UniqueIndex] public string Tag { get; set; } = "";

    // One composite index over both
    [Index(IndexName = "idx_catch_trip")] public Guid TripId { get; set; }
    [Index(IndexName = "idx_catch_trip")] public int Position { get; set; }
}
```

## An expression index, not a column

**This is the whole divergence, and it makes the feature cheaper on Fisher than on either sibling.**
Marten needs a computed index and Polecat a `JSON_VALUE` index, both of which materialise something
first. SQLite has had indexed expressions since 3.9 (restricted to deterministic expressions, which
`json_extract` is), so the member is indexed **where it lives** and the table's shape does not change.

That is what makes `Duplicate` and `Index` two different things:

- [`Duplicate`](/documents/indexing/duplicated-fields) materialises a `VIRTUAL` generated column
  **and** indexes it — for when the member should also be a column something else can name.
- `Index` indexes the expression only. No column, no affinity to declare, nothing added to the table.

## The indexed expression is the member's locator

From the same factory a query goes through, and that is load-bearing rather than tidy:

::: warning
SQLite's planner uses an expression index only when the query's expression **matches the index's**. An
index built from a hand-written `json_extract` is created without error, never used, and reports
nothing.
:::

A timestamp is the case that proves it. Its locator is the
[`strftime` wrapper](/documents/querying/linq/operators#timestamps), so a bare `json_extract` index
would not serve the range predicates a timestamp index exists for.

The same reading is what makes **indexing a member that is also duplicated index the generated
column** — a duplicated member's locator *is* the column name. Not special-cased.

## Unique indexes and missing members

::: warning
A `UNIQUE` index does **not** constrain documents missing the member. `json_extract` yields SQL NULL
for an absent key, and SQLite treats NULLs in a unique index as distinct.
:::

Same as both siblings, and worth stating because it is the kind of thing a reader assumes the
opposite of.

## Fisher's `[Index]` is narrower than Polecat's, on purpose

Polecat's carries `SortOrder`, `Casing` and `SqlType`. All three describe a *computed column*, which
is what a Polecat index is built over. A Fisher index has:

- no column to type,
- no casing to apply — SQLite's default collation is case-sensitive and the
  [LINQ string operators](/documents/querying/linq/strings) are ordinal to match,
- no direction worth naming.

Carrying them would be three knobs that silently did nothing.

## Naming

`idx_<table>_<members>`, mirroring Weasel's formula. Members sharing an `IndexName` become one
composite index in declaration order.

## Checking that it is used

```cs
var plan = await session.AdvancedSql.QueryAsync<string>(
    "explain query plan select data from fi_doc_catch where json_extract(data, '$.species') = ?",
    token, "Brook Trout");
```

You are looking for `SEARCH … USING INDEX` rather than `SCAN`.
