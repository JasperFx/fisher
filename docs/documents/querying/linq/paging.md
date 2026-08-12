# Paging

Fisher carries two paging operators, answering different questions. Both are worth having, for the
reason Marten and Polecat carry both.

| | `ToPagedListAsync` | `ToCursorPageAsync` |
| :--- | :--- | :--- |
| Jump to an arbitrary page | yes | no |
| Reports a total | yes | no |
| Stable under concurrent writes | no | yes |
| Degrades as the offset grows | yes | no |

## Offset paging

```cs
var page = await session.Query<Order>()
    .Where(x => x.Status == Status.Open)
    .OrderByDescending(x => x.PlacedAt)
    .ToPagedListAsync(pageNumber: 3, pageSize: 20);

// IPagedList<T> *is* an IReadOnlyList<T> — the rows are the list
foreach (var order in page) { … }

page.TotalItemCount;   // long, across the whole query
page.PageCount;
page.PageNumber;
page.HasNextPage;
page.FirstItemOnPage;  // 1-based, or 0 when the page is empty
```

Underneath it is `limit m offset n`. T-SQL's `TOP(n)` / `OFFSET … FETCH NEXT` split collapses to one
form here, and SQLite needs no `ORDER BY (SELECT NULL)` filler.

::: tip
An offset with no limit must say `limit -1` first — a bare `offset` is a parse error in SQLite. Fisher
emits it for you; it is worth knowing if you are reading `ToSql` output.
:::

### The total is a second statement

Not `count(*) over ()`. A window function returns **no row at all** when the page is past the end,
which is exactly when a pager most needs the real total.

### The total is not `CountAsync`

```cs
await query.Take(5).CountAsync();   // 5 — CountAsync counts the *page*
page.TotalItemCount;                // the whole result set
```

Deliberately different questions. `CountAsync` counts the page when the query is paged; the count
behind `TotalItemCount` discards `Take`/`Skip`, because a total that counted the page would say
nothing. Both are right for their caller, and conflating them would make one silently wrong.

## Keyset (cursor) paging

```cs
var page = await session.Query<Catch>()
    .OrderByDescending(x => x.Landed)
    .ThenBy(x => x.Id)                    // the terminal identity key — required
    .ToCursorPageAsync(cursor: null, pageSize: 20);

page.Items;        // IReadOnlyList<T>
page.NextCursor;   // null when there is no next page
```

`CursorPage<T>` is typed, where Polecat's equivalent is pre-rendered JSON — that shape exists to feed
its ASP.NET Core streaming result, and Fisher's JSON variant lives in `Fisher.AspNetCore` instead.

### The identity key is required, and that guard is what makes the rest honest

::: warning
Keyset pagination **requires a terminal identity key**. Without a total order, rows tied on the sort
key have no defined order between them and a seek boundary lands mid-tie — skipping some rows and
repeating others, silently, and only when there are ties. Fisher refuses rather than paging wrongly.
:::

### The seek predicate

Fisher emits the expanded OR-of-ANDs, not SQLite's row-value comparison:

```sql
(a < ?) or (a = ? and b > ?)
```

Row values are available since SQLite 3.15 and would be one comparison the planner could serve from a
composite index — but they only express a seek when **every key runs the same direction**, and mixed
direction is the common case (`OrderByDescending(x => x.Landed).ThenBy(x => x.Id)`). Special-casing
uniform orderings is an optimisation, not a correctness matter.

### The cursor format

`v1:` followed by base64 JSON, **byte-identical to Polecat's**, so a cursor is portable between the
stores.

::: tip
**Cursor values are typed on decode by the query's ordering members, never by the cursor.** The
payload carries no type information, so a hand-edited cursor can change values but not what they are
read as — and every value then binds as a parameter.
:::

::: tip
A cursor whose key does not bind to its ordering member is an `ArgumentException`, not the
`InvalidOperationException` `JsonElement` raises. The payload's *shape* was already checked; the
per-key bind was not, so one way of malforming a client-supplied cursor produced a 400 and another a
500.
:::

### Ordering keys are read off the row

A key can be any locator, including one no member of the result exposes. The keys are appended to the
select list **after** the document's own columns, which is safe because the storage selector resolves
from fixed positions starting at 0.

## Paging with a join

`ToPagedListAsync` works over a [join](/documents/querying/linq/joins); `ToCursorPageAsync` does not
and is refused by name.

::: tip
A count over a paged join has to carry the joins and the outer alias into the wrapping subquery, or
it counts the outer table instead. Fisher does; it is worth knowing because a qualified locator inside
a subquery that dropped them errors with `no such column` — which is the one mercy of qualifying.
:::

## Paging in an HTTP endpoint

`Fisher.AspNetCore` has streaming versions of both that skip the serializer round trip:

```cs
app.MapGet("/orders", (IQuerySession session, int page) =>
    session.Query<Order>().OrderBy(x => x.PlacedAt).StreamPaged(page, 20));

app.MapGet("/feed", (IQuerySession session, string? cursor) =>
    session.Query<Order>().OrderByDescending(x => x.PlacedAt).ThenBy(x => x.Id)
        .StreamPagedByCursor(cursor, 20));
```

The total comes back as a **header** rather than an envelope. See
[ASP.NET Core Integration](/documents/aspnetcore).
