# Indexing Documents

A document member lives inside `data`, so a predicate against it is `json_extract` per row — a scan.
Fisher has three ways to change that, and they are genuinely different things rather than near
duplicates.

| | Adds a column? | Adds an index? | Use when |
| :--- | :--- | :--- | :--- |
| [`Duplicate(...)`](/documents/indexing/duplicated-fields) | yes, a **generated** one | yes | The member should also be a column something else can name |
| [`Index(...)`](/documents/indexing/indexes) | **no** | yes | You only want the member to be fast |
| [`ForeignKey<T>(...)`](/documents/indexing/foreign-keys) | yes, implicitly | yes | You want referential integrity |

**All three are cheaper on Fisher than on either sibling**, and for the same underlying reason:
SQLite has indexed expressions (since 3.9) and `VIRTUAL` generated columns, so nothing has to be
*written* to be indexed.

<!-- snippet: sample_documents_schema_dsl -->
<a id='snippet-sample_documents_schema_dsl'></a>
```cs
opts.Schema.For<Catch>()
    .DocumentAlias("catches")
    .SoftDeleted()
    .UseOptimisticConcurrency()
    .MultiTenanted()
    .Duplicate(x => x.Species)
    .Index(x => x.Landed)
    .UniqueIndex(x => x.Tag)
    .ForeignKey<Angler>(x => x.AnglerId);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/document_samples.cs#L134-L144' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_documents_schema_dsl' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or declaratively:

<!-- snippet: sample_documents_indexing_attributes -->
<a id='snippet-sample_documents_indexing_attributes'></a>
```cs
public class Catch
{
    public Guid Id { get; set; }

    [DuplicateField] public string Species { get; set; } = "";
    [Index] public DateTimeOffset Landed { get; set; }
    [UniqueIndex] public string Tag { get; set; } = "";

    public Guid AnglerId { get; set; }
    public Guid WaterId { get; set; }
    public decimal Weight { get; set; }
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/document_samples.cs#L51-L64' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_documents_indexing_attributes' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
Members sharing an `IndexName` become **one composite index**, in declaration order. That is the only
reason a per-member attribute carries a name at all.
:::

## The one rule that makes indexes actually work

SQLite's planner uses an expression index only when the query's expression **matches the index's**.
Fisher builds both from the same member locator, which is what makes an index usable at all.

A timestamp is the case that proves it: its locator is the
[`strftime` wrapper](/documents/querying/linq/operators#timestamps), so an index built from a bare
`json_extract` would be created without error, never used, and report nothing.

The same rule means **indexing a member that is also duplicated indexes the generated column**, since
a duplicated member's locator *is* the column name. That is not special-cased; it falls out of
reading the locator.

## Index naming

`idx_<table>_<members>`, mirroring Weasel's own formula — and deliberately indistinguishable in
`sqlite_master` from a duplicated field's index. Which mechanism created one is Fisher's business,
not the reader's.

## Migrations

Indexes go through Weasel's migration like every other schema object, so `AutoCreate.None` is honoured
for free and the table is not created lazily on first write.
