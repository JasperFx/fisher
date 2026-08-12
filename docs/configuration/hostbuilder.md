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
