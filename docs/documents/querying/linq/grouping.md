# Grouping and Aggregates

## Aggregates over a query

```cs
await session.Query<Order>().SumAsync(x => x.Total);
await session.Query<Order>().Where(x => x.Open).AverageAsync(x => x.Total);
await session.Query<Catch>().MinAsync(x => x.Landed);
await session.Query<Catch>().MaxAsync(x => x.Weight);
```

::: tip
The selector is an **argument**, not part of the expression tree. Polecat builds a synthetic
`MethodCallExpression` carrying the selector and parses it back out; Fisher's terminals take it
directly, so the operator-chain parser needed no change at all.
:::

### The empty result is the case that matters

`sum`, `min`, `max` and `avg` all return NULL over no rows, where `count` returns 0. Fisher maps null
to `default` before casting — an unguarded cast fails **only** on an empty result, which is how it
would ship.

::: tip
`total()` would give `0.0` for an empty `sum` but is always REAL, so it is the wrong tool for the
`int` and `decimal` overloads.
:::

### Two guards

| Aggregate | Requires |
| :--- | :--- |
| `Min` / `Max` | The member **orders** — a string minimum and a timestamp minimum are real answers. |
| `Sum` / `Average` | The member is an actual **number**. |

The second guard exists because **SQLite's `sum()` over text returns 0 rather than failing**: summing
a string-stored enum would report a plausible total for a column that has none. Enums are excluded
from `Sum` even under `EnumStorage.AsInteger`, since their numeric value is an identifier rather than
a quantity.

::: warning
`MinAsync(x => x.Id)` over a Guid orders **by text**, which is not .NET's `Guid` ordering.
`Guid.CompareTo` compares the first group as a *signed* int, so the two disagree whenever the set
straddles `0x80000000`. If you compare against `Enumerable.Min` in a test, use `StringComparer.Ordinal`
to build the expectation.
:::

### Aggregates and paging

`Take(5).SumAsync(...)` sums the **page**. The paged query becomes a subquery projecting the member
under an alias, and the aggregate wraps it.

`Last` needs the inverse: `OrderBy(x).Take(3).LastAsync()` is the last of *those three*, so the
reversal goes on the outer statement rather than in place. Inverting in place would answer about the
whole table.

### After a Select

An aggregate after a `Select` is **refused by name**. The aggregate builds from the chain's *source*
type rather than its element type — both a join and a projection make those diverge — and asking the
schema for a mapping of a projected type would be an `InvalidOperationException` about identity
members, naming neither the operator nor the reason.

An aggregate over a [join](/documents/querying/linq/joins) *is* answered.

## GroupBy

```cs
var bySpecies = await session.Query<Catch>()
    .GroupBy(x => x.Species)
    .Select(g => new { Species = g.Key, Count = g.Count(), Total = g.Sum(x => x.Weight) })
    .ToListAsync();
```

`GroupBy` must be followed by a `Select` over the grouping. Aggregates available inside it:

```cs
g.Key
g.Count()
g.Sum(x => x.Weight)
g.Min(x => x.Weight)
g.Max(x => x.Weight)
g.Average(x => x.Weight)
```

::: warning
**`GroupBy` without a `Select` is refused**, rather than handing back `IGrouping` instances — that
would mean reading every row of every group, which is the opposite of what grouping in SQL is for.
The element- and result-selector overloads are refused for the same reason.
:::

### HAVING

Where a `Where` sits decides what it filters. Before the `GroupBy` it is a `WHERE` over rows; after
it, a `HAVING` over groups:

```cs
await session.Query<Catch>()
    .Where(x => x.Landed > cutoff)                 // WHERE — over rows
    .GroupBy(x => x.Species)
    .Where(g => g.Count() > 1)                     // HAVING — over groups
    .Select(g => new { g.Key, Count = g.Count() })
    .ToListAsync();
```

The chain is walked source-outward, so which one it is falls out of whether the key has been seen yet
— there is no lookahead.

The HAVING parser is deliberately **narrower** than the `Where` parser: a comparison between a
grouping expression and a constant, composed with `&&` / `||` / `!`, with reversed operands flipped
(`1 < g.Count()` becomes `count(*) > 1`). Widening it would mean answering questions about individual
rows from a clause that runs after they have been collapsed.

### Ordering a grouped query

By the key or by an aggregate, which is usually the reason grouping was reached for:

```cs
.GroupBy(x => x.Species)
.OrderByDescending(g => g.Count())     // before the Select
.Select(g => new { g.Key, Count = g.Count() })
```

It must come **before** the `Select`, because after it the element is the projected type. After a
single-value grouped `Select`, `OrderBy(x => x)` works the same way it does for an ordinary
projection.

## A trap that does not exist

SQLite permits a bare non-aggregated column in a `GROUP BY` select list and picks an arbitrary row
for it, where T-SQL rejects the query — so a query that errors on Polecat would silently return
arbitrary data here.

**It is unreachable through this API.** The `Select`'s parameter is the *grouping*, so there is no
ungrouped member in scope to select. The type system does the validation for free, which is worth
knowing before somebody adds a validator for a case that cannot arise.

## Conversions

Projected and aggregated values go through Fisher's own coercion, not `Convert.ChangeType`, and the
three explicit conversions are exactly the types Fisher encodes rather than stores natively:

| Type | Why |
| :--- | :--- |
| enum | Comes back INTEGER; needs `Enum.ToObject`. |
| `DateTimeOffset` | Comes back as `strftime` text with **no `Z` suffix** — parsed as universal. |
| `Guid` | Comes back TEXT. |

Neither `DateTimeOffset` nor `Guid` is `IConvertible` at all — both throw `InvalidCastException` from
inside `Convert.ChangeType`. All three were found by tests failing rather than by inspection.
