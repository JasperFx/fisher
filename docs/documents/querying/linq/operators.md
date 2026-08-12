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

```cs
.Where(x => x.Tags.Contains("urgent"))
.Where(x => names.Contains(x.Name))          // in (…)
.Where(x => x.Name.IsOneOf("a", "b", "c"))   // the same, from the other direction
.Where(x => x.Name.In(allowed))
.Where(x => x.Tags.IsEmpty())
.Where(x => x.Tags.Any())
.Where(x => x.Tags.Count() > 2)
```

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
- A soft-delete or tenancy operator against a type that has no such column
