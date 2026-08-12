# Fisher as Event Store

Fisher provides a full event store on SQLite, following the same patterns as Marten's and Polecat's —
and implementing the same JasperFx abstractions, so a projection ports between them unchanged.

## Key Concepts

- **Event** — an immutable record of something that happened
- **Stream** — a sequence of events for one aggregate or entity
- **Aggregate** — a domain object whose state is derived by replaying events
- **Projection** — a read model built from events, inline, live or asynchronously

## Event Store Tables

| Table | Purpose |
| :--- | :--- |
| `fi_events` | Every event: sequence, stream, version, type, JSON data |
| `fi_streams` | Stream metadata: version, type, timestamps, archived flag |
| `fi_event_progression` | Async daemon progress per shard |
| `fi_dead_letters` | Events a shard could not apply and was told to skip |
| `fi_event_tag_<suffix>` | One per registered [DCB tag](/events/dcb) type |
| `fi_natural_key_<alias>` | One per [natural key](/events/natural-keys) definition |

See [Event Storage](/events/storage).

## Stream Identity

```cs
opts.Events.StreamIdentity = StreamIdentity.AsGuid;    // the default
opts.Events.StreamIdentity = StreamIdentity.AsString;
```

## Quick Example

```cs
public record InvoiceCreated(decimal Amount, string Customer);
public record InvoicePaid(decimal AmountPaid, DateTimeOffset PaidAt);

await using var session = store.LightweightSession();

var streamId = session.Events.StartStream<Invoice>(
    new InvoiceCreated(100m, "Acme Corp"),
    new InvoicePaid(100m, DateTimeOffset.UtcNow));

await session.SaveChangesAsync();

var invoice = await session.Events.AggregateStreamAsync<Invoice>(streamId);
```

See the [Quick Start](/events/quickstart) for a complete walkthrough.

## Projection Strategies

| Strategy | When applied | Use case |
| :--- | :--- | :--- |
| [Inline](/events/projections/inline) | The same transaction as the append | Strong consistency |
| [Live](/events/projections/live-aggregates) | On demand, by replay | Occasional reads, always current |
| [Async](/events/projections/async-daemon) | A background daemon | Eventually consistent read models |

## What is here

| Topic | |
| :--- | :--- |
| [Appending Events](/events/appending) | `StartStream`, `Append`, concurrency, `FetchForWriting` |
| [Querying Events](/events/querying) | Stream reads, event metadata queries, body queries |
| [Metadata](/events/metadata) | Correlation, causation, user name, headers |
| [Archiving](/events/archiving) | Archive, unarchive, tombstone |
| [Snapshots](/events/snapshots) | `Snapshot<T>` across all three lifecycles |
| [Natural Keys](/events/natural-keys) | Addressing a stream by a business identifier |
| [DCB](/events/dcb) | Tags, tagged appends, consistency boundaries |
| [Rewriting Events](/events/rewriting) | Overwrite, replace, masking, stream compacting |
| [Projections](/events/projections/) | Every shape, every lifecycle |
| [Subscriptions](/events/subscriptions) | Arbitrary code over each range of events |

## Two SQLite properties worth knowing up front

**Committed sequence numbers are contiguous.** One writer per file plus `BEGIN IMMEDIATE` means a
transaction's sequences fully commit before the next writer allocates any, and a rollback returns the
number. So the async daemon's high-water mark simply *is* `max(seq_id)` — Marten and Polecat must
distinguish the highest sequence *issued* from the highest safe to *read*, and Fisher has no such
distinction to draw.

**`fi_events.seq_id` is `AUTOINCREMENT`, and that is load-bearing rather than decorative.** A bare
`INTEGER PRIMARY KEY` aliases the rowid, which SQLite **reuses** after a delete — and a reused
sequence below the daemon's high-water mark is an event no async projection would ever see. That is
what makes [event deletion](/events/rewriting) safe at all.
