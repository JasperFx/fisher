# ASP.NET Core Integration

```shell
dotnet add package Fisher.AspNetCore
```

Streaming `IResult` types, ETag handling, event stream results and a daemon health check.

## The streaming results

```cs
app.MapGet("/user/{id:guid}", (Guid id, IQuerySession session) =>
    session.Query<User>().Where(x => x.Id == id).StreamOne());

app.MapGet("/users", (IQuerySession session) =>
    session.Query<User>().Where(x => x.Internal).StreamMany());

app.MapGet("/users/page/{page:int}", (int page, IQuerySession session) =>
    session.Query<User>().OrderBy(x => x.LastName).StreamPaged(page, 20));

app.MapGet("/feed", (string? cursor, IQuerySession session) =>
    session.Query<Order>()
        .OrderByDescending(x => x.PlacedAt).ThenBy(x => x.Id)
        .StreamPagedByCursor(cursor, 20));
```

Every one of these is `new StreamX(...)`, so a handler that wants to configure a result — a different
status, no ETag — constructs it directly.

## Why they are worth more here

They exist to skip a deserialize-then-reserialize round trip. On Marten and Polecat that saves CPU
for data that already crossed a network from a database server, so it is a fraction of the total cost.
**Fisher's database is the web process**, so the round trip *is* the cost — an endpoint reading a
document and returning it goes from "parse JSON, build an object, serialize an object" to "copy
bytes".

::: tip
`StreamMany` uses Fisher's own [JSON array read](/documents/querying/query-json), which concatenates
the stored `data` columns in .NET. Polecat's equivalent materializes objects and calls
`JsonSerializer.SerializeToUtf8Bytes`, which throws away the saving the type exists for.
:::

The bytes are exactly what the serializer wrote. Neither sibling can promise that — `jsonb` normalises
whitespace and key order, and `nvarchar` needs an encoding decision.

## Paging

`StreamPaged`'s total comes back as a **header**, not an envelope — and it is a second statement, not
`count(*) over ()`. A window function returns no row for a page past the end, which is exactly when a
pager most needs the total.

## Event stream results

```cs
app.MapGet("/stream/{id:guid}", (Guid id, IQuerySession session) =>
    session.StreamEventState(id));

app.MapGet("/stream/{id:guid}/events", (Guid id, IQuerySession session) =>
    session.StreamEvents(id, fromVersion: 5));

app.MapGet("/order/{id:guid}", (Guid id, IQuerySession session) =>
    session.StreamAggregate<Order>(id));
```

::: tip
**`StreamAggregate` reads the ETag before folding.** A stream's version moves if and only if an event
was appended, so a matching `If-None-Match` answers `304` having read one row of `fi_streams` and
folded nothing. For a long stream that is the whole value.
:::

::: tip
`IQuerySession` gained `Events`, so an endpoint or a report taking a read session can read streams.
Marten and Polecat narrow theirs to a read-only event surface; Fisher does not, for the same reason
[`QuerySession()` is a convention rather than a guarantee](/documents/sessions).
:::

## ETags

`StreamOne` emits an ETag and honours `If-None-Match`, returning `304` with no body.

::: tip
It serves a [numeric-revisioned](/documents/concurrency#numeric-revisions) document from its
`revision`, not only a Guid-versioned one from `guid_version`. A revision validates a cached
representation exactly as well as a Guid version — refusing one of them left the whole revisioned half
of a store unable to emit an ETag, with a message recommending the wrong setting.

There are **two read methods where Marten widened one**, because the flavors are two physical columns
here rather than one column read at either width. `queryable.VersionSourceFor<T>()` is how a caller
asks which applies.
:::

Helpers, if you are writing your own result:

```cs
ETagHelpers.Format(guidVersion);
ETagHelpers.Format(revision);
ETagHelpers.IfNoneMatchMatches(httpContext, etag);
```

## Health check

```cs
builder.Services.AddHealthChecks()
    .AddFisherHighWaterHealthCheck(
        staleThreshold: TimeSpan.FromSeconds(30),
        minimumGap: 1);
```

::: tip
Keep `staleThreshold` comfortably above `EventStoreOptions.HighWaterLivenessInterval` (five seconds by
default), or a healthy agent reports unhealthy between two of its own touches.
:::

**This check has an argument of its own**: Fisher's daemon *warns* rather than refuses when the
journal mode is not WAL, because that misconfiguration presents as a slow projection. This is how an
operator finds out the warning mattered, and its stuck-mark message says so.

What it reads is the **poll-cycle age**, with the gap heuristic as the secondary signal.

::: warning
The extended progression `heartbeat` column is **not** an option and cannot be used for this. JasperFx
returns early for the high-water shard, so nothing ever writes it for that row — a health check
reading it looks like it has a signal it does not have.
:::

`minimumGap` defaults to 1 because the daemon is always at least one event behind a writer that has
just committed.

## What is not here

::: tip
MCP endpoints are deliberately not ported. That surface is moving upstream, and porting it
speculatively would mean maintaining a copy of something about to change.
:::

## Registration

Nothing to register beyond `AddFisher()`. The results take an `IQuerySession` or an `IQueryable<T>`
you already have.
