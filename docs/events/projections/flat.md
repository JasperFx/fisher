# Flat Table Projections

Project events into a **plain relational table** rather than into a document — for reporting, for a
BI tool, or for anything that wants columns.

```cs
public class QuestMetricsProjection : FlatTableProjection
{
    public QuestMetricsProjection() : base("quest_metrics")
    {
        Table.AddColumn("id", "TEXT").AsPrimaryKey();
        Table.AddColumn("quest_name", "TEXT");
        Table.AddColumn("member_count", "INTEGER");

        Project<QuestStarted>(map =>
        {
            map.Map(x => x.Name, "quest_name");
            map.SetValue("member_count", 0);
        });

        Project<MembersJoined>(map => map.Increment("member_count"));
        Project<MembersDeparted>(map => map.Decrement("member_count"));

        Delete<QuestEnded>();
    }
}
```

```cs
opts.Projections.Add(new QuestMetricsProjection(), ProjectionLifecycle.Async);
```

The table's shape is declared in the constructor, and the mappings alongside it. The row is keyed on
the stream.

## The mapping operations

| | |
| :--- | :--- |
| `map.Map(x => x.Member, "column")` | Write a member's value |
| `map.SetValue("column", value)` | Write a constant |
| `map.Increment("column")` | Add — defaults to 1 |
| `map.Decrement("column")` | Subtract |
| `Delete<TEvent>()` | Delete the row |

::: warning Behaviour change
**A member-valued `Decrement(x => x.Quantity)` landing on a row that does not exist yet now inserts
the *negated* value.** The insert branch applies the event to an implicit zero row, so a first event
carrying `5` leaves the column at `-5`. Fisher used to insert the parameter unchanged, so the same
event landed at `+5` — a decrement that incremented.

This is a correction rather than a feature: it settles a disagreement between the stores in Marten's
favour ([jasperfx#773](https://github.com/JasperFx/jasperfx/issues/773),
[fisher#183](https://github.com/JasperFx/fisher/issues/183)), and the rule worth remembering is that
**a decrement must never leave a column higher than it found it**.

Existing rows are not rewritten, so a table that already received a decrement as the first event for
some key can end up holding both conventions across the upgrade. The by-column
`Decrement("column")` form is unaffected: it still inserts `0`.
:::

## One upsert, not a MERGE

SQLite has had upsert syntax since 3.24, so the matched and not-matched branches are two clauses of
**one statement** — which is also why a parameter appearing in both is bound once by name rather than
duplicated.

::: tip
**An unqualified column on the right of the update assignment is the pre-update row.** That is what
makes `"a" = "a" + ?` an increment; `excluded."a"` would be the value the insert branch would have
written. Polecat spells the same thing `target.[a]`.
:::

## The table is created by the migration

Registering the projection puts a feature schema into the store's feature set, so
`ApplyAllConfiguredChangesToDatabaseAsync` creates the table with everything else — and
`AutoCreate.None` is honoured for free.

::: tip
Polecat issues a `CREATE TABLE` from inside its first apply, which works but routes around the store's
schema policy.
:::

## The table name

The store's logical schema folds into the name, because SQLite has no schemas and the prefix *is* the
isolation boundary between two logical stores in one file — a flat table that kept the bare name would
be silently shared by both.

::: tip
The `fi_` family prefix is **not** applied. That prefix marks a table Fisher owns the shape of, and a
flat table's shape is the projection's.
:::

The fold happens once the store's options are final, because the projection's constructor cannot see
the store and is usually registered in the same configuration lambda that sets `DatabaseSchemaName`,
in either order.

## Guid stream ids

The primary key holds a stream id, so it is TEXT and bound through the lowercase-canonical conversion.

::: warning
This is the one place a flat table meets that trap. Bound any other way, **the second event on a
stream inserts a second row instead of updating the first**.
:::

## Rebuilds

::: warning
A flat table's rows are not documents, so the mapped-type sweep that empties a snapshot's table cannot
see it. Fisher's teardown is told the table name directly — without that, a rebuild replays onto the
rows the previous run left.

If you write a projection with unusual storage, this is the lesson to carry: **declare what you
publish, and test the rebuild with a row the replay cannot recreate.** An ordinary rebuild test cannot
catch it, because a replay rewrites every row it can still produce.
:::

## Querying it

It is a plain table, so read it with [raw SQL](/documents/querying/raw-sql):

```cs
var rows = await session.AdvancedSql.QueryAsync<string, long>(
    "select quest_name, member_count from quest_metrics order by member_count desc", token);
```

Or point a reporting tool straight at the file.

## Lifecycles

Both inline and async work. Inline is cheap here — one upsert per event.
