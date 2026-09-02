# Bootstrapping Fisher

Fisher provides `AddFisher()` extension methods on `IServiceCollection`.

## Basic Registration

```cs
builder.Services.AddFisher(options =>
{
    options.Connection("Data Source=app.db");
});
```

## Registration Overloads

```cs
// Connection string only
builder.Services.AddFisher("Data Source=app.db");

// Action-based configuration
builder.Services.AddFisher(options =>
{
    options.Connection("Data Source=app.db");
    options.DatabaseSchemaName = "reporting";
});

// Factory-based, with access to the IServiceProvider
builder.Services.AddFisher(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var opts = new StoreOptions();
    opts.Connection(config.GetConnectionString("Fisher")!);
    return opts;
});
```

## Registered Services

| Service | Lifetime | Description |
| :--- | :--- | :--- |
| `IDocumentStore` | Singleton | Main entry point; creates sessions |
| `DocumentStore` | Singleton | The same instance, for code that resolves the concrete type |
| `ISessionFactory` | Singleton | Decides which session flavor scoped resolution gets |
| `IDocumentSession` | Scoped | Read/write session with a unit of work |
| `IQuerySession` | Scoped | Read session |

::: tip
Everything Fisher hands the container implements `IDisposable` **and** `IAsyncDisposable`. That is
not politeness: a `ServiceProvider` disposed synchronously refuses outright to dispose a service
offering only `IAsyncDisposable`, which would make a scoped session unusable rather than merely less
efficient.
:::

## Startup Options

`AddFisher()` returns a `FisherConfigurationExpression` carrying the host opt-ins:

```cs
builder.Services.AddFisher(options =>
{
    options.Connection("Data Source=app.db");
    options.InitialData.Add(new SeedReferenceData());
    options.Projections.Snapshot<Order>(SnapshotLifecycle.Async);
})
.ApplyAllDatabaseChangesOnStartup()
.SeedInitialDataOnStartup()
.AddAsyncDaemon(DaemonMode.Solo);
```

### ApplyAllDatabaseChangesOnStartup

Runs the Weasel migration once at startup so every table exists before the first session opens.

::: warning
`AutoCreate.None` wins over this registration. The hosted service starts and does nothing, rather
than the registration quietly overriding your schema policy.
:::

### SeedInitialDataOnStartup

Runs every registered `IInitialData`. See [Initial Baseline Data](/documents/initial-data).

::: warning
This **refuses to be registered before** `ApplyAllDatabaseChangesOnStartup()`. Hosted services start
in registration order, so the other way round writes to tables that do not exist yet — and that
presents as `no such table`, which names the table and not the mistake.
:::

### AddAsyncDaemon

Hosts the [async projection daemon](/events/projections/async-daemon).

```cs
.AddAsyncDaemon(DaemonMode.Solo)
```

| Mode | Behaviour |
| :--- | :--- |
| `Solo` | Starts the daemon in this process. |
| `Disabled` | Registers nothing. |
| `ExternallyManaged` | Registers nothing; you build and run the daemon yourself. |
| `HotCold` | **Refused.** See below. |

::: danger
`DaemonMode.HotCold` throws. Hot-cold failover means several nodes competing for a leadership lease
through the database, and a Fisher store is a file that SQLite does not make safe to share across
nodes. Accepting the mode and running `Solo` would give an application the opposite of the guarantee
it asked for — every node projecting at once.
:::

The daemon hosted service also logs the WAL warning at startup, which is the only place an operator
would otherwise see it.

## IConfigureFisher

Implement `IConfigureFisher` to modularise configuration — useful when a library contributes its own
document types or projections:

```cs
public class ReportingConfiguration : IConfigureFisher
{
    public void Configure(IServiceProvider services, StoreOptions options)
    {
        options.Schema.For<Report>().Duplicate(x => x.RunAt);
        options.Projections.Snapshot<Report>(SnapshotLifecycle.Async);
    }
}
```

Register it before `AddFisher()`:

```cs
builder.Services.AddSingleton<IConfigureFisher, ReportingConfiguration>();
builder.Services.AddFisher(options => options.Connection("Data Source=app.db"));
```

