# Joins

Fisher can join document tables in LINQ, in both method and query syntax, chained across any number
of tables.

**This is the LINQ tier where SQLite is the easiest of the three dialects rather than the hardest.** A
join between two document tables is:

```sql
join fi_doc_catch inner_t on outer_t.id = json_extract(inner_t.data, '$.anglerId')
```

No `OPENJSON`, no lateral join, and an [expression index](/documents/indexing/indexes) usable on
either side. It is also worth *more* here than on either sibling: the usual argument against joins in
a document store is that a round trip is cheap next to a join's cost, and an embedded store has no
round trip to be cheap — the alternative is two statements and a client-side stitch.

## Inner join

```cs
var rows = await session.Query<Angler>()
    .Join(session.Query<Catch>(),
          angler => angler.Id,
          c => c.AnglerId,
          (angler, c) => new { angler.Name, c.Species, c.Weight })
    .ToListAsync();
```

Or in query syntax:

```cs
var rows = await (from angler in session.Query<Angler>()
                  join c in session.Query<Catch>() on angler.Id equals c.AnglerId
                  where c.Weight > 5
                  orderby angler.Name
                  select new { angler.Name, c.Species })
    .ToListAsync();
```

## Left join

.NET 10's `LeftJoin`:

<!-- snippet: sample_left_join -->
<a id='snippet-sample_left_join'></a>
```cs
var rows = await session.Query<JoinShopper>()
    .LeftJoin(session.Query<JoinPurchase>(),
        shopper => shopper.Id,
        purchase => purchase.ShopperId,
        (shopper, purchase) => new { shopper.Name, Item = purchase == null ? "none" : purchase.Item })
    .ToListAsync();
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Linq/left_and_right_joins.cs#L106-L113' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_left_join' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

It is the same join as `GroupJoin` followed by `SelectMany` with `DefaultIfEmpty`, and renders the same SQL:

```cs
var rows = await session.Query<Angler>()
    .GroupJoin(session.Query<Catch>(),
               angler => angler.Id,
               c => c.AnglerId,
               (angler, catches) => new { angler, catches })
    .SelectMany(x => x.catches.DefaultIfEmpty(),
                (x, c) => new { x.angler.Name, Species = c == null ? null : c.Species })
    .ToListAsync();
```

## Right join

.NET 10's `RightJoin` keeps every row of the inner query:

<!-- snippet: sample_right_join -->
<a id='snippet-sample_right_join'></a>
```cs
var rows = await session.Query<JoinShopper>()
    .RightJoin(session.Query<JoinPurchase>(),
        shopper => shopper.Id,
        purchase => purchase.ShopperId,
        (shopper, purchase) => new { Name = shopper == null ? "nobody" : shopper.Name, purchase.Item })
    .ToListAsync();
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Linq/left_and_right_joins.cs#L216-L223' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_right_join' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Fisher writes it as a left join with the two queries swapped, so `outer.RightJoin(inner, ...)` is
`inner.LeftJoin(outer, ...)` and renders that SQL. The outer query's `Where` goes in the `ON` clause, since it says
which outer rows may match, and the inner query's `Where` goes in the `WHERE`. Only `Where` may come before a
`RightJoin` on either query; anything else is refused by name.

## Filtering the inner side

**Everything about the inner side goes in the `ON` clause; a post-join `Where` goes in the `WHERE`.**

That is not an inconsistency. An inner-side filter says which rows the join may *match*; a post-join
predicate says which joined rows *survive*. On a left join the two differ visibly — the first keeps
an unmatched outer row and the second may remove it, which is exactly what the same clauses do in
memory.

```cs
// Inner-side: the ON clause. Anglers with no heavy catch still appear.
session.Query<Angler>()
    .GroupJoin(session.Query<Catch>().Where(c => c.Weight > 5), …)

// Post-join: the WHERE clause. Anglers with no heavy catch are removed.
    .SelectMany(…).Where(x => x.Weight > 5)
```

