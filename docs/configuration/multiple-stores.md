# Multiple Stores

`AddFisherStore<T>()` registers a second, independently configured store in the same container.

**Several stores are a better fit here than on either sibling.** On PostgreSQL or SQL Server a second
store usually means a second schema in one database and the isolation is the server's. On SQLite a
second store can simply be a second **file** — separately backed up, separately deletable, and with
its own write lock. That last point is the one that matters: one writer per file is SQLite's central
constraint, so splitting a workload across two files is the primary way to get two concurrent
writers, and this is the ergonomic front door to it.

## Registering a secondary store

Declare a marker interface extending `IDocumentStore`:

```cs
public interface IReportingStore : IDocumentStore { }
```

```cs
builder.Services.AddFisher(opts => opts.Connection("Data Source=app.db"))
    .ApplyAllDatabaseChangesOnStartup();

builder.Services.AddFisherStore<IReportingStore>(opts =>
{
    opts.Connection("Data Source=reporting.db");
    opts.StoreName = "Reporting";
    opts.Projections.Snapshot<SalesSummary>(SnapshotLifecycle.Async);
})
.ApplyAllDatabaseChangesOnStartup()
.AddAsyncDaemon(DaemonMode.Solo);
```

Then inject the marker:

```cs
app.MapGet("/sales", async (IReportingStore store) =>
{
    await using var session = store.QuerySession();
    return await session.Query<SalesSummary>().ToListAsync();
});
```

## Two shapes, one mechanism

Both work and always have at the storage layer; this is the registration surface for them:

| Shape | How |
| :--- | :--- |
| A second **file** | A different connection string. Two write locks. |
| A second **logical store in one file** | The same connection string, a different `DatabaseSchemaName`. One write lock. |

::: danger
Two stores registered over **one file with the same `DatabaseSchemaName`** are refused. They would
share every table — each reading, writing and cleaning the other's rows — and it is silent.
:::

::: tip
The registry that enforces this is **scoped to the container, not the process**. Building two
`DocumentStore`s over one file by hand is something tests and migrations legitimately do; what is
refused is *registering* two.
:::

## Sessions for a secondary store

A secondary store's sessions are reached **through the store**, not injected:

```cs
await using var session = reportingStore.LightweightSession();
```

`IDocumentSession` cannot be registered scoped for two stores at once. Polecat answers this the same
way; keyed registrations would give Fisher a shape neither sibling has for a case a property access
reads perfectly well.

## Targeted configuration

`IConfigureFisher<T>` says *which* store a contribution is about:

```cs
public class ReportingProjections : IConfigureFisher<IReportingStore>
{
    public void Configure(IServiceProvider services, StoreOptions options)
    {
        options.Projections.Add(new SalesProjection(), ProjectionLifecycle.Async);
    }
}
```

An untargeted `IConfigureFisher` reaches the primary store only. Without the distinction, a library's
configuration would reach stores it has never heard of.

## How the marker is implemented

The marker is implemented with `System.Reflection.DispatchProxy` — in the BCL, so no proxy library
and no code generation. A marker interface is empty apart from what it inherits, so every call it can
receive is one the wrapped store already implements.

Two consequences worth knowing:

- **Exceptions are unwrapped.** `TargetInvocationException` is rethrown with
  `ExceptionDispatchInfo`, so an exception from a secondary store arrives as itself rather than
  wrapped, and the proxy does not leak into your catch blocks.
- **A proxy is not an `IEventStore`.** `DispatchProxy` implements only the interfaces it was asked
  for, and the [tooling surfaces](/diagnostics) are implemented explicitly and deliberately absent
  from `IDocumentStore`. The `IEventStore` registration therefore reaches *through* the proxy to the
  real store, so a secondary store is still visible to a monitoring console.

`StoreName` defaults to the marker's name, so two stores are distinguishable in a monitoring tool and
in a trace with nothing said.
