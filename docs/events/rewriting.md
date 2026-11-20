# Rewriting Events

Events are immutable in principle. In practice three things force a rewrite: a bug that wrote the
wrong body, a legal obligation to erase personal data, and a stream that has grown too long to replay.
Fisher supports all three, and is explicit about what each one cannot do.

## The three operations

| Operation | Rewrites |
| :--- | :--- |
| **Overwrite** | What an event *says* — `data`, plus headers. Stream, version, sequence, type untouched. |
| **Replace** | What an event *is* — `data`, `type`, `dotnet_type` and a fresh `id`. Stream, version and sequence stay. |
| **Delete** | Removes rows by sequence. Internal — reached through a higher-level operation. |

```cs
session.Events.OverwriteEvent(sequence, correctedBody);
session.Events.CompletelyReplaceEvent(sequence, replacementEvent);
await session.SaveChangesAsync();
```

All three queue onto the session, so a rewrite commits in the same transaction as everything else —
which is what lets masking rewrite a batch atomically.

### Four decisions in them

- **The row is matched by `seq_id`, never by `id`.** The sequence is the primary key; `id` has no
  index, so matching on it would turn every rewrite into a table scan.
- **Replace does not move the timestamp**, where Polecat's does. `fi_events.timestamp` is what the
  timestamp-bounded stream read and the daemon's timestamp floor both read, and both assume it rises
  with the sequence. Moving one row's timestamp forward puts the column out of order, and a bounded
  read then returns a set that is neither the old answer nor the new one.
- **Replace deletes the row's [tag rows](/events/dcb).** A tag describes the event that was appended.
- **Delete clears tag rows before events**, because of the foreign key. Dead letters are deliberately
  left alone — they have no foreign key precisely so they outlive the events they describe.

::: warning
**Deleting is safe only because `seq_id` is `AUTOINCREMENT`.** A bare `INTEGER PRIMARY KEY` aliases
the rowid, which SQLite reuses after a delete, and a reused sequence below the daemon's high-water
mark is an event no async projection would ever see.
:::

::: danger
**None of this reaches an async projection that has already run.** The high-water mark is a sequence
and a rewrite does not move it, so a shard past the event never reads the new body — and a projection
holding state derived from the old one stays wrong until it is rebuilt.

Marten behaves the same way. Anything built on these has to say so rather than leave it implicit.
:::

