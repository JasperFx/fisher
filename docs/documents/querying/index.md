# Querying Documents

Fisher offers several ways to read a document, and they differ in what they cost and what they carry.

| Approach | |
| :--- | :--- |
| [Loading by id](/documents/querying/byid) | `LoadAsync`, `LoadManyAsync`, `CheckExistsAsync` |
| [LINQ](/documents/querying/linq/) | `Query<T>()` — where, ordering, paging, projections, grouping, joins |
| [Raw JSON](/documents/querying/query-json) | Skip the serializer round trip entirely |
| [Batched queries](/documents/querying/batched-queries) | Several reads back to back on one connection |
| [Raw SQL](/documents/querying/raw-sql) | `AdvancedSql` — your SQL, typed results |

## The three implicit filters

Whichever path you take, Fisher adds up to three filters you did not write:

| Filter | When |
| :--- | :--- |
| **Tenant** | The type is `MultiTenanted()` |
| **Soft delete** | The type is `SoftDeleted()` and you did not ask for deleted rows |
| **Hierarchy** | The type is a registered base or sub-class |

Each is applied as **one statement-level pass**, not by wrapping each caller predicate. That
distinction is worth a paragraph, because getting it wrong is a silent cross-tenant read:

::: warning
Composing an implicit filter into a per-predicate wrapper repeats it once per predicate *and omits it
entirely from a query with none* — so `Query<T>()` with no `Where` would return every tenant's rows.
Silent, and asymmetric in the way that makes it hard to spot: the tenant owning most of the data sees
a correct-looking answer with extras, and a tenant with none sees somebody else's.

All three filters are statement-level passes so that no query shape can drop one. If you are
extending Fisher, do not fold any of them back into a per-predicate wrapper.
:::

Being its own pass is also what makes [`AnyTenant()` and `TenantIsOneOf(...)`](/documents/multi-tenancy)
possible: they *replace* the term, which is impossible while it is welded to each predicate.

There are exactly two places Fisher goes around the filters on purpose, and both are documented where
they live: [`MetadataForAsync`](/documents/metadata#reading-metadata-without-mapping-it) and
[bulk insert's duplicate probe](/documents/bulk-insert#ignoreduplicates).

## Seeing the SQL

```cs
var sql = session.ToSql(session.Query<User>().Where(x => x.Internal));
```

`ToSql` renders parameter *names*, not values, so the text is readable rather than executable. It is
the cheapest way to check that an implicit filter is actually present.

## Waiting for projections to catch up

```cs
var results = await session.Query<Summary>()
    .QueryForNonStaleData(TimeSpan.FromSeconds(5))
    .ToListAsync();
```

::: tip
`QueryForNonStaleData` waits for the **whole store**, where Polecat waits for the projections feeding
the queried type. Stricter rather than weaker, and it needs no type-to-shard map.
:::