An untargeted `IConfigureFisher` reaches the **primary** store only. To contribute to a secondary
store, use the targeted `IConfigureFisher<T>` — see [Multiple Stores](/configuration/multiple-stores).

### ConfigureFisher(...)

For a contribution that does not warrant a class of its own, `ConfigureFisher(...)` is the lambda
form of the same seam — and the same surface Marten's `ConfigureMarten` and Polecat's
`ConfigurePolecat` present, so integration code that layers its own options onto a store somebody
else registered reads alike across the three stores:

<!-- snippet: sample_configure_fisher_lambda -->
<a id='snippet-sample_configure_fisher_lambda'></a>
```cs
// Layered onto whatever store the application configured, either side of the AddFisher call.
services.ConfigureFisher(options =>
{
    options.Schema.For<Report>().Duplicate(x => x.RunAt);
    options.Projections.Snapshot<Report>(SnapshotLifecycle.Async);
});

// The overload taking the container as well, for configuration that needs a resolved service.
services.ConfigureFisher((serviceProvider, options) =>
{
    options.Projections.Add(
        serviceProvider.GetRequiredService<SalesProjection>(), ProjectionLifecycle.Async);
});
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/configuration_samples.cs#L29-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configure_fisher_lambda' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Contributions run after the `AddFisher(...)` lambda, in registration order, and may be registered
either side of it — they are resolved when the store is built, not when the call is made.

## Host-level JasperFx options

`AddJasperFx(...)` configures the whole application rather than one store, and Fisher reads it when
each store is built:

```cs
builder.Services.AddJasperFx(o => o.EnableAdvancedTracking = true);
builder.Services.AddFisher(options => options.Connection("Data Source=app.db"));
```

`EnableAdvancedTracking` is the switch a CritterWatch host throws, and it turns on
`Events.EnableExtendedProgressionTracking` for **every** Fisher store the container builds — the
primary and every `AddFisherStore<T>` alike — so extended per-shard state reaches a monitoring
console without naming each store.

::: tip
It only ever **adds**. A host that leaves `EnableAdvancedTracking` at its default does not switch off
a store that asked for extended tracking in its own configuration, and the read runs *after* the
`IConfigureFisher` chain so a per-store contribution cannot clobber the host's opt-in.
:::

::: warning
Before 1.0.6 Fisher never read `JasperFxOptions` at all, so this switch lit up Marten and Polecat and
silently did nothing here ([#141](https://github.com/JasperFx/fisher/issues/141)). A console then
showed no per-shard state for Fisher stores with nothing to indicate why, which is the same failure
shape as having nothing to report.
:::

### The application-assembly reuse warning

JasperFx pins the application assembly used for discovery **process-wide**, to whichever Critter Stack
host starts first. A later host registered from a different assembly therefore gets discovery over
somebody else's assembly, and JasperFx sets a warning saying so. Fisher logs it once per container
when the store is built.

::: tip
It typically only bites a **test harness** that stands up several hosts across different assemblies.
The symptom without the warning is a type this host registered simply not being discovered — silent,
and dependent on which host happened to start first. Set the application assembly explicitly on the
later host if you hit it.
:::

## Session Factories

By default, the scoped `IDocumentSession` is a lightweight session. Supply your own `ISessionFactory`
to change that — for instance to give every request a dirty-tracked session, or to stamp the current
user onto the unit of work:

```cs
public class UserSessionFactory : ISessionFactory
{
    private readonly IDocumentStore _store;
    private readonly IHttpContextAccessor _http;

    public UserSessionFactory(IDocumentStore store, IHttpContextAccessor http)
    {
        _store = store;
        _http = http;
    }

    public IQuerySession QuerySession() => _store.QuerySession();

    public IDocumentSession OpenSession()
    {
        var session = _store.DirtyTrackedSession();
        session.CurrentUserName = _http.HttpContext?.User.Identity?.Name;
        return session;
    }
}
```

```cs
builder.Services.AddSingleton<ISessionFactory, UserSessionFactory>();
```