::: tip
**The inner query's own predicates are applied.** Polecat drops them silently — it collects only the
tenant and soft-delete filters for its inner table, so `GroupJoin(session.Query<Catch>().Where(...))`
there returns rows the caller excluded.
:::

Anything beyond filtering on the inner source — ordering, paging, a projection — is refused, being a
question about one outer row's matches after the join has flattened them.

## Chained joins

More than one join per query works:

```cs
var rows = await (from angler in session.Query<Angler>()
                  join c in session.Query<Catch>() on angler.Id equals c.AnglerId
                  join water in session.Query<Water>() on c.WaterId equals water.Id
                  select new { angler.Name, c.Species, Water = water.Name })
    .ToListAsync();
```

Each rung's outer key is written against **the shape the previous join produced**, and Fisher
resolves it back to a document to build the locator.

::: tip
Aliases are `outer_t`, `inner_t`, then `inner_t2`, `inner_t3` and so on, rather than being renumbered
to `t0`/`t1`. `ToSql` exists to be read, one join is overwhelmingly the common case, and the two names
say which side is which where a number does not.
:::

## What works over a join

`ToListAsync`, the `First` / `Single` / `Last` families, the scalar aggregates, `CountAsync`,
`AnyAsync`, `ToPagedListAsync` and `ToSql` all work — over a chain as well as over one join.

The join lives on the ordinary statement, not a parallel one, which is why they do:

::: tip
Polecat's join path re-implements the select list, the wheres, the ordering and the paging, so
anything built for one shape has to be built again for the other — it carries its own `Count` and its
own `TOP`/`OFFSET` rendering. Fisher's `Count`, `Any`, `ToPagedListAsync` and `ToSql` serve a join
without knowing it is one.
:::

## What is refused, by name

- Keyset paging (`ToCursorPageAsync`)
- The [JSON reads](/documents/querying/query-json)
- `Select` / `GroupBy` / `Distinct` / `DistinctBy` **after** the join
- A predicate naming a member the projection **computed**, since its value exists only after the row is
  read. An ordering key over one works when the computation is one Fisher translates — see
  [Ordering by a computed key](/documents/querying/linq/operators#ordering-by-a-computed-key)
- A key comparer on `Join`, `GroupJoin`, `LeftJoin` or `RightJoin`. The keys are compared as stored, in SQL,
  so the comparer would be ignored
- An operator other than `Where` before a `RightJoin`, and a join to an in-memory sequence
- `FullJoin` is not a `Queryable` operator, so there is nothing to translate
- A `GroupJoin`'s **group** — `x.catches.Count()` asks about rows the join has flattened

::: tip
The group is deliberately left unmapped rather than being silently answered about the one matched row.
:::

## Two details worth knowing

**The alias is built into the locator, not patched in afterwards.** Every member resolves as
`json_extract(outer_t.data, '$.name')` from the start — note that the alias belongs *inside*
`json_extract`, on `data`, not on its result. Polecat rewrites the rendered string afterwards, which
produces valid SQL that reads the wrong table whenever the pattern matches something it should not.
The case that tells the two apart is a member both sides have.

**The inner document is materialized by its own storage's selector.** A joined
[hierarchy](/documents/hierarchies) therefore comes back as its real sub-classes, with its metadata
binders intact. Deserializing the `data` column directly — which Polecat's join handler does — loses
`doc_type` resolution, so a sub-class comes back as its base, quietly missing whatever it added.

## Which side a member belongs to

A predicate or ordering key written after the join is resolved against whichever shape it names,
decided by its parameter's **type**: method syntax names the projected result; query syntax's `where`
and `orderby` come before its `select` and name the intermediate shape.

Both land on the projection's own two parameters, so which side a member belongs to is decided **by
parameter reference, not by type** — which is what makes a self-join work.

## Making a join fast

Index the inner side's key:

```cs
opts.Schema.For<Catch>().Index(x => x.AnglerId);
```

An [expression index](/documents/indexing/indexes) costs no column at all, and the planner will use
it because Fisher builds the index's expression and the query's from the same member locator.
