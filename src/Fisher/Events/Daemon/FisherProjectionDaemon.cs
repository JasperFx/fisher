using Fisher.Projections;
using Fisher.Storage;
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
    }

    internal FisherProjectionDaemon(DocumentStore store, FisherDatabase database, ILoggerFactory loggerFactory,
        IHighWaterDetector detector)
        : base(store, database, loggerFactory, detector, store.Options.Projections)
    {
    }
}
