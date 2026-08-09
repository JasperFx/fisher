using Fisher.Storage;
using JasperFx.Events.Daemon;
using Microsoft.Extensions.Logging;

namespace Fisher;

/// <summary>
///     The root of a Fisher store, as an interface: configuration, the database, session creation and
///     the escape hatches. Implemented by <see cref="DocumentStore" /> (fisher#45).
/// </summary>
/// <remarks>
///     <para>
///         Mirrors Polecat's <c>IDocumentStore</c>, which exists for the reasons any store-level
///         interface does — an application depends on the abstraction rather than the concrete type, and
///         a test can substitute it. Fisher had interfaces for the sessions
///         (<see cref="IQuerySession" />, <see cref="IDocumentSession" />) and none for the store, so
///         every consumer took a hard dependency on <see cref="DocumentStore" /> itself.
///     </para>
///     <para>
///         <b>It declares both <see cref="IDisposable" /> and <see cref="IAsyncDisposable" />, and that
///         is not politeness.</b> A <c>ServiceProvider</c> disposed synchronously refuses outright to
///         dispose a service offering only <see cref="IAsyncDisposable" /> — "type only implements
///         IAsyncDisposable" — which is the bug fisher#20 found the moment there was a container at all.
///         An interface declaring only the async form would reintroduce it one level up, where it would
///         be harder to see.
///     </para>
///     <para>
///         <b>What is deliberately not here:</b> the tooling surfaces. <see cref="DocumentStore" />
///         implements <c>JasperFx.Events.IEventStore</c>,
///         <c>IEventStore&lt;IDocumentSession, IQuerySession&gt;</c> and
///         <c>ISubscriptionRunner&lt;ISubscription&gt;</c> <em>explicitly</em>, precisely so a
///         monitoring-and-daemon surface does not crowd the store's own API. Re-exposing any of them
///         through this interface would undo that. A consumer who wants one casts to it, which is what
///         CritterWatch and the daemon already do.
///     </para>
///     <para>
///         <see cref="DocumentStore.For(System.Action{StoreOptions})" /> stays a static factory on the
///         concrete class and keeps returning <see cref="DocumentStore" />, mirroring Marten — a static
///         member cannot live on an interface usefully, and a caller building a store by hand knows
///         which store it is building.
///     </para>
/// </remarks>
public interface IDocumentStore : IDisposable, IAsyncDisposable
{
    /// <inheritdoc cref="DocumentStore.Options" />
    StoreOptions Options { get; }

    /// <inheritdoc cref="DocumentStore.Database" />
    FisherDatabase Database { get; }

    /// <inheritdoc cref="DocumentStore.Advanced" />
    AdvancedOperations Advanced { get; }

    /// <inheritdoc cref="DocumentStore.LightweightSession" />
    IDocumentSession LightweightSession(string? tenantId = null);

    /// <inheritdoc cref="DocumentStore.IdentitySession" />
    IDocumentSession IdentitySession(string? tenantId = null);

    /// <inheritdoc cref="DocumentStore.DirtyTrackedSession" />
    IDocumentSession DirtyTrackedSession(string? tenantId = null);

    /// <inheritdoc cref="DocumentStore.OpenSession" />
    IDocumentSession OpenSession(SessionOptions options);

    /// <inheritdoc cref="DocumentStore.QuerySession(string)" />
    IQuerySession QuerySession(string? tenantId = null);

    /// <inheritdoc cref="DocumentStore.QuerySession(string)" />
    IQuerySession QuerySession(SessionOptions options);

    /// <inheritdoc cref="DocumentStore.ApplyAllConfiguredChangesToDatabaseAsync" />
    Task ApplyAllConfiguredChangesToDatabaseAsync(CancellationToken token = default);

    /// <inheritdoc cref="DocumentStore.BuildProjectionDaemonAsync" />
    ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(
        string? tenantIdOrDatabaseIdentifier = null, ILogger? logger = null);
}
