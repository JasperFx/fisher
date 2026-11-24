# Natural Keys

Address a stream by the business identifier it was created with, an invoice number or a booking reference, rather
than by its Guid. Mark the identifying member with `[NaturalKey]`, say which events set it, and register the
aggregate's projection:

<!-- snippet: sample_natural_key_declaration -->
<a id='snippet-sample_natural_key_declaration'></a>
```cs
// A strong-typed key: one public property, and a constructor taking it
public readonly record struct BookingReference(string Value);

public record BookingMade(BookingReference Number, decimal Amount);

public record BookingPaid(decimal Amount);

public partial class Booking
{
    public Guid Id { get; set; }

    // The owned key: Booking's inline projection keeps the lookup from BookingReference to the stream
    [NaturalKey]
    public BookingReference Number { get; set; }

    public decimal Balance { get; set; }

    [NaturalKeySource]
    public static Booking Create(BookingMade e) => new() { Number = e.Number, Balance = e.Amount };

    public void Apply(BookingPaid e) => Balance -= e.Amount;
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/natural_key_samples.cs#L57-L82' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_natural_key_declaration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: sample_registering_natural_keys -->
<a id='snippet-sample_registering_natural_keys'></a>
```cs
_store = DocumentStore.For(opts =>
{
    opts.ConnectionString = _database.ConnectionString;

    // The owner of the key has to be registered, and Inline keeps its lookup current
    // as the events commit
    opts.Projections.Snapshot<Booking>(SnapshotLifecycle.Inline);

    // The mapped read model can be any lifecycle
    opts.Projections.Snapshot<BookingSummary>(SnapshotLifecycle.Async);
});
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/natural_key_samples.cs#L112-L126' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_natural_keys' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A key is an `int`, a `long`, a `string`, or a strong-typed wrapper around one of them. The definition, the attributes
and their discovery are all JasperFx's, so they read the same on Marten and Polecat. Fisher supplies the storage, the
same division as the async daemon. A key declared in a way the store would refuse is a build error; see
[Build Diagnostics: natural keys](https://shared-libs.jasperfx.net/codegen/diagnostics#natural-keys) for `JFXEVT010`
to `JFXEVT018`. The store checks the same rules again when it is built, and throws `InvalidProjectionException`
naming the problem.

## Fetching by natural key

Every fetch that takes a stream id has a form that takes a natural key in its place, as a second type argument:

<!-- snippet: sample_fetching_by_natural_key -->
<a id='snippet-sample_fetching_by_natural_key'></a>
```cs
// Read the current state, or null when no live stream has the key
var booking = await session.Events.FetchLatest<Booking, BookingReference>(number);

// Fetch for writing, with the optimistic version guard at SaveChangesAsync
var stream = await session.Events.FetchForWriting<Booking, BookingReference>(number);
stream.AppendOne(new BookingPaid(25m));
await session.SaveChangesAsync();
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/natural_key_samples.cs#L152-L162' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_fetching_by_natural_key' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`FetchForWritingByNaturalKey<T, TKey>` and `FetchLatestByNaturalKey<T, TKey>` are the same fetches with no room for
doubt about what the identifier is. See [where natural keys meet stream identity](#where-natural-keys-meet-stream-identity)
for when that matters.

A key type argument is always `TKey`, never `TKey?`. A fetch by `Nullable<TKey>` throws `ArgumentException`, so deal
with a null value before fetching. A null key value, including a strong-typed key whose inner value is null, is
answered with null by `FetchLatest` without a query, and refused with `ArgumentOutOfRangeException` by
`FetchForWriting` and `FetchForExclusiveWriting`, the way an empty `Guid` stream id is.

### Checking the version {#expected-version}

A command that carries the version the user was looking at can have it checked when the aggregate is fetched:

<!-- snippet: sample_fetching_by_natural_key_with_a_version -->
<a id='snippet-sample_fetching_by_natural_key_with_a_version'></a>
```cs
// The command carries the version the user saw. A stale one throws
// EventStreamUnexpectedMaxEventIdException, a ConcurrencyException, before
// anything is enlisted in the session
var checkedStream = await session.Events.FetchForWriting<Booking, BookingReference>(number, expectedVersion: 2);
checkedStream.AppendOne(new BookingPaid(25m));
await session.SaveChangesAsync();
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/natural_key_samples.cs#L166-L175' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_fetching_by_natural_key_with_a_version' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A stale version throws `EventStreamUnexpectedMaxEventIdException`, which is a `ConcurrencyException` and a 409 in
Wolverine's HTTP handling. The check runs before anything is enlisted in the session, so a session whose fetch
failed has nothing pending to commit by mistake. The same overload takes a stream id or a strong-typed id.

### Exclusive writes {#exclusive-writes}

<!-- snippet: sample_exclusive_writing_by_natural_key -->
<a id='snippet-sample_exclusive_writing_by_natural_key'></a>
```cs
// Takes the database's write lock, resolves the key inside it, and holds the lock until
// SaveChangesAsync commits or the session is disposed. A competing exclusive writer waits
// its turn rather than failing
var locked = await exclusive.Events.FetchForExclusiveWriting<Booking, BookingReference>(number);
locked.AppendOne(new BookingPaid(9m));
await exclusive.SaveChangesAsync();
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/natural_key_samples.cs#L220-L229' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_exclusive_writing_by_natural_key' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`FetchForExclusiveWriting` takes the database's write lock before it reads, resolves the key inside the lock so it
cannot move to another stream before the commit, and holds the lock until `SaveChangesAsync` commits or the session
is disposed. A competing exclusive writer waits its turn. See [exclusive writes](/events/appending#exclusive-writes)
for what holding SQLite's lock costs.

### Several streams at once

<!-- snippet: sample_fetch_many_by_natural_key -->
<a id='snippet-sample_fetch_many_by_natural_key'></a>
```cs
// Three statements however many keys: one to resolve them, two to fetch the streams.
// The handles come back in the order of the keys
var streams = await session.Events.FetchManyForWriting<Booking, BookingReference>([number, other]);
foreach (var each in streams)
{
    each.AppendOne(new BookingPaid(1m));
}

await session.SaveChangesAsync();
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/natural_key_samples.cs#L187-L199' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_fetch_many_by_natural_key' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A key that names no live stream throws `UnknownNaturalKeyException` before anything is enlisted, as a single
`FetchForWriting` by that key does. A repeated key, a null key and a `TKey?` type argument are refused.

### Batch queries

The same fetches are on a batch's `Events`, in the shape Marten's batch has:

<!-- snippet: sample_batching_natural_key_fetches -->
<a id='snippet-sample_batching_natural_key_fetches'></a>
```cs
var batch = session.CreateBatchQuery();

var writing = batch.Events.FetchForWriting<Booking, BookingReference>(number);
var view = batch.Events.FetchLatest<BookingSummary, BookingReference>(other);

await batch.Execute();

var bookingStream = await writing;
var otherSummary = await view;
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/natural_key_samples.cs#L201-L213' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_batching_natural_key_fetches' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`batch.Events` also has `FetchForWriting(id, expectedVersion)`, `FetchForExclusiveWriting`, `FetchStream` and
`FetchStreamState`. A batch on Fisher is [for parity, not speed](/migration-guide#batched-queries-are-not-a-performance-feature):
there is no round trip to save on an embedded database, so the reads run one after another on the session's
connection when `Execute` is called. A read that fails faults its own task, and `Execute` throws as well.

## Mapped natural keys {#mapped-keys}

An aggregate can be fetched by a key another aggregate owns. That is how an **async** read model becomes reachable
by natural key: it maps the key with `[MappedNaturalKey<TSource>]`, and the fetch reads the source's lookup, which
the source keeps current inline as the events commit.

<!-- snippet: sample_mapped_natural_key -->
<a id='snippet-sample_mapped_natural_key'></a>
```cs
// An async read model of the same stream. It owns no lookup: the key resolves through Booking's
public partial class BookingSummary
{
    public Guid Id { get; set; }

    [MappedNaturalKey<Booking>]
    public BookingReference Number { get; set; }

    public int Payments { get; set; }

    public static BookingSummary Create(BookingMade e) => new() { Number = e.Number };

    public void Apply(BookingPaid _) => Payments++;
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/natural_key_samples.cs#L84-L101' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_mapped_natural_key' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: sample_fetching_a_mapped_natural_key -->
<a id='snippet-sample_fetching_a_mapped_natural_key'></a>
```cs
// BookingSummary is async and may not be caught up yet. FetchLatest folds the stream
// the key names, so the answer is current anyway
var summary = await session.Events.FetchLatest<BookingSummary, BookingReference>(number);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/natural_key_samples.cs#L177-L183' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_fetching_a_mapped_natural_key' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

- The source has to be registered and has to own a key of the same type. The store refuses to build otherwise,
  naming the mapped key and what is wrong with it. `[NaturalKey(SourceProjection = typeof(...))]` is the same
  declaration, and can name a projection class rather than an aggregate.
- An aggregate owns at most one key, and can map any number of others, each of its own type. The fetch picks the
  key by the type argument.
- A mapped key follows a rename by its owner with no bookkeeping of its own, because it reads the owner's lookup.
- `FetchLatest` and `FetchForWriting` on an async aggregate fold the stream the key names, so they are current
  whether or not the daemon has caught up.
- An aggregate in a [composite projection](/events/projections/composite) cannot own a key, but it can map one to
  an owner registered outside the composite.
- The [parked identity list](/events/projections/async-daemon#parking-a-failing-identity) shows a mapped key for a
  projection that has no key of its own.

## Nullable keys {#nullable-keys}

A struct key can be declared `TKey?` on the aggregate, and a `[NaturalKeySource]` can return `TKey?`. It is the same
key as a `TKey`: the lookup, the fetches and the mapped keys all use the underlying type, and an aggregate whose key
is still null simply has no lookup row. Fetch it by `TKey`. What a null from a source means is covered under
[a null from a key source](#null-keys).

## Storage

One `fi_natural_key_<alias>` table per definition, holding the key and the stream it resolves to. Rows
are written by an inline projection, inside the append's transaction — a key registered outside it
would leave either a stream no key resolves to, or a key naming a stream that does not exist.

## Backfilling a key over history that already exists

**Declaring a natural key on an aggregate whose streams already exist is a supported thing to do.**
The lookup is also maintained by the projection daemon: every page of events a shard reads writes the
keys it carries, so running the daemon over the history — a rebuild, or a fresh async projection
catching up from zero — populates the lookup for streams that were appended long before anybody
declared the key.

```cs
// After adding [NaturalKey] to an aggregate on a store that already has data:
using var daemon = await store.BuildProjectionDaemonAsync();
await daemon.RebuildProjectionAsync<OrderSummary>(CancellationToken.None);

// Streams that existed before the key was declared now resolve by it.
var stream = await session.Events.FetchForWritingByNaturalKey<Order, string>("ORD-2024-0001");
```

::: tip
The same mechanism is what repopulates the lookup if it is ever emptied, and it is why the two paths
are two different statements. The **append** path *claims* a key and refuses one already mapped to a
different live stream; the **replay** path is last-writer-wins and refuses nothing. Re-adjudicating on
a replay would turn a pre-existing data condition into a shard that can never advance again, with
nobody present to correct it.
:::

::: warning
An **archived** stream's events are not read by the daemon, so a rebuild does not put its key back —
and it would not matter if it did. The lookup carries no `is_archived` column; whether a key resolves
is read off the join to `fi_streams`, so the presence of a row says nothing on its own.
:::

## Archived streams

The lookup **joins `fi_streams`**, so an archived stream no longer resolves — and Fisher's natural key
tables carry no `is_archived` column of their own.

::: tip
Polecat copies the flag onto its lookup table and keeps it in sync from a projection watching for the
`Archived` event. Fisher [archives with a direct operation](/events/archiving) rather than an event, so
there is nothing to watch — and reading the flag off the join makes `fi_streams` the only place that
knows.
:::

## A miss is null from `FetchLatest` and an exception from `FetchForWriting`

The asymmetry is deliberate. `FetchForWriting` is the read half of a read-modify-write and has to say
what it would be writing to, so a key naming no live stream throws `UnknownNaturalKeyException`.
`FetchLatest` reports current state, and `FetchLatestByNaturalKey(...) is null` is the idiomatic
"does this aggregate exist?" probe — the same question the by-id overload answers that way.

::: tip
The `FetchForWriting` miss is the one place the three stores genuinely disagree — Marten hands back a
null aggregate, Polecat throws `InvalidOperationException`, Fisher throws its own — so the shared
compliance suite deliberately does not pin it. The `FetchLatest` miss it does pin, and all three
agree on null.
:::

## A second stream claiming a key is refused

::: warning
**Refusing is the shared contract, and it was Fisher's behaviour that became it.** Polecat's `MERGE`
updated the stream id on conflict, so the newcomer silently took the key and the original stream
became unreachable by the identifier it was created with.
[jasperfx#764](https://github.com/JasperFx/jasperfx/issues/764) ruled for refusing, and Polecat is
changing to match.

Fisher's conflict clause carries `where stream_id = excluded.stream_id` and returns the row it settled
on — the same stream returns it, a new key returns it, a conflicting stream matches nothing — and "no
row" becomes `DuplicateNaturalKeyException`.
:::

Re-asserting the **same** mapping stays idempotent, which it has to be: every event carrying the key
rewrites the row.

**The refusal is the statement, not a read before it.** A probing `SELECT` to see whether the key is
free would race — two sessions could both find it free, and the loser's write would repoint the row
exactly as an unguarded upsert does. The guard is a `where` on the conflict clause instead, so a
conflicting claimant matches no row and the `returning` clause hands back nothing, and the row lock
the upsert already takes is what serialises concurrent claimants.

## Renaming retires the previous key

A stream has exactly one *current* natural key, so an event that changes the key deletes the row the
old one occupied. The superseded identifier stops resolving, and — the half that matters more —
becomes free for another stream to claim.

::: tip
A retired alias that resolved forever would also occupy its slot in the lookup's primary key forever,
which is what makes this a defect rather than a nicety. All three stores had it the other way round
at some point.
:::

## A null from a key source {#null-keys}

What a `null` key value means depends on the source that produced it.

- An **explicit** source can remove a stream's key on purpose: a `[NaturalKeySource]` static method that
  returns the key type, or a `NaturalKeyFor()` registration. When it returns `null`, the lookup row is
  deleted, the stream can no longer be found by its old key, and that key is free for another stream to
  claim. A strong-typed key whose inner value is null counts as null.
- A source discovery **infers**, by reading a key-typed property off the event or by running your `Apply`
  method against a blank aggregate, reads `null` as "this event carries no key". The stream keeps its key,
  so an `Apply` that only sets the key when its event carries one does what it says.

The rule is JasperFx's (`NaturalKeyEventMapping.RetiresKeyOnNull`, jasperfx#1287), and Fisher follows it on
the append path and on a replay, so a rebuild does not bring back a key an explicit source removed. A stream
that has never been given a key has no lookup row.

<!-- snippet: sample_natural_key_sources_and_null -->
<a id='snippet-sample_natural_key_sources_and_null'></a>
```cs
public record ShipmentBooked(string Code);

public record ShipmentUpdated(string? NewCode, string Notes);

public record ShipmentCodeWithdrawn;

public partial class Shipment
{
    public Guid Id { get; set; }

    [NaturalKey]
    public string? Code { get; set; }

    public string Notes { get; set; } = string.Empty;

    [NaturalKeySource]
    public void Apply(ShipmentBooked e) => Code = e.Code;

    // Inferred: the key is read off the event, so a null NewCode means "this event carries no key"
    // and the stream keeps its code
    [NaturalKeySource]
    public void Apply(ShipmentUpdated e)
    {
        if (e.NewCode != null) Code = e.NewCode;
        Notes = e.Notes;
    }

    // Explicit: a static method returning the key type, so null removes the stream's key and frees it
    [NaturalKeySource]
    public static string? KeyFor(ShipmentCodeWithdrawn e) => null;

    public void Apply(ShipmentCodeWithdrawn e) => Code = null;
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/natural_key_samples.cs#L14-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_natural_key_sources_and_null' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## No foreign key to fi_streams

Uniformly, and deliberately. Polecat declares one for a single-tenant store and omits it under
conjoined tenancy, where its provider's column sorting breaks the composite mapping — so its two
tenancy styles behave differently.

One rule beats referential integrity in half the configurations, and a row whose stream is gone
resolves to nothing anyway, because the join is what produces an answer.

## Resolving outside the write transaction is safe

`FetchForWriting` resolves the key before the write transaction opens. That is the same argument the
[optimistic append](/events/appending#optimistic-concurrency) rests on: the version guard runs inside the write
transaction regardless, so a stale resolution fails the commit rather than writing a wrong version. When a loser
should wait instead of failing, use [`FetchForExclusiveWriting`](#exclusive-writes), which resolves the key inside the
lock.

## Guid stream ids

A Guid stream id binds as **lowercase canonical text** here as everywhere else. This is the third
table where getting that wrong would be silent, after documents and tag rows, and the failure mode is
identical: every lookup returns nothing.

## Where natural keys meet stream identity

```cs
var stream = await session.Events.FetchForWriting<Order, string>("INV-2026-0042");
```

::: warning
On a **string-identity** store, this reads the string as the *stream key*, not as a natural key. The
stream identity type wins, because which reading applies must not depend on whichever aggregate types
happen to declare a natural key.

`FetchForWritingByNaturalKey` and `FetchLatestByNaturalKey` are the unambiguous spellings.
:::

## Cleaning

`DeleteAllEventDataAsync` clears the lookup tables with the rest. Leaving them behind is not cosmetic:
the duplicate guard would then fire on data that no longer exists.
