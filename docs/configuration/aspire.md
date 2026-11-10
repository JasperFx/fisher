# Hosting with .NET Aspire

SQLite is a file, so Aspire has nothing to start for Fisher: the AppHost's job is to say *which
file* and hand that to the service. Two routes.

## `Fisher.Aspire.Hosting`

```shell
dotnet add package Fisher.Aspire.Hosting
```

In the AppHost:

```csharp
var orders = builder.AddFisher("orders", "/data/orders.db"); // path optional: defaults to ./orders.db

builder.AddProject<Projects.Api>("api")
    .WithReference(orders)   // ConnectionStrings__orders = "Data Source=/data/orders.db"
    .WithVolume(orders);     // FISHER_DATA_DIRECTORY = /data
```

In the service, the connection string is an ordinary SQLite one:

```csharp
builder.Services.AddFisher(builder.Configuration.GetConnectionString("orders")!);
```

`WithVolume` sets `FISHER_DATA_DIRECTORY` on a project or container so the service can find the
directory a mounted volume provides. It does not mount anything itself.

## Community Toolkit `AddSqliteConnection`

If the AppHost already uses the Aspire Community Toolkit's SQLite integration
, a connection string it produces is an ordinary SQLite one, so
`AddFisher(connectionString)` can take it (not verified against every toolkit version). Fisher then owns the schema, journal mode and
PRAGMAs through its own data source, so leave the toolkit's own settings at their defaults.

## OpenTelemetry

Fisher emits an `ActivitySource` and a `Meter` both named `Fisher`. Register them with the service
defaults' OpenTelemetry setup:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("Fisher"))
    .WithMetrics(m => m.AddMeter("Fisher"));
```

Metric instruments are opt-in through `StoreOptions.OpenTelemetry`; see [Diagnostics](../diagnostics).

## Health check

With `Fisher.AspNetCore`, `AddHealthChecks().AddFisherHighWaterHealthCheck()` reports a stuck
async daemon, which the Aspire dashboard shows on the service's tile.