A [binary event](/events/storage#binary-event-bodies) is refused by both rewrite operations, by name.

## Event data masking

Rewriting protected information out of events already stored.

::: warning Masking is not GDPR erasure
Masking changes the live event bodies only. Projections already built from them, backups, and the
file's free pages keep the old values. The Critter Stack's answer to an erasure request is
crypto-shredding with [Tayra](https://tayra.dev). See [Personal Data and GDPR Erasure](/events/personal-data).
:::

```cs
await store.Advanced.ApplyEventDataMaskingAsync(masking =>
{
    masking.IncludeStream(streamId);
    masking.IncludeEvents(e => e.Timestamp < cutoff);
});
```

Register the rules on the store:

```cs
// A mutating rule — reaches a whole hierarchy
opts.Events.AddMaskingRuleForProtectedInformation<IHasCustomerName>(e => e.CustomerName = "***");

// A replacing rule — for records
opts.Events.AddMaskingRuleForProtectedInformation<OrderPlaced>(e => e with { Customer = "***" });
```

### Both overloads reach a hierarchy

`IEvent<out T>` is covariant, so a rule registered against an **interface or base class reaches every
event body implementing it**, through either overload. The `Action` overload mutates the body in
place; the `Func` overload replaces it.

::: warning
A `Func` rule over an interface or base class has to return something the event can still hold,
normally an instance of the same runtime type. Anything else fails the masking pass rather than leaving
the event unmasked:

```cs
options.Events.AddMaskingRuleForProtectedInformation<IPersonalData>(
    x => x is CustomerRegistered c ? c with { Email = "REDACTED" } : x);
```

A `record` needs the `Func` overload, because a `with` expression makes a new instance.
:::

The rule list is JasperFx's `EventRegistry`'s, which Fisher's event graph derives from, so a rule
registered through store-agnostic code holding the graph as an `EventRegistry` is applied the same
way.

### Selecting the events

```cs
masking.IncludeStream(streamId);
masking.IncludeStream(streamId, e => e.Version > 5);   // in-memory filter
masking.IncludeEvents(e => e.Timestamp < cutoff);      // translated to SQL
```

Every selector reaches [archived events](/events/archiving#reading-archived-events), which the default
reads leave out, so an archived stream is erased like any other. `IncludeEvents` can still narrow them
itself with `e => !e.IsArchived`.

::: tip
**`IncludeEvents` is the only selector translated to SQL.** The two `IncludeStream` filter overloads
take a plain delegate and are applied in memory to an already-fetched stream; `IncludeEvents` takes an
expression. That asymmetry is the shared interface's, not Fisher's — the parameter types say so.
:::

### The rules

- **The whole batch runs in one session**, so a masking pass is either done or not done. A partial
  one would leave you unable to say which events were masked.
- **An event is rewritten only when a rule matched it**, and added headers follow the same gate — so
  `AddHeader` marks the events that were *masked*, not the events that were looked at.
- **The same event reached by two sources is masked once**, deduplicated by sequence.
- **A batch naming no stream and no filter throws**, rather than masking everything.

::: danger
**Masking does not reach anything derived from the events.** A snapshot, document or flat table that
already folded the unmasked body still holds the protected information until that projection is
rebuilt. Marten is the same.

That includes a baseline held by the
[`FetchForWriting` aggregate cache](/events/appending#caching-the-aggregate-between-fetches), which is
node-local and therefore not reachable from the process doing the masking at all. Leave an aggregate
whose history you mask unenrolled.
:::

## Rewriting stored events in bulk

`IEventStore.RewriteStoredEventsAsync` reads the stored events a rewrite selects, puts each one through
named transforms, and writes it back the way an append would store it today. It is the seam a Wolverine job
drives, and it suits a fix that has to reach every event of a type rather than a few known sequences.

Register the transforms on the store:

<!-- snippet: sample_register_a_rewrite_transform -->
<a id='snippet-sample_register_a_rewrite_transform'></a>
```cs
options.Events.AddRewriteTransform<CustomerNoted>("trim-notes",
    x => x with { Note = x.Note.Trim() });
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/rewriting_samples.cs#L18-L21' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_register_a_rewrite_transform' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Then run a rewrite against each database:

<!-- snippet: sample_rewrite_stored_events -->
<a id='snippet-sample_rewrite_stored_events'></a>
```cs
var definition = new EventRewriteDefinition
{
    Name = "trim-customer-notes",
    Transforms = ["trim-notes"],
    BatchSize = 500
};

foreach (var database in await store.AllDatabases())
{
    // The first request stores each tenant's ceiling. A later one resumes after the stored cursor.
    var run = await EventRewriteRun.FromMarksAsync(database, definition, token);
    var totals = await store.RewriteStoredEventsAsync(run, token);
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/rewriting_samples.cs#L26-L40' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_rewrite_stored_events' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

What it does:

- The events selected are the types the definition lists, the types its transforms apply to, and the stored
  names an upcaster reads into either. `AllEventTypes` selects everything, and a row whose type the application
  no longer knows is left alone and counted.
- An upcast event is written back under its current type, and a binary event keeps its body in `data_binary`.
  A row that would be stored the same is not written.
- Each page and its cursor are read and written in one `BEGIN IMMEDIATE` transaction, kept in
  `fi_event_range_marks`. A run that stops resumes after its cursor, so no event goes through a transform twice,
  and events appended after the first request are past the ceiling and left alone.
- Appends wait while a page is transformed. `BatchSize` and `PauseBetweenBatches` are how you give the
  application room.
- `DryRun` counts what would change and writes nothing, not even the cursor.

Like the operations above, a rewrite does not reach projections, natural keys or tags already built from the
old events. Rebuild what depends on them.

## Stream compacting

Replace a stream's events with a single `Compacted<T>` event carrying the aggregate state.

```cs
await session.Events.CompactStreamAsync<Order>(streamId);

await session.Events.CompactStreamAsync<Order>(streamId, request =>
{
    request.Archiver = async events => await ArchiveSomewhereElse(events);
});
```

::: tip
**Reading it back needed nothing.** JasperFx's aggregator fast-forwards from a `Compacted<T>` before
folding, so a stream starting with a snapshot event starts from that state and applies only what
follows. Live aggregation, `FetchForWriting` and the daemon all inherit it.
:::

### How it works

- **The snapshot takes the last event's row**, so the stream's version does not move and the next
  append carries on from where it would have. The events below it are deleted.
- **An aggregate that folds to null leaves the stream alone.** A stream deleted by its own
  `ShouldDelete` has no state to snapshot, and writing an empty `Compacted<T>` would be worse than
  doing nothing.
- **Every compacted event's tag rows go**, including the replaced one.
- **An archived stream is compacted too**, and its snapshot row stays archived. Marten and Polecat read
  the stream through their default read, so compacting an archived stream does nothing on either.
- **It waits for async projections and subscriptions.** Compacting deletes events, so Fisher refuses to
  compact a stream while any asynchronous projection or subscription has not processed them yet.
  `CompactStreamAsync()` throws an `InvalidOperationException` naming the ones that are behind, and
  nothing is compacted; compact again once they caught up. One that has never run counts as behind. A
  single stream projection of the type you are compacting to is not checked, since it reads the
  `Compacted<T>` snapshot instead of the deleted events.

### There is no version guard, and that is not an oversight

::: tip
The fetch is outside the write transaction, and that is safe. Compacting only touches events at or
below a version it observed, and an append only adds *above* one — so the two cannot overlap. Two
concurrent compactions of the same stream either write the same snapshot to the same row, or find the
target already gone and update nothing.

There is no lost update to prevent, so a version guard would be theatre.
:::

### Compacting is not erasure

::: warning
Compacting deletes history, but the `Compacted<T>` snapshot carries the aggregate's state, including
any personal data in it. An `Archiver` copies the old events somewhere else on purpose. For a GDPR
erasure request, see [Personal Data and GDPR Erasure](/events/personal-data).
:::

### Compacting is one-way

::: danger
A projection rebuilt afterwards rebuilds **from the snapshot**, not from the history that produced it.
`Archiver` is the hook for copying the events somewhere first, and it runs before anything destructive
is queued.
:::

### From tooling

```cs
await eventStore.CompactStreamAsync(streamId);
```

The non-generic form on `IEventStore` **resolves the aggregate type from `fi_streams`**, where Polecat
throws at that level. The type is on the row, so declining for every stream would be a worse answer
than declining for the streams that genuinely record none — and the message that does decline names
the generic overload.

On a multi-tenanted store, pass the tenant the stream belongs to:

```cs
await eventStore.CompactStreamAsync(streamId, "north");
```

This is the action-side twin of `OpenReadOnlyEventStore(tenantId)`: a compaction policy that selected a
tenant's streams through that reader can then compact them. Both the stream-state read and the
compaction run in that tenant's scope — the `tenant_id` term under conjoined tenancy, the tenant's own
file under database-per-tenant. A tenant on a store with no tenant dimension is refused rather than
ignored, because the unscoped tables would compact whichever stream carries that id.
