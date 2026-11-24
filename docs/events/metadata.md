# Event Metadata

Every event carries `IEvent` metadata: its id, sequence, stream, version, timestamp, type and
`dotnet_type`. Four more are optional. They are off by default, as in Marten 10 (fisher#682,
marten#6144): each adds a value to every append. Turn them on for CritterWatch, whose event query filters on
correlation id, causation id and user name, and to link a Wolverine conversation to the events it appended.
Each one can be set on its own:

```cs
opts.Events.EnableCorrelationId = true;
opts.Events.EnableCausationId = true;
opts.Events.EnableUserName = true;
opts.Events.EnableHeaders = true;
```

Or all four in one call, the same set as Marten's
`Events.MetadataConfig.EnableAll()`. That includes the user name, so the session's `LastModifiedBy` is
stored on every appended event:

```cs
opts.Events.EnableAllEventMetadata();
```

::: warning
The `fi_events` columns exist whatever the flags say; the flags decide whether they are written and read.
They also gate the matching filters on [event queries](/events/querying#querying-event-metadata): a filter
on metadata the store does not write is ignored rather than answered from empty columns.
:::

## Where the values come from

The session, copied onto each event that does not already carry its own:

```cs
session.CorrelationId = "…";
session.CausationId = "…";
session.CurrentUserName = "jane";
session.SetHeader("region", "eu-west");

session.Events.Append(streamId, new OrderShipped(…));
await session.SaveChangesAsync();
```

::: tip
**The session seeds correlation and causation from `Activity.Current`** — `RootId` and `ParentId` — at
construction, so tracing context reaches events with no application code at all. An explicit
assignment afterwards wins.
:::

An event that already carries its own value keeps it.

## Reading it back

```cs
var events = await session.Events.FetchStreamAsync(streamId);

foreach (var e in events)
{
    e.Id; e.Sequence; e.StreamId; e.Version; e.Timestamp;
    e.EventType; e.EventTypeName; e.DotNetTypeName;
    e.CorrelationId; e.CausationId; e.Headers;
    e.TenantId; e.IsArchived;
}
```

## Filtering on it

```cs
await session.Events.QueryEventsAsync(new EventQuery { CorrelationId = id, … });
```

And in a [tag predicate](/events/dcb):

```cs
session.Events.AssignTagWhere<BasketTag>(tag, e => e.Timestamp > cutoff && e.EventTypeName == "ItemAdded");
```

::: tip
`IEvent.Timestamp` permits **range comparison** where a document's `DateTimeOffset` member does not.
Same CLR type, but `fi_events.timestamp` is Fisher's fixed-width UTC format, chosen precisely so that
a string comparison *is* an instant comparison.
:::

## A note on how metadata is applied

Fisher applies session metadata itself rather than going through JasperFx's
`StreamAction.PrepareEvents`, and the reason is worth recording because it looks like duplication.

In Quick mode `PrepareEvents` numbers events only when the expected version is already set, because
Marten and Polecat let the database assign versions while Fisher
[numbers them client-side](/events/appending#how-the-append-works). Pre-setting that value to make it
number them would make the optimistic-concurrency check *inside the same method* compare the value
against itself and pass unconditionally.

**Keeping version assignment and metadata application apart is what keeps the guard real.** The cost
is that a new metadata field in JasperFx will not reach Fisher's events until Fisher's own method
learns about it.

## Document metadata

Documents have their own, including the same four session-sourced values. See
[Fisher Metadata](/documents/metadata) — enabling them there is what lets one request be identified
from either an event or a document it wrote.
