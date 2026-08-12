# Archiving Streams

```cs
session.Events.ArchiveStream(streamId);
session.Events.UnArchiveStream(streamId);
await session.SaveChangesAsync();
```

Archiving sets `is_archived` on `fi_streams`. The events stay; the stream is simply marked as no
longer live.

## What archiving affects

- A [natural key](/events/natural-keys) no longer resolves an archived stream. The lookup joins
  `fi_streams`, so the flag is read off the join rather than being copied.
- An archived stream is excluded from the explorer's recent-streams read.
- The events remain in `fi_events` and remain visible to the [async daemon](/events/projections/async-daemon)
  and to [stream reads](/events/querying).

::: tip
**Fisher archives with a direct operation rather than by appending an `Archived` event.** That is why
its natural key tables carry no `is_archived` column of their own: Polecat copies the flag and keeps
it in sync from a projection watching for that event, which then needs a second, rebuild-time entry
point — because a daemon rebuild replays events without appending streams and would otherwise leave
the table empty after teardown.

Nothing here watches for anything, so there is nothing to repopulate.
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
