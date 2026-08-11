using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using Microsoft.Extensions.Logging;

namespace Fisher.Projections;

/// <summary>
///     Presents a bare <see cref="IProjection" /> as something a composite can hold (fisher#19).
/// </summary>
/// <remarks>
///     <para>
///         <see cref="IProjection" /> is <c>IJasperFxProjection&lt;IDocumentSession&gt;</c> — it applies
///         events and nothing else. A composite stage holds an
///         <c>IProjectionSource&lt;IDocumentSession, IQuerySession&gt;</c>, which also knows its shards,
///         its version and how to build an execution. This supplies all of that around the projection.
///     </para>
///     <para>
///         <b>The execution deliberately does not dispose the batch it is handed.</b> Every stage of a
///         composite writes into one batch so the whole composite commits together — the composite owns
///         that lifecycle, and a stage disposing it would commit the earlier stages and leave the later
///         ones writing into a disposed session.
///     </para>
///     <para>
///         Ported from Polecat's type of the same name.
///     </para>
/// </remarks>
internal class CompositeIProjectionSource :
    ProjectionBase,
    IProjectionSource<IDocumentSession, IQuerySession>,
    ISubscriptionFactory<IDocumentSession, IQuerySession>
{
    private readonly IProjection _projection;

    public CompositeIProjectionSource(IProjection projection)
    {
        _projection = projection;
        Lifecycle = ProjectionLifecycle.Async;
        Name = projection.GetType().Name;
        Version = 1;
        if (_projection.GetType().TryGetAttribute<ProjectionVersionAttribute>(out var att))
        {
            Version = att.Version;
        }

        // fisher#63: adopt the wrapped projection's options, the way JasperFx's ProjectionWrapper and
        // ScopedProjectionWrapper both do. Without it this wrapper keeps the empty AsyncOptions it was
        // constructed with, and the rebuild teardown — which asks each composite member what it
        // publishes — enumerates nothing for this one. The progression rows are still deleted, so the
        // rebuild restarts from zero and replays on top of the previous run's documents.
        //
        // Name and Version are deliberately NOT adopted: they compose this member's shard identity, and
        // changing them would orphan every progression row already written under the old one.
        //
        // A raw IProjection that is not a ProjectionBase declares neither its storage nor its teardown,
        // and there is nothing here to invent one from — FisherCompositeProjection.Add's configure
        // overload is where that is said instead.
        if (projection is ProjectionBase source)
        {
            replaceOptions(source.Options);

            foreach (var publishedType in source.PublishedTypes())
            {
                RegisterPublishedType(publishedType);
            }
        }
    }

    public SubscriptionType Type => SubscriptionType.EventProjection;
    public ShardName[] ShardNames() => [new ShardName(Name, ShardName.All, Version)];
    public Type ImplementationType => _projection.GetType();
    public SubscriptionDescriptor Describe(IEventStore store) => new(this, store);

    public IReadOnlyList<AsyncShard<IDocumentSession, IQuerySession>> Shards()
    {
        return
        [
            new AsyncShard<IDocumentSession, IQuerySession>(Options, ShardRole.Projection,
                new ShardName(Name, "All", Version), this, this)
        ];
    }

    public bool TryBuildReplayExecutor(IEventStore<IDocumentSession, IQuerySession> store, IEventDatabase database,
        [NotNullWhen(true)] out IReplayExecutor? executor)
    {
        executor = default;
        return false;
    }

    IInlineProjection<IDocumentSession> IProjectionSource<IDocumentSession, IQuerySession>.BuildForInline()
    {
        throw new NotSupportedException("CompositeIProjectionSource does not support inline execution");
    }

    public ISubscriptionExecution BuildExecution(IEventStore<IDocumentSession, IQuerySession> store,
        IEventDatabase database, ILoggerFactory loggerFactory, ShardName shardName)
    {
        return new CompositeIProjectionExecution(_projection, shardName);
    }

    public ISubscriptionExecution BuildExecution(IEventStore<IDocumentSession, IQuerySession> store,
        IEventDatabase database, ILogger logger, ShardName shardName)
    {
        return new CompositeIProjectionExecution(_projection, shardName);
    }
}

/// <summary>
///     One stage's execution: apply the range's events, per tenant, into the composite's batch.
/// </summary>
internal class CompositeIProjectionExecution : ISubscriptionExecution
{
    private readonly IProjection _projection;

    public CompositeIProjectionExecution(IProjection projection, ShardName shardName)
    {
        _projection = projection;
        ShardName = shardName;
    }

    public ShardName ShardName { get; }
    public ShardExecutionMode Mode { get; set; }

    public async Task ProcessRangeAsync(EventRange range)
    {
        var batch = range.ActiveBatch as IProjectionBatch<IDocumentSession, IQuerySession>;
        if (batch == null) return;

        var groups = range.Events.GroupBy(x => x.TenantId).ToArray();
        foreach (var group in groups)
        {
            await using var session = batch.SessionForTenant(group.Key);
            await _projection.ApplyAsync(session, group.ToList(), CancellationToken.None).ConfigureAwait(false);
        }
    }

    public ValueTask EnqueueAsync(EventPage page, ISubscriptionAgent subscriptionAgent) => new();
    public Task StopAndDrainAsync(CancellationToken token) => Task.CompletedTask;
    public Task HardStopAsync() => Task.CompletedTask;

    public bool TryBuildReplayExecutor([NotNullWhen(true)] out IReplayExecutor? executor)
    {
        executor = default;
        return false;
    }

    public Task ProcessImmediatelyAsync(SubscriptionAgent subscriptionAgent, EventPage events,
        CancellationToken cancellation) => Task.CompletedTask;

    public bool TryGetAggregateCache<TId, TDoc>([NotNullWhen(true)] out IAggregateCaching<TId, TDoc>? caching)
    {
        caching = null;
        return false;
    }

    public ValueTask DisposeAsync() => new();
}
