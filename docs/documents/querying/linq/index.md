# Querying Documents with LINQ

```cs
var users = await session.Query<User>()
    .Where(x => x.Internal && x.LastName == "Doe")
    .OrderByDescending(x => x.LastLogin)
    .Take(20)
    .ToListAsync();
```

`Query<T>()` returns an ordinary `IQueryable<T>`, and Fisher's provider translates the operator chain
to SQL over `json_extract`.

::: warning
Anything Fisher cannot translate throws a `BadLinqExpressionException` **naming the operator**,
rather than falling back to client-side evaluation. That is deliberate: a silent fallback would read
every row of the table to answer a query that looked cheap.
:::

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
await query.ToCursorPageAsync(cursor, size);
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
| [Joins](/documents/querying/linq/joins) | `Join` and `GroupJoin`, chained |

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

## Ordering after a projection

Ordering **before** a `Select` always works. After one, only `OrderBy(x => x)` over a single-value
projection — which is the `Select(...).Distinct().OrderBy(x => x)` idiom.

Ordering by a member of a *shaped* projection is refused rather than unimplemented: the mapping back
from an anonymous member to a locator exists only while the projection is a plain member-for-member
copy, and the whole point of the rewrite is that it need not be.
