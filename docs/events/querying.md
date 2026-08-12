# Querying Events

## Reading a stream

```cs
var events = await session.Events.FetchStreamAsync(streamId);
var events = await session.Events.FetchStreamAsync(streamId, version: 10);
var events = await session.Events.FetchStreamAsync(streamId, fromVersion: 5);
var events = await session.Events.FetchStreamAsync(streamId, timestamp: cutoff);

var state = await session.Events.FetchStreamStateAsync(streamId);
// state.Version, state.Created, state.LastTimestamp, state.AggregateType, state.IsArchived
```

## Loading one event

```cs
var @event = await session.Events.LoadAsync(eventId);
```

## Aggregating

```cs
var order = await session.Events.AggregateStreamAsync<Order>(streamId);
var order = await session.Events.AggregateStreamAsync<Order>(streamId, version: 10);
var order = await session.Events.AggregateStreamToLastKnownAsync<Order>(streamId);
```

See [Live Aggregations](/events/projections/live-aggregates).

## Unknown event types

::: tip
**Stream reads skip an unresolvable `dotnet_type` unconditionally**, so a deployment can still read
events it does not know about.

The [async daemon](/events/projections/async-daemon) must **not** do that — silently skipping one
would leave the projection permanently wrong — so it honours `SkipUnknownEvents` and otherwise throws.
Two different policies, each right for its caller.
:::

## Querying event metadata

`QueryEventsAsync` pages over `fi_events` filtering on the *row's* columns:

```cs
var page = await session.Events.QueryEventsAsync(new EventQuery
{
    StreamId = streamId.ToString(),
    EventTypes = ["OrderPlaced"],
    From = cutoff,
    CorrelationId = correlationId,
    PageNumber = 1,
    PageSize = 50
});

page.Events;
page.TotalCount;
```

Two things in it are load-bearing:

::: tip
**A `StreamId` filter is parsed and re-rendered under Guid identity.** `fi_events.stream_id` holds the
lowercase canonical form and SQLite's default collation is case-sensitive, so binding a caller's
uppercase Guid string directly would match nothing — and a monitoring tool would render an existing
stream as empty.
:::

::: tip
**The three metadata filters are gated on the options that create their columns.** `correlation_id`,
`causation_id` and `user_name` do not exist unless the matching `Enable*` option is on, so an ungated
filter would be `no such column` rather than an empty result. An unavailable filter is ignored, which
is what the query shape asks for and what Polecat does.
:::

The count is a **second statement**, not `count(*) over ()` — a window function returns no row at all
for a page past the end, and "page 9 of a 3-page result" is exactly when a tool most needs the real
total.

## Querying event bodies

`QueryEventsAsync` filters on metadata. To filter on what an event *says*, name its type:

```cs
var events = await session.Events.QueryEventDataAsync<OrderPlaced>(e => e.Total > 1000m);
```

An event body is a JSON document in a TEXT column, structurally identical to a stored document, so the
same member locators apply verbatim — including the
[`strftime` wrapper](/documents/querying/linq/operators#timestamps) for a timestamp *inside* a body.

::: tip
There is no `DocumentMapping` involved, and that is not laziness. Most event types have no identity
member, and asking for a mapping would *register* the event type as a document — giving it a table in
the next migration.
:::

::: warning
A body member called `Id` is **not** the event's own `id` column. That column is the event's identity,
so resolving to it would compare against the wrong column and return rows rather than an error.
:::

The type filter uses the short `type` alias, not `dotnet_type` — short and stable where the other is
assembly-qualified and brittle across a rename.

::: warning
**This is a scan**, and honestly so: there is no index over `fi_events.data`.
[Expression indexes](/documents/indexing/indexes) are the mechanism if one ever needs to be fast, and
they would apply here unchanged.
:::

A [binary event](/events/storage#binary-event-bodies) is refused by name — `data` is null for those
rows, so the query would match nothing and report that as an answer.

## The read-only event store

```cs
await using var events = store.OpenReadOnlyEventStore();
```

::: tip
`FisherReadOnlyEventStore` **owns its session lifetime** rather than capturing one — the one divergence
from Polecat here, and it is dialect-forced. Polecat returns `QuerySession().Events` directly, and
since the interface is not `IDisposable`, nothing ever disposes that session. A Fisher session caches
its connection for its whole lifetime, so the same shape would leak a pooled connection against a
single database file on every call — to a method whose caller is a polling monitoring tool.
:::

## Explorer reads

For a monitoring console:

```cs
var streams = await eventStore.GetRecentStreamsAsync(…);
var metadata = await eventStore.GetStreamMetadataAsync(streamId);
```

These are on the `IEventStore` tooling interface, implemented **explicitly** on `DocumentStore` — so
you cast to reach them, which is the point of implementing them explicitly. See
[Diagnostics](/diagnostics).

::: tip
Rows are **materialised inside** the resilience pipeline, not streamed out of it. A retried
`SQLITE_BUSY` re-executes the whole delegate, so yielding a live reader to the caller would let a
retry resume against a connection the previous attempt had already disposed.
:::
