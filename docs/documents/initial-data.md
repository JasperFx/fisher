# Initial Baseline Data

Seed reference data when the application starts.

```cs
public class SeedCountries : IInitialData
{
    public async Task Populate(IDocumentStore store, CancellationToken token)
    {
        await using var session = store.LightweightSession();

        session.Store(new Country { Id = "no", Name = "Norway" });
        session.Store(new Country { Id = "se", Name = "Sweden" });

        await session.SaveChangesAsync(token);
    }
}
```

```cs
builder.Services.AddFisher(opts =>
{
    opts.Connection("Data Source=app.db");
    opts.InitialData.Add(new SeedCountries());
})
.ApplyAllDatabaseChangesOnStartup()   // must come first
.SeedInitialDataOnStartup();
```

## Order matters, and Fisher enforces it

::: warning
`SeedInitialDataOnStartup()` **refuses to be registered before** `ApplyAllDatabaseChangesOnStartup()`.

Hosted services start in registration order, so the other way round writes to tables that do not exist
yet — and that presents as `no such table`, which names the table and not the mistake.
:::

## There is no "already seeded" marker

::: tip
Deliberately. A seeder that upserts by a known id is idempotent for free, which is what every useful
seeder does — and a marker table would be a table nobody asked for holding a claim Fisher cannot
verify. Both siblings say the same.
:::

So write your seeders to be safely re-runnable:

```cs
// Good: an upsert by a known id
session.Store(new Country { Id = "no", Name = "Norway" });

// Bad: unconditional inserts with generated ids
session.Insert(new Country { Id = Guid.NewGuid().ToString(), Name = "Norway" });
```

## Seeding without the host

```cs
foreach (var seeder in store.Options.InitialData)
{
    await seeder.Populate(store, token);
}
```

## Multi-tenancy

An `IInitialData` gets the **store**, so it decides which tenants it seeds:

```cs
public async Task Populate(IDocumentStore store, CancellationToken token)
{
    foreach (var tenantId in _tenants)
    {
        await using var session = store.LightweightSession(tenantId);
        session.Store(new Country { Id = "no", Name = "Norway" });
        await session.SaveChangesAsync(token);
    }
}
```

Under [database-per-tenant](/configuration/multitenancy#database-per-tenant) that is a session per
file, so each seeds its own database.
