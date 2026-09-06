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
- A member access on a scalar element (`x.Tags.Any(t => t.Length > 3)`) — the elements are plain
  values with no members to extract.
- A bare element comparison (`x.Tags.Any(t => t == "urgent")`) — use `Contains`.
- `Contains` against another document member, and `Contains` over child-object elements — use
  `Any(c => …)` with a predicate on the element's members.

::: tip
Predicates inside `Any`/`All`/`Count` follow **SQL null semantics**, consistently with the rest of
the provider: an element for which the predicate is NULL (say a null `Port` compared with `!=`) has
not satisfied it, so it fails `All` and is not counted — where C# would call `null != "Oslo"` true.
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

## Enums

Under the default `EnumStorage.AsInteger` everything works. Under `AsString`, **range comparison and
ordering are refused by name**, because the stored value is the member's name and would sort
alphabetically rather than by the enum's declared order. Equality still works. See
[JSON Serialization](/configuration/json#enum-storage-and-why-the-default-matters-here).

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

Not refused by name — these simply do not exist, so a ported file naming one will not compile:
`MatchesSql(…)`, `Stats(out QueryStatistics)` and `ToAsyncEnumerable()`. Compiled
queries (`ICompiledQuery<T>`) are absent too, on a
[measurement](https://github.com/JasperFx/fisher/issues/195) rather than by omission.

The [migration guide](/migration-guide#marten-features-fisher-does-not-have) lists each with what to
use instead.
