# Event Storage

## fi_events

| Column | Type | Notes |
| :--- | :--- | :--- |
| `seq_id` | INTEGER PRIMARY KEY **AUTOINCREMENT** | The global order |
| `id` | TEXT | The event's own identity |
| `stream_id` | TEXT / INTEGER | Guid or string stream identity |
| `version` | INTEGER | Position within the stream |
| `data` | TEXT | The JSON body |
| `data_binary` | BLOB | Present only when a [binary serializer](#binary-event-bodies) is configured |
| `type` | TEXT | The event's short alias |
| `dotnet_type` | TEXT | Assembly-qualified type name |
| `timestamp` | TEXT | Fixed-width UTC ISO-8601 |
| `tenant_id` | TEXT | Under conjoined tenancy |
| `correlation_id`, `causation_id`, `user_name`, `headers` | TEXT | Each behind its `Enable*` option |

::: warning
**`AUTOINCREMENT` on `seq_id` is load-bearing, not decorative.** A bare `INTEGER PRIMARY KEY` aliases
the rowid, which SQLite **reuses** after a delete — and a reused `seq_id` below the daemon's
high-water mark would silently hide events from every async projection. It is what makes
[event deletion and compacting](/events/rewriting) safe at all.
:::

## fi_streams

| Column | Notes |
| :--- | :--- |
| `id` | The stream identity |
| `type` | The aggregate type name |
| `version` | The current version |
| `timestamp`, `created` | Fixed-width UTC ISO-8601 |
| `is_archived` | INTEGER 0/1 |
| `tenant_id` | Under conjoined tenancy |

::: tip
Recent-stream ordering is `order by timestamp desc` over TEXT — a string sort, correct only while the
timestamp format stays fixed-width, UTC and millisecond-precision. A format with a variable-width
offset or no sub-second component would silently mis-order streams written in the same second.
:::

## Sequence numbers are contiguous

One writer per file plus `BEGIN IMMEDIATE` means a transaction's sequences fully commit before the
next writer allocates any, and a rollback returns the number (`sqlite_sequence` is an ordinary table
and rolls back with it).

So the async daemon's high-water mark **is** `max(seq_id)`. Marten and Polecat must distinguish the
highest sequence *issued* from the highest safe to *read*, because a PostgreSQL sequence or a SQL
Server IDENTITY hands out numbers outside the transaction — a writer can hold 7 uncommitted while 8
commits ahead of it.

::: warning
If you are extending Fisher: **do not reintroduce gap-skipping.** It would guard a state that cannot
occur.
:::

## Statistics

```cs
var stats = await store.Advanced.FetchEventStoreStatisticsAsync();

stats.EventCount;
stats.StreamCount;
stats.EventSequenceNumber;
```

::: tip
**There are three fields rather than two, and the third is the point.** `EventSequenceNumber` can
exceed `EventCount`, because archiving, compacting or deleting events leaves the sequence where it
was — SQLite never reuses an `AUTOINCREMENT` value it handed out. The gap between the two numbers is
the count of events that once existed and no longer do.
:::

`sqlite_sequence` has no row until the first `AUTOINCREMENT` insert, so the read is a `coalesce` and
an untouched store reports 0 rather than throwing.

## Row readers

Two types own the canonical SELECT projection and lock the column order for events and streams
respectively. Adding or renaming a column means changing those files and only those files.

Every conversion in them is **explicit** — `Guid.Parse`, a timestamp parse, `GetInt64(..) != 0` —
rather than `GetGuid` / `GetFieldValue<DateTimeOffset>` / `GetBoolean`. The write path converts
explicitly on the way in, so reading through a provider convenience method would leave the round trip
depending on Microsoft.Data.Sqlite's coercion rules instead of Fisher's own storage decisions —
asymmetry that breaks quietly under a provider upgrade.

## Binary event bodies

An event body can be a BLOB rather than JSON text:

```cs
opts.Events.BinarySerializer = new MyBinarySerializer();
```

```cs
[BinaryEvent]
public record SensorReadings(float[] Samples);
```

**Worth more here than the same feature is on Marten**, and for a structural reason: Fisher is
embedded, so the store's disk footprint *is* the application's — and SQLite has no `jsonb`. Where
PostgreSQL keeps a compact binary form for free, Fisher stores the literal JSON text of every event
forever, property names included.

The decisions in it:

- **A separate nullable BLOB column, not BLOBs mixed into `data`.** SQLite would tolerate the mixture,
  since affinity is a preference rather than a constraint — but then `typeof(data)` is the only way to
  tell an encoding apart, and `json_extract` over the column silently stops meaning anything for the
  rows that are binary.
- **The column exists only when a serializer is configured**, and `data` becomes nullable at the same
  moment — so a store that will never hold a binary event keeps the constraint it had. Appending a
  binary event to a store created without a serializer is refused **by name**, rather than failing on
  `data`'s NOT NULL constraint.
- **Which column a row's body is in is read off the resolved event type**, never off a null check. A
  genuinely null body would pass a null check too.
- **`data_binary` is composed last in the SELECT** and gets the last ordinal, so every ordinal above
  it is unmoved whether or not the store has one.

A stream can mix the two encodings freely. Everything that reads the row's *columns* — stream reads,
the daemon's loader, DCB tag queries, event metadata filters — is unaffected, which is why the daemon
needed no change at all.

::: warning
Two things **refuse** a binary event by name, and both would otherwise corrupt data or lie:

- The [rewrite operations](/events/rewriting) write the JSON `data` column. Against a binary row that
  would leave a JSON body *and* a BLOB body, and every reader resolves by event type — so the JSON
  would be invisible and the row quietly wrong.
- [`QueryEventDataAsync<T>`](/events/querying#querying-event-bodies) reads `data`, which is null for
  those rows, and `json_extract(null, …)` is null — it would match nothing and report that as an
  answer.

[Compacting](/events/rewriting#stream-compacting) does work, and clears the BLOB: the snapshot it
writes is JSON, and leaving the BLOB would keep a body no reader will ever look at.
:::

::: tip
**Fisher ships no `IEventBinarySerializer`, and that is the end state.** A binary encoding is a choice
with real consequences for schema evolution — MessagePack, protobuf and compressed JSON fail
differently when an event type gains a member — and picking one would be Fisher deciding how your
data ages.
:::

## Dead letters

`fi_dead_letters` holds one row per event a shard could not apply and was configured to skip, with
`DeadLetterEvent`'s columns one for one so CritterWatch reads Fisher's the same way it reads Marten's.

Three decisions in it:

- **No foreign key to `fi_events`, deliberately** — the opposite of the tag tables. A tag is
  meaningless without its event; a dead letter is the record that something went wrong and has to
  survive the event being archived, compacted or cleaned away. A cascade would erase the evidence
  somebody came looking for.
- **The write goes on its own connection, outside the failing batch's transaction.** That batch is
  about to roll back; a dead letter written inside it would roll back with the very failure it is
  recording, and the shard would skip the event leaving no trace.
- **It is an upsert, not an insert**, because the id is assigned at construction and the daemon
  retries the write in the background.

Nothing else removes them, which is why `DeleteAllEventDataAsync` does.

## Deletion order

`DeleteAllEventDataAsync` deletes in a fixed order — **tag tables first**, dead letters last.
`fi_event_tag_*` rows have a real foreign key to `fi_events(seq_id)` and Weasel's default profile
turns enforcement on, so clearing events first fails with `FOREIGN KEY constraint failed`.

`CompletelyRemoveAllAsync` needs no ordering: SQLite does not enforce a foreign key against a dropped
table.
