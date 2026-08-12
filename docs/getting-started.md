# Getting Started

Fisher integrates with the standard .NET `IServiceCollection` abstractions. Most features work
without an IoC container, but the async daemon and schema management use the `IHost` model.

## Installation

::: code-group

```shell [.NET CLI]
dotnet add package Fisher
```

```powershell [Powershell]
PM> Install-Package Fisher
```

:::

There is no server to install and nothing to run alongside your application. Fisher uses
[Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/), which ships
the SQLite engine itself.

Two companion packages are optional:

```shell
dotnet add package Fisher.AspNetCore          # streaming IResult types and ETag handling
dotnet add package Fisher.EntityFrameworkCore # a DbContext inside Fisher's transaction
```

## Registering Fisher

In your application startup, call `AddFisher()`:

<!-- snippet: sample_getting_started_add_fisher -->
<a id='snippet-sample_getting_started_add_fisher'></a>
```cs
services.AddFisher(options =>
    {
        // Any Microsoft.Data.Sqlite connection string. This one is a file beside the
        // application.
        options.Connection("Data Source=app.db");

        // SQLite has no schemas, so this folds into the table *prefix* instead:
        // "main" gives fi_events, anything else gives <name>_fi_events.
        options.DatabaseSchemaName = "main";
    })
    // Run the Weasel migration at startup so the tables exist before the first session.
    .ApplyAllDatabaseChangesOnStartup();
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/getting_started_samples.cs#L57-L70' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_getting_started_add_fisher' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

If you have async projections registered, opt the daemon into the host as well:

<!-- snippet: sample_getting_started_add_daemon -->
<a id='snippet-sample_getting_started_add_daemon'></a>
```cs
services.AddFisher(options =>
    {
        options.Connection("Data Source=app.db");
        options.Projections.Snapshot<Order>(SnapshotLifecycle.Async);
    })
    .ApplyAllDatabaseChangesOnStartup()
    .AddAsyncDaemon(DaemonMode.Solo);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/getting_started_samples.cs#L75-L83' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_getting_started_add_daemon' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

See [Bootstrapping Fisher](/configuration/hostbuilder) for every overload.

::: tip
`AddFisher()` registers `IDocumentStore` as a singleton, and `IDocumentSession` / `IQuerySession` as
scoped services. In most cases inject a session directly.
:::

::: warning
`DaemonMode.HotCold` is **refused**, and that is a real limitation rather than an omission. Hot-cold
failover means several nodes competing for a leadership lease through the database, and a Fisher
store is a file SQLite does not make safe to share across nodes. Accepting the mode and quietly
running `Solo` would give you the opposite of the guarantee you asked for.
:::

## Choosing a connection string

| Connection string | What it is |
| :--- | :--- |
| `Data Source=app.db` | A file beside the application. The ordinary choice. |
| `Data Source=/var/lib/app/app.db` | An absolute path. |
| `Data Source=:memory:` | A private in-memory database. |
| `Data Source=app;Mode=Memory;Cache=Shared` | A shared in-memory database. |

::: tip
An in-memory database lives only as long as something holds a connection open. Fisher's
`SqliteDataSource` is what keeps it alive for the store's lifetime, which is one of the reasons
production code should never do `new SqliteConnection(...)` of its own.
:::

## Working with Documents

Define a document type:

<!-- snippet: sample_getting_started_document_type -->
<a id='snippet-sample_getting_started_document_type'></a>
```cs
public class User
{
    public Guid Id { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public bool Internal { get; set; }
}
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/getting_started_samples.cs#L17-L25' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_getting_started_document_type' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

*For more on identity, see [Document Identity](/documents/identity).*

Use `IDocumentSession` to store:

<!-- snippet: sample_getting_started_store_a_document -->
<a id='snippet-sample_getting_started_store_a_document'></a>
```cs
var user = new User { FirstName = "Jane", LastName = "Doe", Internal = true };

session.Store(user);
await session.SaveChangesAsync(token);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/getting_started_samples.cs#L88-L93' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_getting_started_store_a_document' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

to query:

<!-- snippet: sample_getting_started_query_documents -->
<a id='snippet-sample_getting_started_query_documents'></a>
```cs
var internalUsers = await session.Query<User>()
    .Where(x => x.Internal)
    .OrderBy(x => x.LastName)
    .ToListAsync(token);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/getting_started_samples.cs#L95-L100' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_getting_started_query_documents' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and to load by id:

<!-- snippet: sample_getting_started_load_by_id -->
<a id='snippet-sample_getting_started_load_by_id'></a>
```cs
var loaded = await session.LoadAsync<User>(user.Id, token);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/getting_started_samples.cs#L102-L104' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_getting_started_load_by_id' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

See [Querying Documents](/documents/querying/) for the whole surface.

## Working with Events

<!-- snippet: sample_getting_started_events -->
<a id='snippet-sample_getting_started_events'></a>
```cs
public record OrderPlaced(string Customer, decimal Total);

public record OrderShipped(DateTimeOffset ShippedAt);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/getting_started_samples.cs#L27-L31' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_getting_started_events' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: sample_getting_started_events_round_trip -->
<a id='snippet-sample_getting_started_events_round_trip'></a>
```cs
await using var session = store.LightweightSession();

// StartStream hands back a StreamAction; its Id is the stream's identity.
var stream = session.Events.StartStream<Order>(
    new OrderPlaced("Acme Corp", 199.95m),
    new OrderShipped(DateTimeOffset.UtcNow));

await session.SaveChangesAsync(token);

var order = await session.Events.AggregateStreamAsync<Order>(stream.Id, token: token);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/getting_started_samples.cs#L112-L123' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_getting_started_events_round_trip' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

See the [Event Store quick start](/events/quickstart) for a complete walkthrough.

## Creating a Standalone Store

You do not need a host at all:

<!-- snippet: sample_getting_started_standalone_store -->
<a id='snippet-sample_getting_started_standalone_store'></a>
```cs
await using var store = DocumentStore.For("Data Source=app.db");
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/getting_started_samples.cs#L130-L132' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_getting_started_standalone_store' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or with full configuration:

<!-- snippet: sample_getting_started_standalone_store_configured -->
<a id='snippet-sample_getting_started_standalone_store_configured'></a>
```cs
await using var configured = DocumentStore.For(opts =>
{
    opts.Connection("Data Source=app.db");
    opts.DatabaseSchemaName = "reporting";
    opts.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
});

await configured.ApplyAllConfiguredChangesToDatabaseAsync(token);
```
<sup><a href='https://github.com/JasperFx/fisher/blob/main/src/Fisher.Tests/Documentation/getting_started_samples.cs#L134-L143' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_getting_started_standalone_store_configured' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
`DocumentStore` implements both `IDisposable` and `IAsyncDisposable`. Disposing it also releases the
process-wide pooled connections for its connection string — see
[Releasing pooled connections](/configuration/sqlite#releasing-pooled-connections).
:::

## A note on WAL

Fisher turns on [write-ahead logging](https://sqlite.org/wal.html) by default, and it matters: WAL is
what lets the async daemon read while a session writes. If you override the PRAGMA settings and turn
it off, the daemon logs a warning at startup rather than refusing to run — because without WAL a
store still projects correctly, it just serialises every reader against every writer, which presents
as a slow projection rather than as a misconfiguration.

See [SQLite and PRAGMA Settings](/configuration/sqlite).
