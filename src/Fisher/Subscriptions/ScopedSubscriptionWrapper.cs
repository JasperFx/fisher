using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;

namespace Fisher.Subscriptions;

/// <summary>
///     Presents a container-resolved <see cref="ISubscription" /> to the daemon, opening a fresh IoC
///     scope for each page of events (fisher#194).
/// </summary>
/// <remarks>
///     <para>
///         <b>Fisher writes this one rather than reusing a shared wrapper, and that is a gap rather
///         than a choice.</b> <c>JasperFx.Events.Subscriptions</c> does carry a
///         <c>ScopedSubscriptionServiceWrapper</c> generic over the store's session pair, but it is
///         <c>internal</c> and nothing in the library references it, so no consumer can reach it —
///         Marten carries its own copy for the same reason. The projection half of fisher#194 needed no
///         such copy: every wrapper in <c>JasperFx.Events.Projections.ContainerScoped</c> is public and
///         Fisher uses them unchanged.
///     </para>
///     <para>
///         <b>The scope is per page, and nothing is held between pages.</b> A subscription shard runs
///         for as long as the daemon does, which is longer than any scope in the process lives; the
///         only way a scoped dependency can be valid when it is used is for the scope to be opened,
///         used and disposed inside one <see cref="ProcessEventsAsync" /> call. That is the same
///         boundary the shared projection wrappers use, and it is why <c>Transient</c> and
///         <c>Scoped</c> register identically — the subscription is resolved afresh per page either
///         way.
///     </para>
///     <para>
///         The constructor's scope is separate and short: it exists only to read the inner
///         subscription's own <c>Name</c>, <c>Version</c>, <c>Options</c> and event filtering, because
///         it is <em>this wrapper</em> the daemon reads those from. Dropping them is how a scoped
///         subscription silently loses a <c>SubscribeFromPresent()</c> or a batch size its constructor
///         set — marten#4318, found the hard way over there.
///     </para>
/// </remarks>
internal sealed class ScopedSubscriptionWrapper<T> : SubscriptionBase where T : ISubscription
{
    private readonly IServiceProvider _provider;

    internal ScopedSubscriptionWrapper(IServiceProvider provider)
    {
        _provider = provider;
        Name = typeof(T).Name;

        var scope = _provider.CreateScope();
        try
        {
            if (scope.ServiceProvider.GetRequiredService<T>() is SubscriptionBase inner)
            {
                IncludedEventTypes.AddRange(inner.IncludedEventTypes);
                StreamType = inner.StreamType;
                IncludeArchivedEvents = inner.IncludeArchivedEvents;

                Options = inner.Options;
                Name = inner.Name;
                Version = inner.Version;
            }
        }
        finally
        {
            scope.SafeDispose();
        }
    }

    public override async Task<IDaemonChangeListener> ProcessEventsAsync(EventRange page,
        ISubscriptionController controller, IDocumentSession operations, CancellationToken cancellationToken)
    {
        var scope = _provider.CreateScope();

        try
        {
            var subscription = scope.ServiceProvider.GetRequiredService<T>();

            return await subscription
                .ProcessEventsAsync(page, controller, operations, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Awaited rather than fire-and-forget when the scope is async-disposable, because a scoped
            // dependency's DisposeAsync may well be the thing that flushes whatever the subscription
            // did — and the daemon commits the batch's progress the moment this returns.
            if (scope is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                scope.SafeDispose();
            }
        }
    }
}
