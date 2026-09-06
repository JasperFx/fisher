# Archiving Streams

```cs
session.Events.ArchiveStream(streamId);
session.Events.UnArchiveStream(streamId);
await session.SaveChangesAsync();
```

Archiving sets `is_archived` on `fi_streams`. The events stay; the stream is simply marked as no
longer live.

## What archiving affects

- **An archived stream refuses further appends**, with `ArchivedStreamException`. Archiving is not a
  soft delete you can keep writing through; unarchive it first if that is really the intent.
- A [natural key](/events/natural-keys) no longer resolves an archived stream. The lookup joins
  `fi_streams`, so the flag is read off the join rather than being copied.
- An archived stream is excluded from the explorer's recent-streams read.
- The events remain in `fi_events` and remain visible to the [async daemon](/events/projections/async-daemon)
  and to [stream reads](/events/querying).

::: warning
**Starting a stream over an archived id is still a collision**, not an archive refusal — you get
`ExistingStreamIdCollisionException`. An archived id is an id in use, and a caller who reused one
needs a different id rather than an unarchive.
:::

## Archiving through an `Archived` event

A [single stream projection](/events/projections/single-stream-projections) that owns the stream archives it when
it sees `JasperFx.Events.Archived`, inline or through the daemon:

```cs
session.Events.Append(streamId, new Archived("Closed out"));
await session.SaveChangesAsync();
```

"Owns" is the operative word: only a projection with a snapshot for that stream — either one it
loaded or one this slice produced — archives it, so sibling projections in a composite do not fire
phantom archives.

::: tip
**The direct operation is Fisher's own route and needs no projection at all**, which is why its
natural key tables carry no `is_archived` column: Polecat copies the flag and keeps it in sync from a
projection watching for the event, which then needs a second, rebuild-time entry point — because a
daemon rebuild replays events without appending streams and would otherwise leave the table empty
after teardown. Nothing here watches for anything, so there is nothing to repopulate.
:::

## Tombstoning

```cs
session.Events.TombstoneStream(streamId);
```

A tombstone marks a stream as abandoned. Use it when a stream was created in error and its events
should not be interpreted.

## Archiving is not deletion

The events are still there and still occupy space. If you want them **gone**, the options are:

| Want | Use |
| :--- | :--- |
| The stream's history collapsed to one snapshot event | [Stream compacting](/events/rewriting#stream-compacting) |
| Protected information removed from event bodies | [Event data masking](/events/rewriting#event-data-masking) |
| Everything gone | [`DeleteAllEventDataAsync`](/schema/cleaning) |

::: warning
Deleting events is safe **only** because `fi_events.seq_id` is `AUTOINCREMENT`. A bare
`INTEGER PRIMARY KEY` aliases the rowid, which SQLite reuses after a delete, and a reused sequence
below the daemon's high-water mark is an event no async projection would ever see.
:::

## Archiving and projections

An archived stream's events are unchanged, so a projection that already folded them keeps what it
derived. Archiving is a statement about the stream, not a correction to anything built from it — if
you need a projection to forget it, rebuild the projection.

## Statistics

Archived and compacted streams are why `EventSequenceNumber` can exceed `EventCount`:

```cs
var stats = await store.Advanced.FetchEventStoreStatisticsAsync();
```

See [Event Storage](/events/storage#statistics).
