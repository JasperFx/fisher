# Dynamic Consistency Boundary

DCB lets a consistency boundary be **a set of events matching a query**, rather than one stream. It is
the answer to the modelling problem where the thing you must be consistent about does not line up with
the aggregate you chose.

## Registering a tag type

```cs
opts.Events.RegisterTagType<BasketId>("basket");     // fi_event_tag_basket
opts.Events.RegisterTagType<CustomerId>();           // suffix from the type name
```

Each registered tag type gets a table.

## Tagging events

Tag the events an append produces:

```cs
session.Events.AssignTagWhere(e => e.EventTypeName == "ItemAdded", basketId);
session.Events.Append(streamId, new ItemAdded(sku, quantity));
await session.SaveChangesAsync();
```

The predicate goes through the same parser `Query<T>()` uses, over `IEvent` members resolved to
`fi_events` columns rather than `json_extract` paths.

::: tip
`IEvent.Timestamp` permits **range comparison** where a document's `DateTimeOffset` member does not —
same CLR type, but `fi_events.timestamp` is Fisher's fixed-width UTC format, chosen precisely so a
string comparison is an instant comparison.
:::

## Querying by tags

```cs
var events = await session.Events.QueryByTagsAsync(query);
var exists = await session.Events.EventsExistAsync(query);
var basket = await session.Events.AggregateByTagsAsync<Basket>(query);
```

## Writing against a boundary

```cs
var boundary = await session.Events.FetchForWritingByTags<Basket>(query);

if (boundary.Aggregate?.Items.Count < 20)
{
    boundary.AppendOne(new ItemAdded(sku, quantity));
}

await session.SaveChangesAsync();
```

The boundary records the highest sequence it saw. At commit, Fisher checks **inside the write
transaction, before anything is written**, that no later event matches the query — and fails the
commit if one does.

::: tip
Checking *after* would be checking against the session's own appends. Checking *outside* the
transaction would prove nothing, because `BEGIN IMMEDIATE` is what holds the write lock.
:::

::: tip
A boundary over an **empty result still enforces consistency**: the last-seen sequence is 0, and any
later matching event exceeds it.
:::

## Identity-less boundary aggregates

An aggregate reached only through a tag boundary is keyed to no stream, so requiring it to carry a
`Guid Id` would be asking for a member the model has no use for. Mark it `[BoundaryAggregate]` (from
`JasperFx.Events.Aggregation`) and leave the identity off:

<!-- snippet: sample_dcb_boundary_aggregate -->
<a id='snippet-sample_dcb_boundary_aggregate'></a>
```cs
[BoundaryAggregate]
public partial class ShowSeating
{
    public HashSet<string> Reserved { get; } = [];

    public void Apply(SeatReserved e) => Reserved.Add(e.Seat);

    public void Apply(SeatReleased e) => Reserved.Remove(e.Seat);
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/dcb_samples.cs#L17-L27' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_dcb_boundary_aggregate' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Keep the type `partial`: the marker is what makes JasperFx's source generator emit a dispatcher for
it, and the generator attaches that to the type. The attribute must sit on the aggregate **in its own
defining assembly** — that is the compilation the evolver is emitted into, and the assembly the
runtime scans when resolving it.

::: warning
The marker is the whole opt-in, and an identity-less aggregate **without** it is still refused. A bare
no-`Id` aggregate is far more often a forgotten identity than a deliberate boundary aggregate, so the
generator emits nothing for one and Fisher declines to resolve it — deliberately, rather than
inventing an identity that would leave the dispatcher unmatched later.
:::

::: tip
An aggregate that already has an `Id` needs no marker and is unaffected. `[BoundaryAggregate]` is only
for the identity-less case, and the `string` identity it implies is vestigial — nothing on the DCB
path reads it.
:::

::: tip
This bites late without the marker. `FetchForWritingByTags` only folds an aggregate when the query
**finds events**, so a boundary over an empty result — the ordinary "this must not exist yet"
assertion — works either way, and the failure arrives the first time the boundary actually matches
something.
:::

::: warning
`[BoundaryAggregate]` is a JasperFx marker rather than a Fisher one, so a model carrying it is
portable in *source*. It is not yet portable in behaviour: as of Polecat 5.20.0 an identity-less
boundary aggregate still fails there, from its own document-identity resolution, despite Polecat's
DCB page documenting the marker. See [polecat#521](https://github.com/JasperFx/polecat/issues/521).
:::

## How tag rows are stored

One `fi_event_tag_<suffix>` table per tag type, with a composite primary key **leading with `value`**.
That key is load-bearing twice over:

- a tag query filters on `value`, so leading with it makes the lookup a range scan;
- it is what lets both the append path and `AssignTagWhere` write `on conflict do nothing` instead of
  reading first — which is where **idempotency** comes from.

### Tags are written after the batch, inside its transaction

A tag row is keyed by the `seq_id` SQLite assigns on insert, which Fisher only learns from the
[trailing sequence read-back](/events/appending#how-the-append-works) — so there is nothing to write
until the appends postprocess.

::: warning
Committing tags separately would leave an event visible but untagged, and to a tag query that is
indistinguishable from an event that was never tagged.
:::

### The query shape

Each condition becomes a `seq_id in (select seq_id from <tag table> where value = ?)` subselect, OR'd
together.

::: tip
**Subselects rather than joins**, because joining several tag tables multiplies rows when one event
carries two matching tags — and the caller expects each event once.
:::

Ordering is by `seq_id`, since a tag query spans streams and version is not a global order.

### Guid tag values

Bound as lowercase canonical text, the same trap as everywhere else. Bound any other way, every lookup
returns nothing.

## Batched DCB reads

```cs
var batch = session.Events.CreateBatchQuery();

var exists = batch.EventsExist(queryA);
var boundary = batch.FetchForWritingByTags<Basket>(queryB);

await batch.Execute();
```

The batch is where DCB is most useful: several boundaries opened against **one coherent view**, since
the reads run back to back on one connection with nothing interleaved.

::: warning
It is not a performance feature on Fisher. See [Batched Queries](/documents/querying/batched-queries)
for why, and for the failure-handling contract.
:::

## Tags and rewriting

- **Replacing an event deletes its tag rows.** A tag describes the event that was *appended*, so
  carrying it over would let a tag query return the replacement as though it were the tagged event.
- **Deleting events clears tag rows first.** `fi_event_tag_*` has a real foreign key to
  `fi_events(seq_id)` and enforcement is on, so the other order fails with `FOREIGN KEY constraint
  failed`.
- **[Compacting](/events/rewriting#stream-compacting) removes every compacted event's tags**, including
  the replaced one. Keeping the last event's tag while deleting the rest is the one outcome that is
  neither "the stream is still tagged" nor "the tagged events are gone".

## Raised events from projections

An [event-emitting async projection](/events/projections/async-daemon#event-emitting-projections)
routes its raised events through Fisher's own append operation precisely so they get the sequence
read-back — queuing bare per-event operations would silently make raised events **untaggable**.
