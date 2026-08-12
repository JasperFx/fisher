# Duplicated Fields

`Duplicate` lifts a member into a column of its own and indexes it, so a predicate against that
member is a range scan rather than `json_extract` per row.

```cs
opts.Schema.For<Catch>()
    .Duplicate(x => x.Species)
    .Duplicate(x => x.Landed);
```

```cs
[DuplicateField]
public string Species { get; set; } = "";
```

## The column is a VIRTUAL generated column

**That is the whole divergence from Marten and Polecat, whose duplicated columns are *written* on
every upsert.** Three things follow, and they are why it was worth diverging:

- **It cannot drift from `data`, because nothing writes it.** `Duplicate` can be added to a type that
  already has rows and every one of them is correct at once. A written column would need a backfill.
- **It costs index space, not row space.** `VIRTUAL` computes on read; only the index materialises.
- **The write path is untouched.** No extra binder, no shift in the positional `?` contract the
  document write statements maintain.

There is a fourth dividend, and it is the clearest one:
**a [patch](/documents/partial-updates-patching) has nothing to refresh.** Both siblings must update
their duplicated columns inside the patch SQL.

## The generated expression is the member's own locator

Not a hand-written `json_extract`. That is what makes a duplicated member mean exactly what an
unduplicated one means — and it is what makes a duplicated **timestamp** work: the locator is the
[`strftime` wrapper](/documents/querying/linq/operators#timestamps), so the column holds the
normalised fixed-width UTC form, sorts as text and is indexable.

::: tip
`strftime` over a value (rather than over `'now'`) is deterministic, which is what SQLite requires of
a generated column's expression.
:::

## What a duplicated member still is

A `DuplicatedMember` delegates **everything except its locator** to the underlying member. So:

- a duplicated string-stored enum still [refuses to be ordered](/configuration/json#enum-storage-and-why-the-default-matters-here),
- a duplicated bool still binds 1/0,
- a **null test stays on the JSON**, because it asks whether the member is *present* — which is not
  quite whether the column is null (an unparseable value yields a null column with the key present) —
  and no index would serve it anyway.

## Column naming and types

The default column name is **snake case**: `LandedAt` becomes `landed_at`, `Water.Name` becomes
`water_name`. Marten simply lowercases; every other column on a Fisher document table is snake case,
and a duplicated column sits among them.

```cs
opts.Schema.For<Catch>().Duplicate(x => x.LandedAt, columnName: "landed");
```

::: warning
A name Fisher owns (`id`, `data`, `last_modified`, …) is **refused at configuration time**, because
SQLite would otherwise report it as a duplicate column at CREATE TABLE — long after the line that
caused it.
:::

**The declared type is the column's comparison affinity**, so it is load-bearing rather than
decorative: declare a numeric member TEXT and it starts sorting as text. `decimal` goes to REAL,
because `json_extract` hands back a REAL for any JSON number and a column whose affinity disagrees
with its own expression is the one shape that cannot be right.

## A migration caveat

::: warning
`pragma_table_info` does **not** list generated columns; only `pragma_table_xinfo` does. Weasel's
delta detection uses the former, so every duplicated column would read as missing and the migration
would emit `ALTER TABLE … ADD COLUMN` for it *every time* — and since Fisher runs a migration on the
first write of each document type per process, the second one would fail with `duplicate column
name`.

Fisher overrides the query to use `table_xinfo`, whose first six columns are `table_info`'s in the
same order. Reported as [weasel#426](https://github.com/JasperFx/weasel/issues/426); the override goes
when that ships.
:::

## Duplicate or Index?

If you only want the member to be fast, use [`Index`](/documents/indexing/indexes) — it adds no column
at all. Reach for `Duplicate` when something else needs to *name* the column: a foreign key, a query
you write by hand, an external reporting tool reading the table.

Declaring a [foreign key](/documents/indexing/foreign-keys) duplicates the member implicitly, and an
explicit `Duplicate` on the same member still wins, because the call is idempotent.
