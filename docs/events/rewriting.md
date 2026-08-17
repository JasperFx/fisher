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

GDPR-style erasure: rewriting protected information out of events already stored.

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

### The two overloads do not have the same reach

::: warning
That falls out of the type system, so it is worth stating plainly:

- The **`Action`** overload tests `@event is IEvent<T>`, and `IEvent<out T>` is covariant — so a rule
  registered against an **interface or base class reaches every event body implementing it**.
- The **`Func`** overload has to *assign* the replacement back, and only the closed generic event type
  exposes a setter — so it matches its **exact type only**.

A `record` needs the `Func` overload, because a `with` expression makes a new instance. A
hierarchy-wide rule therefore has to be the mutating one.
:::

### Selecting the events

```cs
masking.IncludeStream(streamId);
masking.IncludeStream(streamId, e => e.Version > 5);   // in-memory filter
masking.IncludeEvents(e => e.Timestamp < cutoff);      // translated to SQL
```

::: tip
**`IncludeEvents` is the only selector translated to SQL.** The two `IncludeStream` filter overloads
take a plain delegate and are applied in memory to an already-fetched stream; `IncludeEvents` takes an
expression. That asymmetry is the shared interface's, not Fisher's — the parameter types say so.
:::

### The rules

- **The whole batch runs in one session**, so an erasure is either done or not done. A partial one is
  a compliance answer that is neither.
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

### There is no version guard, and that is not an oversight

::: tip
The fetch is outside the write transaction, and that is safe. Compacting only touches events at or
below a version it observed, and an append only adds *above* one — so the two cannot overlap. Two
concurrent compactions of the same stream either write the same snapshot to the same row, or find the
target already gone and update nothing.

There is no lost update to prevent, so a version guard would be theatre.
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
