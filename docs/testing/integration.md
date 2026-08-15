# Integration Testing

**This is one of Fisher's strongest cases.** There is no database server to start, no container to
wait for, and no shared state between test classes — a store is a file, and a throwaway file per
fixture is genuinely isolated.

Fisher's own suite works exactly this way: no Docker Compose file, no fixture ordering, no shared xUnit
collection.

## A throwaway store per test

```cs
public sealed class store_fixture : IAsyncDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():n}.db");

    public DocumentStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Store = DocumentStore.For(opts =>
        {
            opts.Connection($"Data Source={_path}");
            opts.Projections.Snapshot<Order>(SnapshotLifecycle.Inline);
        });

        await Store.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();     // releases this store's pooled connections
        File.Delete(_path);
    }
}
```

::: warning
Dispose the store before deleting the file. Microsoft.Data.Sqlite pools a connection per connection
string, and the `-wal` and `-shm` sidecars are only removed when the last connection closes.

And **never** call `SqliteConnection.ClearAllPools()` — it disposes every pooled connection in the
process, so with tests running in parallel one class's cleanup takes out another's, intermittently
enough to look like a flake.
:::

## In-memory databases

```cs
opts.Connection("Data Source=test;Mode=Memory;Cache=Shared");
```

Faster, but note that an in-memory database lives only as long as something holds a connection open —
Fisher's data source is what keeps it alive for the store's lifetime.

::: tip
A file under the temp directory is usually the better default. It is nearly as fast, it survives across
connections without qualification, and you can open the file with any SQLite tool when a test fails.
:::

## Isolating without separate files

Two logical stores in one file are isolated by their table prefix:

```cs
opts.DatabaseSchemaName = $"test_{Guid.NewGuid():n}";
```

That is what the prefix is *for*. But note that they still share **one write lock** — separate files
give you concurrency as well as isolation.

## Cleaning between tests

```cs
await store.Advanced.ResetAllDataAsync();
```

Or a single type:

```cs
await store.Advanced.Clean.CleanAsync<Order>();
```

See [Tearing Down Document Storage](/schema/cleaning).

## Registering document types up front

::: tip
**A read against a type nothing has written provisions its table and answers empty**, exactly as the
first write of that type does. So a test that queries a collection before seeding it gets an empty
list, not `no such table`.
:::

Registering the types up front is still worth doing, for a different reason: it puts every table in
the one migration your fixture already runs, instead of paying a migration on the first read or write
of each type.

```cs
opts.Schema.For<Order>();
```

::: warning
An [enlisted session](/documents/sessions#enlisting-in-your-own-connection-or-transaction) is the
exception, on reads as on writes: it cannot provision, because running a migration on a second
connection from inside your transaction would deadlock against your own write lock. A missing table
throws by name there.
:::

## Testing async projections

The daemon is asynchronous, so wait for it:

```cs
var daemon = await store.BuildProjectionDaemonAsync();
await daemon.StartAllAsync();

// …append events…

await daemon.WaitForNonStaleData(TimeSpan.FromSeconds(5));
```

Or from a query:

```cs
var results = await session.Query<Summary>()
    .QueryForNonStaleData(TimeSpan.FromSeconds(5))
    .ToListAsync();
```

::: warning
**Non-stale does not imply a post-commit listener has run.** The progression row is written *inside* the
batch's transaction, so non-stale is true the moment that commits — strictly before any listener. A test
that waits on non-staleness and then asserts a listener fired fails roughly one full-suite run in
several. Wait on the listener's own signal.
:::

## The projection scenario harness

```cs
await store.Advanced.EventProjectionScenarioAsync(scenario =>
{
    scenario.Append(streamId, new OrderPlaced("Acme", 100m));
    scenario.Append(streamId, new OrderShipped(DateTimeOffset.UtcNow));

    scenario.DocumentShouldExist<Order>(streamId, order => Assert.True(order.Shipped));
});
```

::: tip
Its teardown clears the event store and the document types the **registered projections** own — not
every table. A scenario is entitled to seed documents its projections do not produce, and clearing
those would make the harness quietly destructive.
:::

## Testing rebuilds properly

::: danger
A replay rewrites every row it can still produce, so **a broken teardown is invisible** to a test that
checks a live aggregate is correct.

Plant a row the replay **cannot** recreate — one whose backing events are gone, or against an id no
event mentions — and assert the rebuild removed it.
:::

## Two traps from Fisher's own suite

::: warning
**Do not sample a boundary time from the client clock.** `last_modified` is written by SQLite's own
`strftime('now')`, so a client-sampled bound compares two clocks that are only incidentally the same
one. Read the stored value back instead — that removes the question rather than widening the window and
hoping.
:::

::: warning
**An `ActivityListener` is process-wide**, and test collections run in parallel. A test that asserts
`Single(...)` over recorded [trace spans](/diagnostics#tracing) is green alone and red in a full suite.
Filter by a tag the test's own store sets.
:::

## Testing against Fisher, deploying on Marten or Polecat

The API is shared, so this is a common and reasonable setup — an integration suite that needs no server
at all, against production code that runs on PostgreSQL or SQL Server.

::: warning
What does **not** carry across is the behaviour this documentation calls out as SQLite-specific: the
[exclusive append methods fail rather than wait](/events/appending#the-exclusive-methods-fail-where-the-siblings-wait),
[string comparisons are ordinal](/documents/querying/linq/strings), sub-millisecond timestamp equality
is normalised away, and there is one writer per file. Test those against the store you deploy on.
:::
