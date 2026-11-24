# Querying Documents with LINQ

```cs
var users = await session.Query<User>()
    .Where(x => x.Internal && x.LastName == "Doe")
    .OrderByDescending(x => x.LastLogin)
    .Take(20)
    .ToListAsync();
```

`Query<T>()` returns an ordinary `IQueryable<T>`, and Fisher's provider translates the operator chain
to SQL over `json_extract`. The terminals (`ToListAsync`, `CountAsync` and the rest) are extension
methods in the `Fisher.Linq` namespace.

::: warning
Every Fisher query runs asynchronously. The synchronous `ToList()`, `First()`, `Count()`, `foreach` and
the rest still compile, because they come from `System.Linq`, but they throw a `NotSupportedException`
that points at `ToListAsync()` and the other async terminals. Use `ToListAsync()`, `FirstAsync()`, `CountAsync()` and the
other `*Async()` terminals, or [`ToAsyncEnumerable()`](/documents/querying/linq/operators#streaming).
:::

::: warning
Anything Fisher cannot translate throws a `BadLinqExpressionException` **naming the operator**,
rather than falling back to client-side evaluation. That is deliberate: a silent fallback would read
every row of the table to answer a query that looked cheap.
:::

Fisher throws `JasperFx.BadLinqExpressionException`, the type Marten and Polecat throw too, so
store-agnostic code can catch the refusal without naming Fisher:

```csharp
try
{
    var results = await session.Query<User>().Where(predicate).ToListAsync();
}
catch (JasperFx.BadLinqExpressionException e)
{
    // "I cannot answer this correctly" -- categorically different from an empty result, and the
    // same catch works against Marten and Polecat.
}
```

## What Fisher supports {#what-fisher-supports}

The tables below list what Fisher translates to SQL. Each row links to an example. Fisher never runs
part of a query in memory to make up for a shape it cannot translate: it throws, and the
[last table](#not-supported) says which exception you get and what to do instead.

### Filtering with Where() {#supported-filtering}

| Shape | Example |
| :--- | :--- |
| `==`, `!=`, `>`, `>=`, `<`, `<=`, `&&`, `\|\|` and `!` on members and members of members | [Comparisons](/documents/querying/linq/operators#comparisons) |
| Comparing two members, `x.Price < x.Cost` | [Comparisons](/documents/querying/linq/operators#comparisons) |
| `+`, `-` and `%` inside a predicate, `x.Number % 2 == 0` | [Comparisons](/documents/querying/linq/operators#comparisons) |
| `== null`, `!= null`, `Nullable<T>.Value`, and the `Year`/`Month`/`Day`/`Hour`/`Minute`/`Second` of a date | [Nullable members and date parts](/documents/querying/linq/operators#nullable-members-and-date-parts) |
| A local collection, `ids.Contains(x.Id)`, and `IsOneOf()` / `In()` | [Collections](/documents/querying/linq/operators#collections) |
| Timestamps, compared as instants | [Timestamps](/documents/querying/linq/operators#timestamps) |
| Decimals, compared exactly; a decimal member plus or minus constants | [Decimals](/documents/querying/linq/operators#decimals) |
| Enums, under either `EnumStorage` | [Enums](/documents/querying/linq/operators#enums) |
| Registered strong-typed identifiers | [Strong-typed identifiers](/documents/querying/linq/operators#strong-typed-identifiers) |
| Raw SQL with `MatchesSql()` | [Raw SQL inside a Where](/documents/querying/linq/operators#raw-sql-inside-a-where) |
| `ModifiedSince()` / `ModifiedBefore()` | [Metadata operators](/documents/querying/linq/operators#metadata-operators) |
| `MaybeDeleted()`, `IsDeleted()`, `DeletedSince()`, `DeletedBefore()` | [Soft delete operators](/documents/querying/linq/operators#soft-delete-operators) |
| `AnyTenant()` and `TenantIsOneOf()` | [Tenancy operators](/documents/querying/linq/operators#tenancy-operators) |
| C# 15 union members on .NET 11: `x.Payment.Value is Card`, `((Card)x.Payment.Value).Last4` | [Union-typed members](/documents/querying/linq/operators#union-typed-members) |

### Strings {#supported-strings}

| Shape | Example |
| :--- | :--- |
| `Contains()`, `StartsWith()` and `EndsWith()`, ordinal and case-sensitive | [Searching on String Fields](/documents/querying/linq/strings) |
| The same with `StringComparison.OrdinalIgnoreCase`, and `ToLower()` / `ToUpper()` | [Case-insensitive searching](/documents/querying/linq/strings#case-insensitive-searching) |
| `Length` | [Case-insensitive searching](/documents/querying/linq/strings#case-insensitive-searching) |
| `string.IsNullOrWhiteSpace()` | [Null handling](/documents/querying/linq/strings#null-handling) |

### Collections {#supported-collections}

| Shape | Example |
| :--- | :--- |
| `Any()`, `Any(predicate)`, `All(predicate)`, `Contains(value)`, `Count()`, `Count(predicate)` and `IsEmpty()` | [Collections](/documents/querying/linq/operators#collections) |
| A predicate on a scalar element, `x.Tags.Any(t => t.StartsWith("ur"))`, and one collection predicate inside another, `x.Stops.Any(s => s.Cargo.Contains("fuel"))` | [Collections](/documents/querying/linq/operators#collections) |
| An element by position: `x.Values[1]`, `x.Grid[1][0]`, `ElementAt(1)` | [Indexing into arrays and lists](/documents/querying/linq/operators#indexing-into-arrays-and-lists) |

### Projections with Select() {#supported-select}

| Shape | Example |
| :--- | :--- |
| A member, a member of a member, an object or a collection member | [Projections](#projections) |
| An anonymous type, a constructor or an object initializer built from members | [Projections](#projections) |
| Anything else in the lambda body, computed in .NET over the members it reads | [Projections](#projections) |
| `Distinct()` after a `Select()`, and `DistinctBy()` on the documents | [Distinct](#distinct) |
| `OrderBy(x => x)` after a single-value `Select()` | [Ordering after a projection](#ordering-after-a-projection) |

### SelectMany() {#supported-selectmany}

| Shape | Example |
| :--- | :--- |
| Flatten a collection of values or of child documents, with or without a result selector | [SelectMany over a collection](/documents/querying/linq/operators#selectmany-over-a-collection) |
| `Where()`, the `OrderBy()` family, `Take()`, `Skip()`, `Distinct()` and one `Select()` after it | [SelectMany over a collection](/documents/querying/linq/operators#selectmany-over-a-collection) |

### Ordering and paging {#supported-ordering-and-paging}

| Shape | Example |
| :--- | :--- |
| `OrderBy()`, `OrderByDescending()`, `ThenBy()` and `ThenByDescending()` | [Query operators](/documents/querying/linq/operators#query-operators) |
| A computed key: `-x.Number`, `x.Number % 3`, `x.Name.ToUpper()`, an element by position | [Ordering by a computed key](/documents/querying/linq/operators#ordering-by-a-computed-key) |
| `Take()` and `Skip()`, and `Where()` or `OrderBy()` after them, applied to the page | [Operators after Take and Skip](/documents/querying/linq/paging#operators-after-paging) |
| `ToPagedListAsync()` and `Stats()` | [Offset paging](/documents/querying/linq/paging#offset-paging) |
| Keyset paging with `ToCursorPageAsync()` | [Keyset (cursor) paging](/documents/querying/linq/paging#keyset-cursor-paging) |

### Results and aggregates {#supported-results}

| Shape | Example |
| :--- | :--- |
| `ToListAsync()`, the `First`/`Single`/`Last` families, `CountAsync()`, `LongCountAsync()` and `AnyAsync()` | [Async terminals](#async-terminals) |
| `SumAsync()`, `MinAsync()`, `MaxAsync()` and `AverageAsync()`; a `decimal` is summed exactly | [Aggregates over a query](/documents/querying/linq/grouping#aggregates-over-a-query) |
| `ToAsyncEnumerable()` | [Streaming](/documents/querying/linq/operators#streaming) |
| `ExplainAsync()` | [Explaining a query](/documents/querying/linq/operators#explaining-a-query) |
| The stored JSON, unparsed: `ToJsonArrayAsync()` and its siblings | [Querying for Raw JSON](/documents/querying/query-json) |

### Grouping and joins {#supported-grouping-and-joins}

| Shape | Example |
| :--- | :--- |
| `GroupBy()` followed by a `Select()` with `Key`, `Count()`, `Sum()`, `Min()`, `Max()` and `Average()` | [GroupBy](/documents/querying/linq/grouping#groupby) |
| `Where()` after `GroupBy()`, as `HAVING` | [HAVING](/documents/querying/linq/grouping#having) |
| `Count(predicate)` over a group | [Counting part of a group](/documents/querying/linq/grouping#counting-part-of-a-group) |
| `Join()`, in method and query syntax | [Inner join](/documents/querying/linq/joins#inner-join) |
| `LeftJoin()`, and `GroupJoin()` followed by `SelectMany()` | [Left join](/documents/querying/linq/joins#left-join) |
| `RightJoin()` | [Right join](/documents/querying/linq/joins#right-join) |
| Several joins in one query | [Chained joins](/documents/querying/linq/joins#chained-joins) |
| `CountAsync()`, the aggregates, `Last` and `ToPagedListAsync()` over a join | [What works over a join](/documents/querying/linq/joins#what-works-over-a-join) |

### Related documents and search {#supported-includes-and-search}

| Shape | Example |
| :--- | :--- |
| `Include()` a related document, into a callback, a list or a dictionary | [The parent holds the related document's id](/documents/querying/linq/includes#parent-holds-the-id) |
| `Include()` the documents that point back at the queried one | [The related documents point back](/documents/querying/linq/includes#related-documents-point-back) |
| `Include()` with a filter on the included documents | [Filters](/documents/querying/linq/includes#filters) |
| Full-text search: `Search()`, `PlainTextSearch()`, `PhraseSearch()`, `WebStyleSearch()`, `PrefixSearch()`, `NgramSearch()` | [The operators](/documents/querying/linq/full-text#the-operators) |
| `OrderByRelevance()` | [Relevance ordering](/documents/querying/linq/full-text#relevance-ordering) |
| Vector search, with a LINQ filter | [Vector Search](/documents/querying/vector-search) |
| Full-text and vector search fused | [Hybrid Search](/documents/querying/hybrid-search) |

### Not supported {#not-supported}

| Shape | What happens | Do this instead |
| :--- | :--- | :--- |
| Synchronous execution: `ToList()`, `ToArray()`, `foreach`, `First()`, `Count()`, `Any()`, `ElementAt()`, `Aggregate()` and anything else that runs the query | `NotSupportedException` pointing at the async terminals | `ToListAsync()` and the other `*Async()` terminals, or `ToAsyncEnumerable()` |
| Any other query operator, such as `Reverse()`, `Concat()`, `Union()`, `Zip()`, `TakeWhile()` or `SkipWhile()` | `BadLinqExpressionException` naming the operator | Load the rows with `ToListAsync()` and apply the operator in memory |
| A method in a `Where()` that Fisher has no translation for, such as `x.Name.Normalize()` | `BadLinqExpressionException` naming the method | [`MatchesSql()`](/documents/querying/linq/operators#raw-sql-inside-a-where) |
| `Distinct()` over whole documents | `BadLinqExpressionException` | `DistinctBy()`, or `Select()` first |
| `DistinctBy()` after a `Select()` | `BadLinqExpressionException` | `Distinct()` |
| A second `Select()` | `BadLinqExpressionException` | Combine them into one `Select()` |
| `SumAsync()` and the other aggregates after a `Select()` | `BadLinqExpressionException` | Pass the member to the aggregate: `SumAsync(x => x.Total)` |
| `LastAsync()` with no `OrderBy()` | `BadLinqExpressionException` | Order the query first |
| `OrderBy()` on a member of an anonymous type or another shaped `Select()` | `BadLinqExpressionException` | Order before the `Select()` |
| An ordering key with no exact SQL: `long` or `decimal` arithmetic, a narrowing cast, `? :`, `??`, a `Math` call, `Substring()` | `BadLinqExpressionException` naming the key | Order by a member, or sort in memory. See [the keys that translate](/documents/querying/linq/operators#ordering-by-a-computed-key) |
| `decimal` arithmetic other than a member plus or minus constants, such as `x.Price + x.Fee` | `BadLinqExpressionException` | Compute the bound in C# before the query. See [Decimals](/documents/querying/linq/operators#decimals) |
| `.Date`, `DayOfWeek`, or a `TimeSpan` member such as `TotalHours` | `BadLinqExpressionException` | Compare the whole value against a range. See [date parts](/documents/querying/linq/operators#nullable-members-and-date-parts) |
| A range comparison or an ordering over an enum stored as a string | `BadLinqExpressionException` naming `EnumStorage` | Equality still works; for ranges, store enums as integers (the default) |
| A negative array index, or one read from the document | `BadLinqExpressionException` | Index with a constant or a captured value |
| `GroupBy()` without a `Select()`, or its element- and result-selector overloads | `BadLinqExpressionException` | Follow `GroupBy()` with a `Select()` over the group |
| After a join: `Select()`, `GroupBy()`, `Distinct()`, `DistinctBy()`, keyset paging, a JSON read, or a key comparer | `BadLinqExpressionException` | Shape the row in the join's result selector. See [joins](/documents/querying/linq/joins#what-is-refused-by-name) |
| `ToCursorPageAsync()` without a terminal identity key, over a computed key, or after `Take()`/`Skip()` | `BadLinqExpressionException` | End the ordering with `ThenBy(x => x.Id)`. See [keyset paging](/documents/querying/linq/paging#keyset-cursor-paging) |
| `Include()` with `Select()`, `GroupBy()`, a join, or a terminal that returns no documents | `BadLinqExpressionException` | See [what is refused](/documents/querying/linq/includes#what-is-refused) |
| A full-text operator on a type with no full-text index | `BadLinqExpressionException` | Declare one with `FullTextIndex(...)`. See [full-text search](/documents/querying/linq/full-text#what-is-refused) |
| A soft-delete or tenancy operator on a type without that column | `BadLinqExpressionException` | Mark the type `SoftDeleted()` or `MultiTenanted()` |
| Union shapes the stored JSON cannot tell apart | `BadLinqExpressionException` | See [Union-typed members](/documents/querying/linq/operators#union-typed-members) |
| Compiled queries (`ICompiledQuery<T>`) | Not available | Write the query each time. Building the SQL is a few microseconds; see [Marten operators that are absent](/documents/querying/linq/operators#marten-operators-that-are-absent) |

## How a member becomes SQL

`json_extract(data, '$.lastName')`. That is it, for almost every member.

This is where SQLite is *easier* than either sibling. `json_extract` returns a JSON number as
INTEGER, a float as REAL, a string as TEXT and `true`/`false` as INTEGER 1/0 — where SQL Server's
`JSON_VALUE` always returns `nvarchar`. So there is no `CAST` anywhere in Fisher's LINQ layer, and
none of the type-mapping machinery Polecat needs has an analogue here.

The one member that needs wrapping is a timestamp — see
[Timestamps](/documents/querying/linq/operators#timestamps).

## Async terminals

```cs
await query.ToListAsync();
await query.FirstAsync();          await query.FirstOrDefaultAsync();
await query.SingleAsync();         await query.SingleOrDefaultAsync();
await query.LastAsync();           await query.LastOrDefaultAsync();
await query.CountAsync();          await query.AnyAsync();
await query.SumAsync(x => x.Total);
await query.MinAsync(x => x.Landed);
await query.MaxAsync(x => x.Landed);
await query.AverageAsync(x => x.Total);
await query.ToPagedListAsync(page, size);
await query.ToCursorPageAsync(size, cursor);
```

Most take a predicate overload as well — `CountAsync(x => x.Internal)` composes a `Where` and
dispatches, so there is nothing duplicated.

## What is here

| Page | |
| :--- | :--- |
| [Supported LINQ Operators](/documents/querying/linq/operators) | The whole translated surface, and what is refused |
| [Searching on String Fields](/documents/querying/linq/strings) | Why `LIKE` is not used |
| [Paging](/documents/querying/linq/paging) | Offset paging and keyset/cursor paging |
| [Grouping and Aggregates](/documents/querying/linq/grouping) | `GroupBy`, `HAVING`, `Sum`/`Min`/`Max`/`Average` |
| [Joins](/documents/querying/linq/joins) | `Join`, `LeftJoin`, `RightJoin` and `GroupJoin`, chained |
| [Full-Text Search](/documents/querying/linq/full-text) | FTS5 search operators and relevance ordering |
| [Including Related Documents](/documents/querying/linq/includes) | `Include()` |

## Projections

`Select` reads only the columns the projection names, rather than materialising the whole document:

```cs
var names = await session.Query<User>().Select(x => x.LastName).ToListAsync();

var summaries = await session.Query<Order>()
    .Select(x => new { x.Reference, x.Total })
    .ToListAsync();
```

**Only member accesses become columns; everything around them runs in .NET, per row.** That is the
boundary, and it is deliberate — `Select(x => x.First + " " + x.Last)` reads two columns and
concatenates client-side. Marten's answer is the same, and it keeps the surface honest: there is no
set of expressions that silently falls back to reading whole documents.

The *shape* of the projection is whatever C# allows — anonymous type, constructor, object
initialiser, interpolation, arithmetic — because the lambda body is rewritten and compiled rather
than interpreted.

::: tip
Members are deduplicated by locator, so `new { A = x.N, B = x.N * 2 }` selects `n` once.
:::

::: tip
**A NULL column becomes the target type's default, not a null.** `json_extract` yields SQL NULL for
an absent key, and the compiled projection unboxes each value to its declared type — so a null
reaching a non-nullable value type would be a `NullReferenceException` thrown from generated code
with nothing in the message to say which column or why. Defaulting matches what deserializing the
document would have produced for an absent key.
:::

**A projected value is read back the way the whole document would have been.** Members stored as a
JSON string, such as `Uri`, `TimeSpan`, `DateOnly`, `TimeOnly` and an enum under
`EnumStorage.AsString`, are handed to the store's own serializer. Its converters and naming policy
therefore apply, and a projection returns the same value a `LoadAsync` would. The same goes for a
member that is an object, a collection or a dictionary: `Select(x => x.Address)` and
`new { x.Id, x.Tags }` come back as the serializer would have built them.

One `Select` per query. A second would have to project members of the first's result, which is not a
document and has no locators.

## Distinct

```cs
await session.Query<Catch>().Select(x => x.Species).Distinct().ToListAsync();
await session.Query<Catch>().DistinctBy(x => x.Species).ToListAsync();
```

Two operators, and each refuses the other's job:

- **`Distinct()` requires a `Select`.** Over whole documents, DISTINCT compares serialized JSON byte
  for byte — so two documents equal in every member but written with different serializer settings,
  or with members in a different order, count as distinct. It would look right on small test data.
- **`DistinctBy` is refused after a `Select`.** It emits `row_number() over (partition by …)`
  filtered to 1, because keeping one *whole document* per key is not something DISTINCT can express.

`Distinct()` compares the projected values themselves, not the members underneath them. So
`Select(x => x.Number % 3).Distinct()` computes `% 3` in SQL and returns each remainder once. Fisher does
that for integer `%` and `/` by a constant and for negation, where SQLite and .NET agree on every value.
Any other computed value is refused by name before a `Distinct()` or an `OrderBy(x => x)`.

## Ordering after a projection

Ordering **before** a `Select` always works. After one, only `OrderBy(x => x)` over a single-value
projection — which is the `Select(...).Distinct().OrderBy(x => x)` idiom.

Ordering by a member of a *shaped* projection is refused rather than unimplemented: the mapping back
from an anonymous member to a locator exists only while the projection is a plain member-for-member
copy, and the whole point of the rewrite is that it need not be.
