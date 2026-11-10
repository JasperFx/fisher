# Initial Baseline Data

Seed reference data when the application starts.

<!-- snippet: sample_documents_initial_data -->
<a id='snippet-sample_documents_initial_data'></a>
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
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/document_samples.cs#L115-L128' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_documents_initial_data' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

```cs
builder.Services.AddFisher(opts =>
{
    opts.Connection("Data Source=app.db");
    opts.InitialData.Add(new SeedCountries());
})
.ApplyAllDatabaseChangesOnStartup()
.SeedInitialDataOnStartup();
```

## Seeding runs after the schema step

The startup opt-ins configure one activator per store, which applies (or asserts) the schema and then
runs the seeders, in that order, whichever of the calls came first. Fisher 1.x started one hosted service
per call in registration order, and refused `SeedInitialDataOnStartup()` before
`ApplyAllDatabaseChangesOnStartup()` for that reason; the refusal went with the cause.

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
