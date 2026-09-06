# Upcasting Events

An event store keeps its JSON forever. Upcasting is how an application changes an event's schema
without rewriting the rows that are already there: register a transformation, and **every read path
hands back the new type** — stream fetches, live aggregation, `FetchForWriting`, the async daemon,
subscriptions.

```cs
options.Events.Upcasters.Upcast<CartOpenedV1, CartInitialized>(
    old => new CartInitialized(old.CartId, old.ClientId, "Opened"));
```

The old CLR type can then be deleted, which is the point: aggregations and projections are written
against the new schema only.

::: tip
The registry, the transformation shape and the `IEventUpcaster` bases are all JasperFx's
(`JasperFx.Events.Upcasting`) — Fisher supplies the read path and one `IUpcastPayload` adapter over
its own reader and serializer. So an upcast registration ports between Fisher, Marten and Polecat
unchanged.
:::

## The registration shapes

| | |
| :--- | :--- |
| `Upcast<TOld, TNew>(old => new(...))` | Typed. Claims `TOld`'s conventional event type name |
| `Upcast<TOld, TNew>(name, old => ...)` | Typed, claiming an explicit stored name |
| `Upcast<TNew>(doc => ...)` | Raw `JsonDocument` — no old CLR type kept at all |
| `Upcast<TNew>(name, doc => ...)` | Raw JSON, claiming an explicit stored name |
| `Upcast<TOld, TNew>(async (old, ct) => ...)` | Async-only. See below |
| `Upcast(new MyUpcaster())` / `Upcast<MyUpcaster>()` | Class-based, over `IEventUpcaster` |

Registration is **last-wins per stored event type name**: re-registering a name replaces the earlier
transformation.

## Raw JSON is cheap here

The raw-`JsonDocument` shape is what lets you drop the old type entirely, and Fisher can always offer
it: `fi_events.data` holds exactly the text System.Text.Json wrote, so the document is a parse of a
string already in hand. Marten's equivalent has to refuse when its configured serializer is not
System.Text.Json.

```cs
options.Events.Upcasters.Upcast<DiscountApplied>(
    "coupon_clipped",
    document => new DiscountApplied(
        document.RootElement.GetProperty("cartId").GetGuid(),
        document.RootElement.GetProperty("percent").GetInt32() / 100.0));
```

::: warning
Property names are **as stored**, and Fisher's default serializer is camelCase. A raw-JSON
transformation reads what the serializer actually wrote, not the CLR member names — that is the
trade for not keeping the old type. The typed shapes have no such concern: they deserialize through
the store's own serializer.
:::

## The authority rule

**A registered transformation is the authoritative interpretation of its source event type name.**
Fisher stores a `dotnet_type` hint on every row, and that hint **does not get a vote**: the registry
is consulted before the hint is resolved.

That matters when the old CLR type is still in the codebase. A typed append of it writes both the
source name and a `dotnet_type` pointing at the old type, so letting the hint win would read those
rows back as the old schema while every row written by the previous deployment upcast correctly —
one store, one event type name, two answers depending on which deployment wrote the row.

## Async-only transformations

Register a `Func<TOld, CancellationToken, Task<TNew>>` when the transformation genuinely has to
await — a lookup, say. Fisher's read paths are all asynchronous, so an async-only registration works
everywhere; prefer the synchronous form when you do not need it.

## What upcasting does not reach

- **A projection that has already run.** An upcast changes how a row is *read*, and the daemon's
  high-water mark is a sequence — so a document, snapshot or flat table built from the old schema
  keeps what it derived until that projection is rebuilt. Same caveat as
  [event data masking](/events/rewriting#event-data-masking), and the same reason: upcasting is a
  read-time reinterpretation, not a correction to anything derived.
- **A binary event body's raw JSON.** A `[BinaryEvent]` row's `data` column holds only a placeholder,
  so a raw-JSON transformation over one is refused by name rather than handed `{}`. Register a typed
  upcast instead — it reads the body through the old type's own `IEventBinarySerializer`.

## Subscriptions and projections filtered by event type

Nothing to do. A subscription that names the **new** types still receives the old rows: Fisher pushes
the type filter into SQL, and it is widened with every registered transformation's source name. Left
alone, a filtered shard would read nothing at all from the history it was pointed at and report
itself caught up.

## Registering the new type

Nothing to do here either. The store registers each transformation's target event type at
construction — nothing else would, since dropping the old type means no `AddEventType`, projection
registration or append ever mentions the new one.
