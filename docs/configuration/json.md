# JSON Serialization

Fisher serializes documents and event bodies with **System.Text.Json**, and stores the result as TEXT
in a `data` column. There is no Newtonsoft option.

## Configuring serialization

```cs
var store = DocumentStore.For(opts =>
{
    opts.Connection("Data Source=app.db");

    opts.ConfigureSerialization(
        enumStorage: EnumStorage.AsInteger,
        casing: Casing.CamelCase,
        collectionStorage: CollectionStorage.Default,
        nonPublicMembersStorage: NonPublicMembersStorage.Default,
        configure: json =>
        {
            json.WriteIndented = false;
            json.Converters.Add(new MyConverter());
        });
});
```

Or supply a serializer of your own:

```cs
opts.Serializer = new MyCustomSerializer();
```

## Enum storage, and why the default matters here

`EnumStorage.AsInteger` is the default, and on Fisher that is more than a preference.

Under `EnumStorage.AsString` the stored value is the member's *name*, so **ordering by it sorts
alphabetically** rather than by the enum's declared order — `HighDistinction` before `Pass`, whatever
the numeric values say. Fisher refuses rather than answering wrongly: both the `Where` parser and
`OrderBy` throw a `BadLinqExpressionException` naming `EnumStorage` when a range comparison is
attempted against a string-stored enum. Equality still works.

Under `AsInteger`, `json_extract` yields a number that orders correctly with no help, so every
operator is available.

The same rule reaches the [aggregates](/documents/querying/linq/grouping#aggregates): a string-stored
enum is excluded from `Min`/`Max` for the ordering reason, and every enum is excluded from
`Sum`/`Average` even under `AsInteger` — an enum's numeric value is an identifier, and **SQLite's
`sum()` over text returns 0 rather than failing**, which would report a plausible total for a column
that has none.

## What the stored JSON is

`data` is TEXT holding **exactly what System.Text.Json wrote** — no normalisation of whitespace, no
key reordering, no encoding decision. PostgreSQL's `jsonb` cannot promise this and SQL Server's
`nvarchar` needs the encoding decided.

That is what makes the [JSON-returning reads](/documents/querying/query-json) byte-exact, and it is
the largest single reason those reads are worth more on Fisher than on either sibling.

::: warning
[Patching](/documents/partial-updates-patching) breaks byte-exactness. `json_set` re-renders the
document, so a patched row is no longer identical to what the serializer would have written, and a
new or renamed key lands at the end.
:::

## Casing

`Casing.CamelCase` is the default. The casing decides the **stored key names**, which is what
`json_extract` paths are built against — so changing it on a store that already holds documents makes
existing rows unreadable through their members. Treat it as a schema decision.

## Dates inside documents

A `DateTimeOffset` member is stored as whatever System.Text.Json wrote: trailing fractional zeros
trimmed, and the original offset kept. That is **not order-preserving** — `12:34:56-05:00` sorts
before `12:34:56.789+00:00` while being five hours later.

So Fisher compares a document's timestamp member through SQLite's date parser rather than against the
raw JSON, folding the offset into UTC and rendering fixed-width to the millisecond. Equality goes
through the same normalisation as ordering — two spellings of one instant must not be equal for `>=`
and unequal for `==` — which costs sub-millisecond discrimination on `==`, as it does on the
siblings.

`DateOnly` and `TimeOnly` need none of this: a `DateOnly` is fixed-width with no offset and no
fraction, and a `TimeOnly`'s optional fraction is a strict suffix.

::: tip
The event store's own timestamp columns are different. They use Fisher's fixed-width UTC format
precisely so they *do* sort as text, which is why `IEvent.Timestamp` permits range comparison in a
[tag predicate](/events/dcb) where a document's `DateTimeOffset` member would not.
:::

## Binary event bodies

An event type marked `[BinaryEvent]`, or registered through
`opts.Events.UseBinarySerializer<TEvent>(…)`, bypasses JSON entirely and is stored as a BLOB in
`fi_events.data_binary`. See [Binary event bodies](/events/storage#binary-event-bodies) — including
why Fisher ships no `IEventBinarySerializer` of its own, and why the interface lives in
`JasperFx.Events` rather than in Fisher.
