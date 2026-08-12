# Foreign Keys

A real, enforced foreign key between two document tables.

```cs
opts.Schema.For<Catch>()
    .ForeignKey<Angler>(x => x.AnglerId);

// With a cascade, and a column name of your own
opts.Schema.For<Catch>()
    .ForeignKey<Angler>(x => x.AnglerId, CascadeAction.Cascade, columnName: "angler_id");
```

## SQLite supports this completely

Its reputation invites the question, so: foreign keys, `ON DELETE CASCADE` and `ON DELETE SET NULL`
are all there.

Enforcement is per-connection through `PRAGMA foreign_keys` and off by default *in the SQLite
library* — but **on for every connection Fisher opens**, because Weasel's default profile sets it. So
a document foreign key bites the moment it is declared.

## The child column is a generated column

**Declaring a foreign key duplicates the member implicitly**, and that is a real divergence from both
siblings, where the two are already separate concepts because their duplicated columns are *written*.

Here the column is generated, so folding one into the other loses nothing — and the alternative is an
error message telling you to write a `Duplicate(...)` line with no other purpose. An explicit
`Duplicate` on the same member still wins, because that call is idempotent.

The column is indexed, which SQLite wants anyway: without an index on the child column, every parent
delete scans the child table.

::: tip
Whether SQLite accepts a `VIRTUAL` generated column as a foreign key *child* was the one genuinely
uncertain thing here — a "no" would have forced a written column and reopened the whole write-path
question. It was probed against SQLite 3.50.4 before anything was built: the table is created, an
orphan insert fails, a row whose key is absent from the JSON is allowed, `ON DELETE CASCADE` works,
and `pragma_foreign_key_list` reports it.
:::

## The rules

- **The referenced side is always the other type's `id`.** SQLite requires a foreign key to reference
  a `PRIMARY KEY` or `UNIQUE` column, and a document table's identity is its primary key. Referencing
  a duplicated field would need that field's index to be `UNIQUE`.
- **A document whose member is absent or null is unconstrained**, because `json_extract` yields SQL
  NULL and SQLite exempts a NULL child. Same asymmetry as a `UNIQUE` index over an absent member.
- **The referenced table is named unqualified**, which is forced rather than chosen: SQLite's
  `REFERENCES` clause cannot be schema-qualified. Since Fisher folds its logical schema into the table
  *prefix*, the rendered name is already the whole name — so two logical stores in one file each
  reference their own table.
- **A self-reference is refused at configuration time.** Not because SQLite minds, but because the
  only shape that wants one — a tree — has no insert order that satisfies the constraint for its own
  root.

## Ordering matters when deleting

`DeleteAllDocumentsAsync` orders by foreign key, **referencing tables first**. The order comes from
`pragma_foreign_key_list` rather than from the store's configuration, so it is the *database's*
account of what references what: a table left behind by an earlier configuration is still enforced
even though the store no longer knows about it.

`CompletelyRemoveAllAsync` needs no ordering — SQLite does not enforce a key against a dropped table.

## Adding one to an existing table

::: warning
SQLite has no `ALTER TABLE ADD CONSTRAINT`, so adding a foreign key to a type whose table already
exists means **recreating the table**. Weasel reports that rather than attempting it.
:::

## Cascades

| `CascadeAction` | Effect |
| :--- | :--- |
| `NoAction` | The default — a parent delete fails while children exist |
| `Cascade` | Deleting the parent deletes the children |
| `SetNull` | Deleting the parent nulls the children's key |

::: tip
`SetNull` against a **generated** column cannot write the column — the value lives in `data`. Prefer
`Cascade` or `NoAction`, and handle the null-out in your own code if you need it.
:::
