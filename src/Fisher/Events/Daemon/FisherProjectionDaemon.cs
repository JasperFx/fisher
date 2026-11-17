using Fisher.Projections;
using Fisher.Storage;
using JasperFx;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Daemon.HighWater;
using Microsoft.Extensions.Logging;

namespace Fisher.Events.Daemon;

/// <summary>
///     Fisher's async projection daemon.
/// </summary>
/// <remarks>
///     Thin on purpose, exactly as Polecat's is. Everything that runs — the high-water agent, the
///     subscription agents, the shard tracker, the retry and throttling — belongs to
///     <see cref="JasperFxAsyncDaemon{TOperations,TQuerySession,TProjection}" />. This type only closes
///     that base over Fisher's session pair and hands it the store, the database, and the detector.
/// </remarks>
public class FisherProjectionDaemon
    : JasperFxAsyncDaemon<IDocumentSession, IQuerySession, IProjection>, IProjectionDaemon
{
    internal FisherProjectionDaemon(DocumentStore store, FisherDatabase database, ILogger logger,
        IHighWaterDetector detector)
        : base(store, database, logger, detector, store.Options.Projections)
    {
        _store = store;
    }

    internal FisherProjectionDaemon(DocumentStore store, FisherDatabase database, ILoggerFactory loggerFactory,
        IHighWaterDetector detector)
        : base(store, database, loggerFactory, detector, store.Options.Projections)
    {
        _store = store;
    }

    private readonly DocumentStore _store;

    /// <inheritdoc cref="RebuildIdentityAsync(string, IdentityRebuildRequest, CancellationToken)" />
    public new Task<IdentityRebuildResult> RebuildIdentityAsync(string projectionName, string? tenantId,
        object identity, CancellationToken token)
        => RebuildIdentityAsync(projectionName,
            new IdentityRebuildRequest(tenantId ?? StorageConstants.DefaultTenantId, identity), token);

    /// <summary>
    ///     Rebuild one identity of a single-stream projection inside the shard that runs it (fisher#629,
    ///     jasperfx#1201). JasperFx's, with one refusal of Fisher's own in front of it.
    /// </summary>
    /// <remarks>
    ///     <b>A projection whose documents live in storage registered through
    ///     <see cref="FisherProjectionOptions.StorageProviders" /> is refused with
    ///     <see cref="IdentityRebuildRefusal.NotSupportedByStore" />.</b> The re-fold clears the identity through
    ///     <c>IProjectionStorage.HardDeleteById</c> inside the shard's own batch, and storage Fisher does not own
    ///     (an EF Core entity, say) need not implement it. Finding that out inside the batch would fail the batch
    ///     and stop the shard, so it is refused here, before anything is queued. Rebuild such a projection with
    ///     <c>RebuildProjectionAsync</c>.
    /// </remarks>
    public new Task<IdentityRebuildResult> RebuildIdentityAsync(string projectionName,
        IdentityRebuildRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(projectionName);
        ArgumentNullException.ThrowIfNull(request);

        if (_store.Options.Projections.TryFindProjection(projectionName, out var source)
            && source is IAggregateProjection aggregate
            && _store.Options.Projections.StorageProviders.HasProviderFor(aggregate.AggregateType))
        {
            return Task.FromException<IdentityRebuildResult>(new IdentityRebuildRefusedException(projectionName,
                request.Identity, IdentityRebuildRefusal.NotSupportedByStore,
                $"'{aggregate.AggregateType.Name}' is kept in storage registered through Projections.StorageProviders, "
                + "which Fisher cannot clear inside the shard's batch. Rebuild the projection with RebuildProjectionAsync."));
        }

        return base.RebuildIdentityAsync(projectionName, request, token);
    }
}
