# Declared Indexes

<!-- snippet: sample_documents_duplicate_and_index -->
<a id='snippet-sample_documents_duplicate_and_index'></a>
```cs
opts.Schema.For<Catch>()
    .Duplicate(x => x.Species)      // generated column + index
    .Index(x => x.Landed)           // expression index only — no column at all
    .UniqueIndex(x => x.Tag);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/document_samples.cs#L166-L171' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_documents_duplicate_and_index' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A composite index over several members, in the order given:

<!-- snippet: sample_documents_composite_index -->
<a id='snippet-sample_documents_composite_index'></a>
```cs
opts.Schema.For<Catch>()
    .Index([x => (object?)x.Species, x => (object?)x.Landed]);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/document_samples.cs#L173-L176' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_documents_composite_index' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

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

## Partial indexes

An index over only the rows a predicate admits:

```cs
opts.Schema.For<Catch>()
    .Index(x => x.Weight, name: "idx_catch_pike_weight", predicate: x => x.Species == "Pike");

opts.Schema.For<Catch>()
    .UniqueIndex(x => x.Tag, predicate: x => !x.Released);
```

Worth more over a document store than over a relational one: the index holds only the subset, so a
hot slice of a large table is indexed at the size of the slice.

**The predicate is an ordinary expression, translated by the same parser and member factory a query
goes through** — not a SQL string. That is the point rather than a convenience, and it is the same
reason the indexed expression is the member's locator: SQLite reaches a partial index only when the
query's `WHERE` implies the index's, over the terms as written.

::: warning
**The predicate's values are written into the DDL as literals**, because `CREATE INDEX … WHERE …` is
a schema definition and carries no parameters. They are escaped, and a value Fisher cannot render
unambiguously is refused by name rather than reached for with `ToString()`.

That is safe because of *where* they come from — constants in a configuration lambda at startup, the
same trust class as a `[JsonPropertyName]`. **Do not build an index predicate out of a request
value.** An index is declared once, when the store is configured.
:::

`SoftDeletedWithIndex()` is the shape this exists for, pre-built:

```cs
opts.Schema.For<Catch>().SoftDeletedWithIndex();   // index over is_deleted where is_deleted = 0
```

Every ordinary read carries `is_deleted = 0`, so an index holding only the live rows is the size of
the live set rather than of the table's whole history.

## Indexing the metadata columns

```cs
opts.Schema.For<Catch>().IndexLastModified().IndexCreatedAt();
opts.Schema.For<Order>().MultiTenanted().IndexTenantId();
```

Plain column indexes — `last_modified`, `created_at` and `tenant_id` are real columns, so there is no
expression to build.

- `IndexCreatedAt()` **enables** `created_at` as well as indexing it. The column is
  [opt-in](/documents/metadata), and an index over a column that does not exist is not a weaker
  version of this — it fails the migration.
- `IndexTenantId()` is refused for a type that is not `MultiTenanted()`, because there is no column to
  index. It is also worth less here than on either sibling and offered for parity: `tenant_id` already
  *leads* the conjoined primary key, so the implicit tenant filter is served by that index already.

## Leaving an index alone

```cs
opts.Schema.For<Catch>().IgnoreIndex("idx_added_by_hand");
```

For an index created outside Fisher. Without it the schema comparison sees an index the configuration
does not declare, so `db-assert` fails and `db-apply` drops it.

Ignoring a name Fisher itself declares is refused — that is a collision, not an exemption.

## Naming

`idx_<table>_<members>`, mirroring Weasel's formula. Members sharing an `IndexName` become one
composite index in declaration order.

## Checking that it is used

```cs
var plan = await session.Query<Catch>().Where(x => x.Species == "Brook Trout").ExplainAsync();

plan.UsesIndex;   // did the planner reach it?
plan.Steps;       // SQLite's own rows — you are looking for SEARCH … USING INDEX rather than SCAN
```

See [`ExplainAsync`](/documents/querying/linq/operators#explaining-a-query).
