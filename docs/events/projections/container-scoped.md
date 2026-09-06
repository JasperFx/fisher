# Container-Scoped Projections

A projection or subscription built by the application's **IoC container**, so it can take injected
services — an `HttpClient`, an `ILogger<T>`, a scoped repository, anything the container knows how to
build.

```cs
builder.Services.AddFisher(opts =>
{
    opts.ConnectionString = connectionString;
})
.AddProjectionWithServices<OrderSummaryProjection>(
    ProjectionLifecycle.Inline, ServiceLifetime.Scoped)
.AddSubscriptionWithServices<OrderNotifier>(ServiceLifetime.Scoped)
.AddAsyncDaemon(DaemonMode.Solo);
```

The projection itself is an ordinary one. Nothing about it says it is container-built:

```cs
public partial class OrderSummaryProjection : SingleStreamProjection<OrderSummary, Guid>
{
    private readonly IPricingService _pricing;

    public OrderSummaryProjection(IPricingService pricing) => _pricing = pricing;

    public OrderSummary Create(IEvent<OrderPlaced> e) =>
        new() { Id = e.StreamId, Total = _pricing.Quote(e.Data.Sku) };
}
```

Every projection base class works — `SingleStreamProjection<TDoc, TId>`,
`MultiStreamProjection<TDoc, TId>`, `EventProjection`, and a bare `IProjection`.

## Scope lifetime — the thing to understand

**An async projection outlives every request scope in the process.** It runs from a hosted service,
where there is no request at all, and it keeps running for as long as the store does. So a
container-scoped projection cannot simply be resolved once and held: the service graph behind it
would come from a scope disposed long before the daemon's next batch, and the first scoped dependency
touched would throw `ObjectDisposedException`. Nor can the wrapper open a scope and keep it — that
leaks one scope, and everything in it, for the life of the process.

**Fisher's answer is a scope per unit of work.** A fresh `IServiceScope` is opened, the projection is
resolved inside it, and the scope is disposed before control returns — once per inline
`SaveChangesAsync`, once per daemon page, once per slicing pass. Nothing is held across a batch
boundary, so nothing is resolved from a disposed provider and nothing accumulates.

::: tip
Two consequences worth planning around. Your projection's constructor runs **once per batch**, so keep
it cheap — the expensive thing belongs in a singleton dependency. And a projection instance never sees
more than one batch, so anything it must remember between batches belongs in a dependency or in the
projected document, never in a field.
:::

This is also why `ServiceLifetime.Transient` and `ServiceLifetime.Scoped` behave identically here: the
wrapper resolves afresh per batch either way, so the two lifetimes describe the same behaviour.

| Lifetime | What is registered | Scopes opened |
|---|---|---|
| `Singleton` | the resolved projection itself | none |
| `Scoped` | a wrapper around the projection | one per unit of work |
| `Transient` | the same wrapper | one per unit of work |

`Singleton` is the cheapest and is correct whenever the projection's dependencies are themselves
singletons. It is registered directly, with no wrapper at all.

## Registering without the `AddFisher` chain

The `AddFisher(...)` chain is consumed once, at the composition root, which is inconvenient in a
modular monolith where each module wants to register its own projections. The bare
`IServiceCollection` extensions do the same job from anywhere:

```cs
// In a module's own registration, long after AddFisher was called
services.AddProjectionWithServices<OrderSummaryProjection>(
    ProjectionLifecycle.Async, ServiceLifetime.Scoped);

services.AddSubscriptionWithServices<OrderNotifier>(ServiceLifetime.Scoped);
```

Both have a `TStore` overload for a [secondary store](/configuration/multiple-stores):

```cs
services.AddProjectionWithServices<OrderSummaryProjection, IReportingStore>(
    ProjectionLifecycle.Async, ServiceLifetime.Scoped);
```

## Subscriptions

A [subscription](/events/subscriptions) is always asynchronous, so there is no lifecycle argument —
only the IoC lifetime, which means exactly what it means for a projection. A `Scoped` subscription
gets a fresh scope per page of events, disposed before the daemon commits that page's progress.

```cs
public class OrderNotifier : SubscriptionBase
{
    private readonly INotificationClient _client;

    public OrderNotifier(INotificationClient client)
    {
        _client = client;
        Options.BatchSize = 100;
    }

    public override async Task<IDaemonChangeListener> ProcessEventsAsync(EventRange page,
        ISubscriptionController controller, IDocumentSession operations, CancellationToken token)
    {
        foreach (var placed in page.Events.Select(x => x.Data).OfType<OrderPlaced>())
        {
            await _client.NotifyAsync(placed.Sku, token);
        }

        return NullDaemonChangeListener.Instance;
    }
}
```

::: warning
Options a subscription sets in its own constructor — `Name`, `Version`, `Options.BatchSize`,
`SubscribeFromPresent()`, event filtering — are read off the **wrapper**, not off the subscription, so
Fisher copies them across at registration. If you find one being ignored, that copy is where to look.
:::

## Where the machinery lives

Almost none of this is Fisher's. The container-scoped wrappers are
`JasperFx.Events.Projections.ContainerScoped`, shared with Marten and generic over the store's session
types — so what Fisher supplies is the registration surface and the dispatch that picks the right
wrapper for the kind of projection being registered. The one piece written here is the subscription
wrapper, because the shared library's equivalent is `internal`.

One thing Fisher's registration does that its sibling's does not: a container-scoped projection's
**published document type is mapped into the schema**, so its table is created with the rest of the
migration. Without that the projection would work against a table that was never created.
