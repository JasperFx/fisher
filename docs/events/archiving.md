# Archiving Streams

```cs
session.Events.ArchiveStream(streamId);
session.Events.UnArchiveStream(streamId);
await session.SaveChangesAsync();
```

Archiving sets `is_archived` on `fi_streams`. The events stay; the stream is simply marked as no
longer live.

## What archiving affects

- **An archived stream refuses further appends**, with `ArchivedStreamException` — which derives from
  `JasperFx.Events.ArchivedStreamException`, so a store-agnostic `catch` works on Marten and Polecat
  too. Archiving is not a soft delete you can keep writing through; unarchive it first if that is
  really the intent.
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

## Deleting a stream with `TombstoneStream`

Archiving keeps everything. `TombstoneStream` is the opposite: a **hard delete** of the stream and every
event in it. Use `ArchiveStream` for a stream that is merely finished, and `TombstoneStream` when the
data has to be gone, for example a stream started in error or a purge that a retention or erasure
policy requires.

<!-- snippet: sample_tombstone_stream -->
<a id='snippet-sample_tombstone_stream'></a>
```cs
await using var session = store.LightweightSession();

// Queued like any other operation: nothing is deleted until SaveChangesAsync.
session.Events.TombstoneStream(streamId);

// Deletes the stream's tag rows, then its events, then its fi_streams row, in this
// session's tenant and in one transaction. A stream that does not exist is a no-op.
await session.SaveChangesAsync(token);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/archiving_samples.cs#L19-L28' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_tombstone_stream' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

There is a `TombstoneStream(string)` twin for string-identified streams. What it does:

- **It is queued until `SaveChangesAsync`**, like an append. Nothing is deleted when you call it,
  and it commits or rolls back with the rest of the session's unit of work.
- **The tag rows go first.** Each [DCB tag](/events/dcb) table (`fi_event_tag_*`) has a real foreign
  key to `fi_events(seq_id)`, and the connection profile Fisher uses (Weasel's default) turns
  SQLite's foreign key enforcement on. So the operation
  deletes the stream's tag rows, then its events in `fi_events`, then its row in `fi_streams`.
  Deleting the events first would fail the whole unit of work with `FOREIGN KEY constraint failed`.
- **It is tenant-scoped.** Every delete is filtered on the session's tenant, so under
  [conjoined tenancy](/events/multitenancy) a stream with the same id in another tenant is untouched.
- **A missing stream is a no-op.** Tombstoning an id that does not exist, or no longer exists,
  deletes nothing and does not throw.
- **Sequence numbers are not reused.** `fi_events.seq_id` is `AUTOINCREMENT`, so the deleted events'
  sequence numbers stay spent and the next append continues above them. An async projection's
  high-water mark never ends up above an event it has not seen. Expect `EventSequenceNumber` to
  exceed `EventCount` in the [statistics](/events/storage#statistics) afterwards.
- **Projected documents are left alone.** A snapshot, or a document an inline or async projection
  built from the stream, stays where it is. Delete it yourself if it must go too, or rebuild the
  projection.

After the commit, `FetchStreamStateAsync` returns null and `FetchStreamAsync` returns nothing for that
id. The id is free again, so a later `StartStream` with it starts a brand new stream.

### Store-neutral code

`TombstoneStream(Guid)` and `TombstoneStream(string)` are members of the shared
`JasperFx.Events.IEventStoreOperations` as of JasperFx 2.90.0
([jasperfx#1014](https://github.com/JasperFx/jasperfx/issues/1014)). Code that holds a session only as
`JasperFx.Events.Documents.IDocumentSessionOperations` can therefore call it without knowing which
store it is talking to:

<!-- snippet: sample_tombstone_stream_store_neutral -->
<a id='snippet-sample_tombstone_stream_store_neutral'></a>
```cs
// Written against JasperFx.Events alone: IDocumentSessionOperations.Events is the shared
// IEventStoreOperations, so the same purge compiles against a Fisher, Marten or Polecat session.
public static async Task PurgeAsync(IDocumentSessionOperations session, Guid streamId,
    CancellationToken token)
{
    session.Events.TombstoneStream(streamId);
    await session.SaveChangesAsync(token);
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/archiving_samples.cs#L31-L40' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_tombstone_stream_store_neutral' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

On the shared contract the members default to throwing `NotSupportedException`, so a store that has
not implemented them fails loudly instead of skipping the delete. Fisher implements both, and runs the
shared `StreamTombstoningCompliance` suite against them.

## Archiving is not deletion

The events are still there and still occupy space. If you want them **gone**, the options are:

| Want | Use |
| :--- | :--- |
| The stream's history collapsed to one snapshot event | [Stream compacting](/events/rewriting#stream-compacting) |
| Protected information removed from event bodies | [Event data masking](/events/rewriting#event-data-masking) |
| One stream and its events gone | [`TombstoneStream`](#deleting-a-stream-with-tombstonestream) |
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
