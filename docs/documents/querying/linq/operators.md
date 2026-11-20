# Supported LINQ Operators

## Query operators

| Operator | Supported |
| :--- | :--- |
| `Where` | yes |
| `OrderBy` / `OrderByDescending` / `ThenBy` / `ThenByDescending` | yes |
| `Take` / `Skip` | yes — `limit m offset n` |
| `Select` | yes — see [projections](/documents/querying/linq/#projections) |
| `Distinct` / `DistinctBy` | yes, with restrictions |
| `GroupBy` + `Select` | yes — see [grouping](/documents/querying/linq/grouping) |
| `Join` / `GroupJoin` + `SelectMany` | yes — see [joins](/documents/querying/linq/joins) |
| `SelectMany` over a document's own collection | yes — see [below](#selectmany-over-a-collection) |
| `Count` / `Any` / `First` / `Single` / `Last` | yes, with predicate overloads |
| `Sum` / `Min` / `Max` / `Average` | yes — see [aggregates](/documents/querying/linq/grouping#aggregates) |
| `Include` | yes — see [including related documents](/documents/querying/linq/includes) |
| `Search` / `PlainTextSearch` / `PhraseSearch` / `WebStyleSearch` / `PrefixSearch` / `NgramSearch` | yes — see [full-text search](/documents/querying/linq/full-text) |

## Comparisons

```cs
.Where(x => x.Name == "Frodo")
.Where(x => x.Age > 30)
.Where(x => x.Age >= 30 && x.Age < 65)
.Where(x => x.Name != null)
.Where(x => x.Internal)
.Where(x => !x.Internal)
```

## Collections

A member that is a collection — a `List<T>`, an array, anything `IEnumerable<T>`-shaped except
strings, byte arrays and dictionaries — is stored as a JSON array and queried through a correlated
sub-query over SQLite's `json_each` table-valued function:

```cs
// scalar elements — string, number, Guid, enum
.Where(x => x.Tags.Contains("urgent"))
// exists (select 1 from json_each(data, '$.tags') as each_1
//         where each_1.key is not null and each_1.value = @p0)

.Where(x => x.Tags.Any())                       // holds anything at all
.Where(x => x.Tags.Count() > 2)                 // also .Count property and array .Length
.Where(x => x.Stops.Count(s => s.Days < 5) == 2)

// scalar elements — a predicate on the element itself (fisher#639)
.Where(x => x.Depths.Any(d => d > 100))
.Where(x => x.Tags.Any(t => t.StartsWith("ur")))
.Where(x => x.Tags.All(t => t.Length < 10))

// child objects — member predicates on the element
.Where(x => x.Stops.Any(s => s.Port == "Oslo"))
.Where(x => x.Stops.Any(s => s.Days > 5 && s.Resupplied))
.Where(x => x.Stops.Any(s => s.Cargo.Contains("fuel")))   // nests, with a fresh alias per depth
.Where(x => x.Stops.All(s => s.Days < 5))

// membership the other way round — a value set, not a collection member
.Where(x => names.Contains(x.Name))          // in (…)
.Where(x => x.Name.IsOneOf("a", "b", "c"))   // the same, from the other direction
.Where(x => x.Name.In(allowed))

.Where(x => x.Tags.IsEmpty())
```

Element values go through the same conversion as a document member of the same type, so an enum
element honours `EnumStorage` and the serializer's naming policy, a Guid element matches its
lowercase canonical text, and a bool element matches the stored 1/0.

**The degenerate shapes are handled honestly.** An absent member, an empty array and a member stored
as JSON `null` all hold no elements: `Any()` is false, `Contains` matches nothing, `Count()`
compares as zero (where in-memory LINQ over a null collection would throw), and `All(...)` is
vacuously true, matching `Enumerable.All` over an empty sequence. The `key is not null` guard in the
generated SQL is what keeps a null member from matching — `json_each` over JSON `null` yields one
phantom row, and that row is the only one whose `key` is NULL.

**Refused rather than mis-translated**, each with a `BadLinqExpressionException` naming the problem:

- A predicate referencing anything outside the element's own scope — the outer document
  (`x.Stops.Any(s => s.Port == x.Name)`), or an enclosing lambda's element. Compare against locals
  or constants instead.
- A member access on a scalar element other than a string's `Length`
  (`x.CrewIds.Any(c => c.Variant > 3)`) — the elements are plain values with no members to extract.
- `Contains` against another document member, and `Contains` over child-object elements — use
  `Any(c => …)` with a predicate on the element's members.

::: tip
A comparison against a null member answers as **C#** does, inside `Any`/`All`/`Count` as everywhere
else: `x.N != 3` and `!(x.N > 3)` are true when `x.N` is null, so `!=` is rendered `is not` and a
negated comparison two-valued. `==` and a range comparison stay plain, which is what an index serves.
The exception is a string method on a null member (`!x.Name.StartsWith("a")`), which C# would throw
on: it keeps SQL's answer, so a null member matches neither the method nor its negation.
:::

::: warning
A non-nullable member that is **absent** from older documents deserializes as its default, but SQL
reads it as NULL. So `x.Crew == 0` does not match such a document, where loading it would show `0`.
:::

::: tip
`IsEmpty()` has to test null **as well as** length. `json_extract` yields SQL NULL for an absent key
and `json_array_length(null)` is NULL rather than 0, so a bare `= 0` would leave the row out instead
of matching. A caller asking "is this empty" means "is there anything in it", and "the key is not
there" is an honest yes.
:::

::: tip
`array.Contains(x)` binds to `MemoryExtensions.Contains(ReadOnlySpan<T>, T)` rather than
`Enumerable.Contains`, so Fisher matches on the call's *shape* rather than its declaring type — and
unwraps the span back to the array first, since a `ReadOnlySpan<T>` is a ref struct that cannot be
returned as `object`.
:::

## SelectMany over a collection

`SelectMany` flattens a collection member of the documents into one result per element (fisher#641):
values, enums, strings, Guids, decimals and child documents.

```cs
var tags = await session.Query<Post>().SelectMany(x => x.Tags).ToListAsync();

var lines = await session.Query<Order>()
    .Where(x => x.Customer == "Acme")
    .OrderBy(x => x.Placed)
    .Take(10)                          // the first ten orders
    .SelectMany(x => x.Lines)
    .Where(l => l.Quantity > 2)
    .OrderByDescending(l => l.Quantity)
    .Select(l => l.Sku)
    .ToListAsync();

var pairs = await session.Query<Order>()
    .SelectMany(x => x.Lines, (order, line) => new { order.Number, line.Sku })
    .ToListAsync();
```

Rows come back in LINQ's order: the documents' order, then each collection's own. `Take` and `Skip`
before the `SelectMany` page the documents; after it they page the flattened rows. Each element is read
as its own JSON and deserialized like the rest of the document, so an enum under either `EnumStorage`, a
Guid and a decimal come back exactly.

After the `SelectMany` Fisher translates `Where` over the element (the element itself for values, its
members for child documents, and a string's methods and `Length`), the `OrderBy` family, `Take`, `Skip`,
`Distinct()` over values, and one `Select`. The `Select` and a result selector run over the elements once
they are read. The terminals are `ToListAsync`, the `First`/`Single` family, `CountAsync`,
`LongCountAsync`, `AnyAsync` and the aggregates over the element or one of its members.

A `Where` after a result selector or a `Select` filters on what the row was built from, so
`.SelectMany(x => x.Lines, (order, line) => new { order.Number, line.Sku }).Where(r => r.Number > 2)` filters
on the order's `Number` in SQL. That works for a member set in an anonymous type or an object initializer.
`Select(...).Distinct()` computes the projected value in SQL and compares that: members of the element, or
integer `%`, `/` and negation over them, the same as over [documents](/documents/querying/linq/#distinct).

**Refused rather than mis-translated:** a second `SelectMany`, a `Select` before the `SelectMany`,
`Where` or ordering after `Take`/`Skip`, ordering after a result selector or a `Select`, a `Where` on a
member a constructor set, `Distinct` over child documents, after a result selector, or after a `Select`
whose value has no exact translation, and `LastAsync`, `ToAsyncEnumerable` and `ToPagedListAsync` over a
flattened query.

The SQL unrolls each document's array with `json_each` over a subquery of the documents:

```sql
select (__fi_p.data -> each_1.fullkey)
from (select data, row_number() over (order by ...) as __fi_ord from fi_doc_order where ... limit 10) as __fi_p,
     json_each(__fi_p.data, '$.lines') as each_1
where each_1.key is not null and ...
order by ..., __fi_p.__fi_ord, each_1.key
```

## Strings

See [Searching on String Fields](/documents/querying/linq/strings) — the short version is that Fisher
uses `instr`/`substr` rather than `LIKE`, because SQLite's `LIKE` is case-insensitive for ASCII while
`=` is case-sensitive.

## Timestamps

A `DateTimeOffset` member is compared **through SQLite's date parser**, not against the raw JSON:

```sql
strftime('%Y-%m-%dT%H:%M:%f', json_extract(data, '$.landedAt'))
```

That folds the trailing offset into UTC and renders fixed-width to the millisecond. Without it, the
comparison is against the text System.Text.Json wrote, which is not order-preserving twice over:
trailing fractional zeros are trimmed, and the original offset is kept — so `12:34:56-05:00` sorts
before `12:34:56.789+00:00` while being five hours later.

Equality goes through the **same** normalisation as ordering. Two spellings of one instant must not
be equal for `>=` and unequal for `==`, which costs sub-millisecond discrimination on `==` — as it
does on both siblings.

::: tip
A null test stays on the raw JSON, because it asks whether the member is *present*, not whether it
parses.
:::

`DateOnly` and `TimeOnly` need none of this: a `DateOnly` is fixed-width with no offset and no
fraction, and a `TimeOnly`'s optional fraction is a strict suffix — so trimming shortens the string
without changing which of two values compares smaller.

## Decimals

A `decimal` member is stored as an ordinary JSON number and `json_extract` hands it back as REAL, so
comparisons, `IsOneOf`, `Contains`, `HAVING` and arithmetic on one all work and all compare
**numerically**:

```cs
.Where(x => x.Total > 100m)
.Where(x => x.Total == 250.75m)
.Where(x => x.Total.IsOneOf(50m, 250.75m))
.Where(x => x.Total + 60m > 200m)
.OrderByDescending(x => x.Total)
```

Nothing to configure. Fisher normalises the comparison value to `double` where the query binds it,
because Microsoft.Data.Sqlite binds a raw `decimal` as **TEXT** and SQLite orders every numeric value
below every TEXT one — see
[SQLite differences](/configuration/sqlite#decimal-is-a-json-number-and-it-compares-as-one) for what
that means if you ever write the statement yourself.

**Arithmetic on a `decimal` member is never computed in SQLite**, which has only `double` arithmetic:
there, `0.3 - 0.1` is `0.19999999999999998`, so `x.Price - 0.1m < 0.2m` would match a price of `0.3`.
A member plus or minus constants, on either side of the comparison, is folded on the .NET side in
`decimal` arithmetic: `x.Price - 0.1m < 0.2m` is sent as `price < 0.3`, and `0.3m - x.Price < 0.2m` as
`price > 0.1`. Every other `decimal` or `float` arithmetic shape — two members (`x.Price + x.Fee`),
multiplication, division — and a `%` that is not integral are refused with a
`BadLinqExpressionException` rather than answered in `double`. Integral and `double` arithmetic
translate as before, because SQLite computes them the way C# does.

The comparison is exact, to every digit a `decimal` holds. The value is compared against the number
as System.Text.Json wrote it, not against the REAL `json_extract` reads, so two decimals that differ
only past a double's fifteenth digit are two values in a `Where`, an `OrderBy`, a `Select`, a
`Distinct` or a `GroupBy`, as they are in C#. The aggregates (`SumAsync` and its siblings) compute in
`decimal` too. A comparison done in `double` in C# — `(double)x.Total > 0.5` — stays a `double`
comparison.

Comparing two members is exact as well: `x.Price < x.Cost` and `x.Price == x.Quantity` compare the decimals
themselves, with C#'s answer when either side is null. A `double` member converted to `decimal`
(`(decimal)x.Ratio == x.Price`) is refused with a `BadLinqExpressionException`, because C# rounds the
double to fifteen significant digits before it compares; write the comparison in `double` instead.

## Enums

Under the default `EnumStorage.AsInteger` everything works. Under `AsString`, **range comparison and
ordering are refused by name**, because the stored value is the member's name and would sort
alphabetically rather than by the enum's declared order. Equality still works. See
[JSON Serialization](/configuration/json#enum-storage-and-why-the-default-matters-here).

## Strong-typed identifiers

A wrapper such as `readonly record struct CustomerId(Guid Value)` can be queried like the value it
holds, **provided it is registered**:

```cs
opts.RegisterValueType<CustomerId>();

session.Query<Order>().Where(x => x.CustomerId == customerId);
session.Query<Order>().Where(x => x.CustomerId.Value == guid);
session.Query<Order>().Where(x => ids.Contains(x.CustomerId));
session.Query<Order>().Where(x => x.Lines.Contains(lineId));
session.Query<Order>().OrderBy(x => x.CustomerId).Select(x => x.CustomerId);
```

Registration makes Fisher store the wrapper in the JSON as its primitive. An unregistered wrapper is
stored as an object, `{"value":…}`, which SQLite cannot compare against a value. The one exception is
the document's **identity**: `Where(x => x.Id == id)` and `Select(x => x.Id)` work whether or not the
id type is registered, because the `id` column always holds the inner value. See
[Strong-typed identifiers](/documents/identity) for what registering changes about rows already
written.

## Metadata operators

```cs
.Where(x => x.ModifiedSince(cutoff))
.Where(x => x.ModifiedBefore(cutoff))
```

These compare `last_modified` as **text with no `strftime` wrapper**, because the column already
holds the fixed-width UTC form — the same asymmetry `DeletedSince` / `DeletedBefore` have.

::: warning
**`CreatedSince` / `CreatedBefore` are deliberately absent.** There is no `created_at` column to
answer from unless you enable it, and answering from `last_modified` would be a different question
asked with a straight face. Use `Where(x => x.CreatedAt > cutoff)` against a
[mapped metadata member](/documents/metadata) instead.
:::

## Soft delete operators

```cs
.Where(x => x.MaybeDeleted())
.Where(x => x.IsDeleted())
.Where(x => x.DeletedSince(cutoff))
.Where(x => x.DeletedBefore(cutoff))
```

See [Deleting Documents](/documents/deletes#reading-soft-deleted-documents).

## Tenancy operators

```cs
session.Query<Order>().AnyTenant()
session.Query<Order>().TenantIsOneOf("acme", "globex")
```

Both *replace* the tenant term rather than composing with it, and both are refused against a type
that is not `MultiTenanted()` — there is no column to have an opinion about.

## Raw SQL inside a `Where`

```cs
.Where(x => x.MatchesSql("json_extract(data, '$.weight') > ?", 5))
.Where(x => x.MatchesSql('^', "json_extract(data, '$.tag') = ^", tag))   // SQL with a literal ?
```

For the predicate the translator cannot express. Unlike [`AdvancedSql`](/documents/querying/raw-sql),
which replaces the **whole** query, this is one term among the others — so the ordering, the paging,
the projection and all three implicit filters still apply, and the fragment is bracketed for you so an
`or` inside it cannot swallow them.

Columns are the physical ones. `session.ToSql(...)` over an ordinary query shows the spellings.

::: danger
**The SQL is yours and is not inspected.** Fisher parameterizes everything it composes itself; here
the text is the caller's by contract, so it is the caller's job not to concatenate untrusted input
into it.

**Pass values as parameters.** They are bound, never interpolated, and go through the same conversions
[raw SQL](/documents/querying/raw-sql) applies — a Guid to lowercase canonical text, a timestamp to the
fixed-width UTC form, a decimal to REAL. Interpolating them instead is not only the injection risk: all
three of those bind to something Fisher never wrote and match **nothing**, silently, because no rows is
an ordinary answer.

A placeholder/value count mismatch is refused by name rather than becoming an index error in one
direction and silence in the other.
:::

## A total alongside a query

```cs
var page = await session.Query<Order>()
    .Where(x => x.Open)
    .Stats(out var stats)
    .OrderBy(x => x.Placed).Skip(20).Take(10)
    .ToListAsync();

stats.TotalResults;   // how many matched, ignoring Take/Skip
```

The total is a second statement rather than `count(*) over ()`, because a window function returns no
row at all when the page is past the end — which is when a caller most needs the real total.

Honoured by the terminals that return rows. A **scalar** terminal — `CountAsync`, `AnyAsync`, the
aggregates — refuses it by name, because it already *is* the number and a `TotalResults` left at zero
would be a wrong answer the caller cannot see.

## Streaming

```cs
await foreach (var order in session.Query<Order>().Where(x => x.Open).ToAsyncEnumerable())
{
    // …
}
```

For a result set large enough that holding it is the problem. Everything `ToListAsync` does still
applies — the implicit filters, the identity map under a tracking session, a hierarchy resolving to its
real sub-classes — and a `Select` projection streams too. **A join is refused by name**: its rows are
stitched from both sides by the join plan, and `ToListAsync` is the operator for those.

::: tip
This is cheaper here than the raw-SQL `IAdvancedSql.StreamAsync`, which has to run **outside** the
resilience pipeline and says so. The LINQ path never ran inside it, so this operator forfeits nothing
`ToListAsync` has.
:::

## Explaining a query

```cs
var plan = await session.Query<Catch>().Where(x => x.Species == "Pike").ExplainAsync();

plan.UsesIndex;   // did the planner reach the index you declared?
plan.Steps;       // SQLite's own rows, in order
plan.Sql;         // the exact statement that was explained
```

SQLite's `EXPLAIN QUERY PLAN`, over the exact statement the query would run. It answers the question a
[declared index](/documents/indexing/indexes) otherwise leaves unanswerable: **is it being used?** The
planner reaches an expression index only when the query's expression matches the index's, so an index
built from a hand-written `json_extract` is created without error, never used, and reports nothing
anywhere.

It plans; it does not execute. Nothing is read and nothing is written.

::: tip
There is no Marten-portable shape here and none is invented. PostgreSQL's `EXPLAIN (FORMAT JSON)`
returns a costed, nested tree with an optional execution pass; SQLite's returns four columns of prose
with no costs and no `ANALYZE`. `Steps` is what SQLite said, in the order it said it, and `UsesIndex`
is an honest reading of that prose rather than a structured field.
:::

## Waiting for projections

```cs
.QueryForNonStaleData(TimeSpan.FromSeconds(5))
```

It is a wait rather than SQL, and it survives being wrapped for a count, a page, an aggregate or a
reversal — the timeout is read by walking the subquery chain, so each wrap site does not have to
remember it.

## What is refused

Each of these throws a `BadLinqExpressionException` naming the operator, so you find out at the call
rather than through a slow query:

- Client-side evaluation of anything untranslatable
- `GroupBy` with no `Select`, and the element/result-selector overloads
- `Distinct()` over whole documents; `DistinctBy` after a `Select`
- Ordering by a member of a shaped projection
- A second `Select`
- After a join: keyset paging, JSON reads, and `Select` / `GroupBy` / `Distinct` / `DistinctBy`
- Ordering or range-comparing a string-stored enum
- Inside a collection predicate: outer-scope references, member access on scalar elements, and bare
  element comparisons — see [Collections](#collections)
- A soft-delete or tenancy operator against a type that has no such column
- `Include()` combined with `Select`, `GroupBy`, a join, or any terminal that returns no documents —
  see [including related documents](/documents/querying/linq/includes#what-is-refused)
- A full-text operator against a type with no declared index, or against one whose tokenizer cannot
  serve it — see [full-text search](/documents/querying/linq/full-text#what-is-refused)

## Marten operators that are absent

**None, as of the full-text operators landing.** Every LINQ operator this page's Marten counterpart
lists now exists here, so a ported query compiles — though several mean something narrower, and the
[migration guide](/migration-guide#behaviour-that-differs) is where those are named.

Compiled queries (`ICompiledQuery<T>`) remain absent, on a
[measurement](https://github.com/JasperFx/fisher/issues/195) rather than by omission — building the
SQL is 4–11% of an ordinary Fisher query, so the absolute saving is around 2 µs.

## Nullable members and date parts

A member read off a stored value works in `Where`, `OrderBy`, `Select` and `GroupBy`:

```csharp
.Where(x => x.Quantity.Value > 2)     // the same as x.Quantity > 2
.Where(x => x.PlacedAt.Year == 2026)
.GroupBy(x => x.PlacedAt.Year)
```

`Year`, `Month`, `Day`, `Hour`, `Minute` and `Second` are read at the value's own offset, which is what
.NET means by them: 23:30 at `-02:00` on New Year's Eve is still the old year, even though it is the
next one in UTC.

Anything else below a stored value, such as `.Date`, `DayOfWeek` or a `TimeSpan`'s `TotalHours`, is refused
with a `BadLinqExpressionException`. Compare the whole value against a range instead:

```csharp
.Where(x => x.PlacedAt >= start && x.PlacedAt < end)
```
