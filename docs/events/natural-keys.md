# Natural Keys

Address a stream by the business identifier it was created with, rather than by its Guid.

```cs
public record InvoiceRaised([property: NaturalKey] string InvoiceNumber, decimal Amount);
```

```cs
var stream = await session.Events.FetchForWritingByNaturalKey<Invoice>("INV-2026-0042");
var invoice = await session.Events.FetchLatestByNaturalKey<Invoice>("INV-2026-0042");
```

The definition, the attributes and the discovery are all JasperFx's — Fisher supplies the storage
seam, the same division as the async daemon.

## Storage

One `fi_natural_key_<alias>` table per definition, holding the key and the stream it resolves to. Rows
are written inside the append's transaction, beside the tag writer.

::: tip
**The rows are written from the session, not from an inline projection.** A natural key row is an
*index over streams*, not a projection of them. Being a projection is exactly what forces Polecat's
rebuild-time entry point; nothing here is reachable from a rebuild, so there is nothing to repopulate.
:::

A key registered outside the transaction would leave either a stream no key resolves to, or a key
naming a stream that does not exist.

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

## Renaming retires the previous key

A stream has exactly one *current* natural key, so an event that changes the key deletes the row the
old one occupied. The superseded identifier stops resolving, and — the half that matters more —
becomes free for another stream to claim.

::: tip
A retired alias that resolved forever would also occupy its slot in the lookup's primary key forever,
which is what makes this a defect rather than a nicety. All three stores had it the other way round
at some point.
:::

## No foreign key to fi_streams

Uniformly, and deliberately. Polecat declares one for a single-tenant store and omits it under
conjoined tenancy, where its provider's column sorting breaks the composite mapping — so its two
tenancy styles behave differently.

One rule beats referential integrity in half the configurations, and a row whose stream is gone
resolves to nothing anyway, because the join is what produces an answer.

## Resolving outside the write transaction is safe

The same argument the [optimistic append](/events/appending#optimistic-concurrency) rests on: the
version guard runs inside the write transaction regardless, so a stale resolution fails the commit
rather than writing a wrong version. A lock would only buy the loser waiting instead of failing — the
trade Fisher declines everywhere.

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
