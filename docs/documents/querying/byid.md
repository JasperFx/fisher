# Loading Documents by Id

```cs
var user = await session.LoadAsync<User>(id);              // Guid
var country = await session.LoadAsync<Country>("no");      // string
var invoice = await session.LoadAsync<Invoice>(42);        // int
var entry = await session.LoadAsync<Entry>(42L);           // long
```

A miss returns `null`.

## Strong-typed ids

Both type parameters are explicit, which is what keeps this unambiguous against the four
single-parameter overloads:

```cs
var order = await session.LoadAsync<Order, OrderId>(orderId);
```

## Loading many

```cs
var users = await session.LoadManyAsync<User>(id1, id2, id3);
var users = await session.LoadManyAsync<User>(token, id1, id2, id3);
var orders = await session.LoadManyAsync<Order, OrderId>(ids, token);
```

The ids go into the statement as `json_each(@ids)` — where Marten writes `= ANY($1)` and Polecat uses
`OPENJSON` — so one parameter carries any number of them.

::: tip
Under a [tracking session](/documents/sessions#tracking-modes), `LoadManyAsync` **preselects out of
the identity map** and asks only for the ids it does not hold. Reference identity would survive
either way; what the preselect buys is the read itself.
:::

## Checking existence

```cs
if (await session.CheckExistsAsync<User>(id)) { … }
```

::: tip
`CheckExistsAsync` routes through the LINQ path rather than a hand-written
`select 1 from … where id = ?`. That is what makes it carry the tenant filter, the soft-delete filter
and a hierarchy discriminator without restating any of them — it would otherwise be a fourth caller
having to remember all three.
:::

## Loading raw JSON

```cs
var json = await session.LoadJsonAsync<User>(id);
```

The bytes are exactly what the serializer wrote. See [Querying for Raw JSON](/documents/querying/query-json).

## Reading metadata

```cs
var meta = await session.MetadataForAsync<User>(id);
```

See [Fisher Metadata](/documents/metadata).

## What a load filters

A load applies the tenant and soft-delete filters, from the SQL itself rather than from the caller.

It does **not** apply a hierarchy discriminator, and that asymmetry is deliberate: a load names one
row and the id is unique across the hierarchy, so a discriminator predicate would only turn "that id
is a different sub-class" into the same answer as "no such id". A `LoadAsync<TBase>` returns whatever
sub-class the row is; a `LoadAsync<TDerived>` narrows in memory, by testing what came back. See
[Document Hierarchies](/documents/hierarchies).
