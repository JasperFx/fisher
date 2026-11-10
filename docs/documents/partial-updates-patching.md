# Partial Updates / Patching

Change part of a stored document without loading it.

```cs
session.Patch<Order>(orderId)
    .Set(x => x.Status, Status.Shipped)
    .Increment(x => x.RevisionCount)
    .Append(x => x.Notes, "shipped by night courier");

await session.SaveChangesAsync();
```

Or by criteria:

```cs
session.Patch<Order>(x => x.Status == Status.Open)
    .Set(x => x.Status, Status.Cancelled);
```

## This is the strongest single case for SQLite

json1 is built in — Marten needs a PL/pgSQL patch function installed on the server — there are no
`JSON_MODIFY` shape differences to work around, and it **composes**: every operation is one json1
function inside a single `update … set data = …`, and a chain nests into one statement.

And there is a dividend from an unrelated decision:
**a [duplicated field](/documents/indexing/duplicated-fields) follows a patch with nothing to
refresh**, because Fisher's duplicated columns are `VIRTUAL` generated columns over `data`. Both
siblings must update theirs inside the patch SQL. That is the clearest payoff of that design.

## What a patch costs, said plainly

::: warning
`json_set` **re-renders the document**, so a patched row is no longer byte-identical to what the
serializer would have written, and a new or renamed key lands at the end.

A patch avoids the deserialize / mutate / serialize round trip. It does **not** avoid the row
rewrite. Do not let "patching avoids the round trip" imply "patching is cheap" — and note that it
breaks the byte-exactness the [JSON reads](/documents/querying/query-json) promise.
:::

## The operations

`Patch<T>` returns `JasperFx.Events.IPatchExpression<T>`, the interface Marten and Polecat implement
too, so patch code reads the same against all three stores.

```cs
.Set(x => x.Name, "new name")
.Set("legacyKey", value)                  // by stored key
.Set("name", x => x.Home, value)          // a stored key under a member

.Increment(x => x.Count)                  // int, long, double, float, decimal
.Increment(x => x.Total, 5m)
.Increment(x => x.MaybeCount)             // the nullable overloads, from Fisher.Patching

.Append(x => x.Tags, "urgent")
.AppendIfNotExists(x => x.Tags, "urgent")
.AppendIfNotExists(x => x.Spots, spot, s => s.Name == spot.Name)
.Insert(x => x.Tags, "urgent", index: 2)  // no index appends
.InsertIfNotExists(x => x.Tags, "urgent", index: 0)
.Remove(x => x.Tags, "urgent")            // the first match
.Remove(x => x.Tags, "urgent", RemoveAction.RemoveAll)
.Remove(x => x.Spots, s => s.Depth > 10, RemoveAction.RemoveAll)

.Append(x => x.Licences, "shire", 1)      // a dictionary entry
.AppendIfNotExists(x => x.Licences, "shire", 1)
.Remove(x => x.Licences, "shire")

.Rename("oldName", x => x.NewName)

.Delete(x => x.Obsolete)
.Delete("legacyKey")
.Delete("name", x => x.Home)

.Duplicate(x => x.Source, x => x.CopyA, x => x.CopyB)  // copies the stored value
```

::: warning Moving from Fisher 1.x
Fisher 1.x had its own `Fisher.Patching.IPatchExpression<T>`. Adopting the shared one changed three
defaults, each to Marten's meaning:

- `Insert` with no index **appends**; 1.x inserted at the front. Pass `0` for the old behaviour.
- `Remove` takes the **first** match; 1.x removed every match. Pass `RemoveAction.RemoveAll`.
- `Duplicate` copies the source member's **stored** value and takes no value of its own. 1.x's
  `Duplicate(source, value, destinations)` is now `Set(source, value).Duplicate(source, destinations)`.

The nullable `Increment` overloads are extension methods in `Fisher.Patching`, since the shared
interface does not declare them.
:::

Element predicates go through the same parser a LINQ `Any(...)` over the member does, so they support
and refuse exactly what that does.

### The by-name overloads take the *stored* key

`"name"`, not `"Name"`. That is the point of them: reaching a key the type no longer has a member
for, which is exactly what `Rename` is for. They deliberately do not go through the member factory,
which would refuse the case they exist for.

## Details worth knowing

**Values go in through the store's serializer**, not the raw-SQL parameter conversions. Those exist
to match *columns*; a patched value lands inside `data`, so it must match what a full write would
have produced — a timestamp in System.Text.Json's format rather than the column format. Wrapping in
`json(?)` then makes a string a JSON string, a number a JSON number and an object a JSON object with
no per-type branching.

::: tip
**`Increment` needs `coalesce(…, 0)`.** `json_extract` of an absent *or null* key is SQL NULL and
`NULL + n` is NULL, so without it the member would silently become null instead of the increment.
Worth knowing because a non-nullable `int` serializes as 0 rather than being absent, so the bug is
only visible on a nullable member.
:::

**Steps that read what they change read the accumulated expression**, not the bare `data` column, so
a chain sees its own earlier work. The cost is that the SQL text grows with the chain.

**The version and timestamp columns are assigned explicitly.** They are not in the JSON, so nothing
about the json1 expression would move them — and without it an
[optimistic-concurrency](/documents/concurrency) type would silently stop seeing patched writes, and
`ModifiedSince` would miss them.

## Array operations

`Insert` at an index **rebuilds the array**, because `json_insert` only inserts where the path does
not exist — at an occupied index it is a silent no-op — and `json_replace` overwrites rather than
shifting.

::: tip
The rebuild does not lean on `json_each`'s row order, which is not a documented guarantee. It computes
an explicit ordinal and orders by it, so an element keeps `2k` below the insertion point and takes
`2k+2` at or above it, with the new element at `2*index+1` — landing strictly between two neighbours.
An index past the end sorts above everything and therefore appends.
:::

::: warning
An element is keyed on its **json1 type**, not on its value, and that matters for two element kinds:

- SQLite has no boolean, so a JSON `true` arrives as the integer 1 and would be written back as `1` —
  every rebuild silently turning an array's booleans into numbers.
- A JSON `null` element reads back as SQL NULL, so a `where value <> ?` is NULL rather than true for
  it — which made `Remove` drop **every null in the array**.

Both are handled. If you are extending the patch operations, this is the trap.
:::

::: tip
json1's JSON subtype does not survive a subquery, so an aggregate over projected elements has to
re-parse with `json_group_array(json(v))`. `Insert` meets this; `Remove` does not, because its rebuild
is a single flat select.
:::

## Soft delete

A patch does not reach a soft-deleted row — the same rule the load SQL and the LINQ default filter
follow.

## Patches and projections

A patch changes a document directly. If that document is a projection's snapshot, the next
[rebuild](/events/projections/async-daemon#rebuilds) overwrites it — a projection's rows are derived
data, and a patch is not an event.
