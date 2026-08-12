# Querying for Raw JSON

Sometimes the document you read is going straight into an HTTP response. These reads skip the
deserialize-then-reserialize round trip.

**The saving is larger here than on either sibling, and the reason is structural.** On Marten and
Polecat this saves CPU for data that already crossed a network from a database server, so it is a
fraction of the total cost. In Fisher the database *is* the caller's process, so the round trip **is**
the cost.

```cs
// One document
string? json = await session.LoadJsonAsync<User>(id);

// A whole query
string json = await session.Query<User>()
    .Where(x => x.Internal)
    .ToJsonArrayAsync();

// The first row, with its version — for an ETag
DocumentJsonWithVersion? result = await session.Query<User>()
    .Where(x => x.Id == id)
    .ToJsonFirstWithVersionAsync();

// Or with its numeric revision, for a revisioned type
DocumentJsonWithRevision? result = await session.Query<User>()
    .Where(x => x.Id == id)
    .ToJsonFirstWithRevisionAsync();

// Straight to a stream
await session.Query<User>().StreamJsonArrayAsync(response.Body);
```

## Byte-exactness

`data` is TEXT holding **exactly what System.Text.Json wrote** — no whitespace normalisation, no key
reordering, no encoding decision. PostgreSQL's `jsonb` normalises and SQL Server's `nvarchar` needs
the encoding decided, so neither sibling can promise this.

::: warning
[Patching](/documents/partial-updates-patching) breaks it. `json_set` re-renders the document, so a
patched row is no longer identical to what the serializer would have written, and a new or renamed
key lands at the end.
:::

## How the array is built

**Concatenated in .NET, not with `json_group_array`.** That function re-parses and re-renders every
document — discarding the whole saving and reordering object keys on the way.

## The filters still apply

Every one of these goes through the ordinary statement path, so the tenant, soft-delete and hierarchy
filters apply without being restated. A JSON read composing its own `select data from …` would be one
more caller having to remember all three.

## Reading a version alongside the JSON

`ToJsonFirstWithVersionAsync` asks for `guid_version` explicitly — a query-only read normally drops
it, having no version tracker to feed. It is **refused by name** for a type without optimistic
concurrency, since the column does not exist, rather than surfacing as `no such column`.

`ToJsonFirstWithRevisionAsync` is the same read against the `revision` column, for a type using
[numeric revisions](/documents/concurrency#numeric-revisions).

::: tip
**Two methods, where Marten widened one.** The two concurrency flavors are two physical columns here
rather than one column read at either width. `queryable.VersionSourceFor<T>()` is how a caller asks
which applies — and no fail-fast guard against a type having both was needed, because that pair is
already refused at configuration time.
:::

There is also a cursor-paged JSON read, `ToJsonCursorPageAsync`, which shares its preparation with
the typed [`ToCursorPageAsync`](/documents/querying/linq/paging) rather than repeating it — the
ordering validation, the decode and the seek predicate are subtle enough that two copies would drift.

## StreamJsonArrayAsync materializes first

::: warning
This one buffers before writing, deliberately. A retried `SQLITE_BUSY` re-executes the whole delegate,
so streaming a live reader to the caller's stream would resume against a disposed reader *and* a
half-written response body.

This is the one place the retry semantics and the streaming goal genuinely conflict. Buffering is the
resolution, because the saving being chased is the serializer round trip rather than the buffer.
:::

## In an HTTP endpoint

`Fisher.AspNetCore` wraps all of this in `IResult` types that write the stored bytes directly:

```cs
app.MapGet("/user/{id:guid}", (Guid id, IQuerySession session) =>
    session.Query<User>().Where(x => x.Id == id).StreamOne());

app.MapGet("/users", (IQuerySession session) =>
    session.Query<User>().StreamMany());
```

See [ASP.NET Core Integration](/documents/aspnetcore).

## What is refused

JSON reads are **refused after a [join](/documents/querying/linq/joins)**, by name. A joined row is
not one document's stored bytes.
