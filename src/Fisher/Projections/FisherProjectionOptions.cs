using System.Diagnostics.CodeAnalysis;
using Fisher.Events;
using JasperFx.Core.Reflection;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;

namespace Fisher.Projections;

/// <summary>
///     Projection registration and configuration for Fisher — <c>StoreOptions.Projections</c>.
/// </summary>
/// <remarks>
///     <para>
///         All three lifecycles work: Live goes through the aggregator cache behind
///         <see cref="ProjectionGraph{TProjection,TOperations,TQuerySession}.AggregatorFor{T}" />, Inline
///         is applied during <c>SaveChangesAsync</c> in the same transaction as the events, and Async is
///         run by the projection daemon. The graph also carries <c>DiscoverGeneratedEvolvers</c> /
///         <c>AllAggregateTypes</c>, which report the self-aggregating types whose evolvers the source
///         generator emitted.
///     </para>
///     <para>
///         Standing this up was cheap only because the write surface work already paid its
///         prerequisites: <c>ProjectionGraph</c> constrains the write session to be both the read
///         session and an <c>IStorageOperations</c>, which <see cref="IDocumentSession" /> became when
///         live aggregation landed.
///     </para>
/// </remarks>
public class FisherProjectionOptions : ProjectionGraph<IProjection, IDocumentSession, IQuerySession>
{
    private readonly EventGraph _events;

    internal FisherProjectionOptions(EventGraph events) : base(events, "fisher")
    {
        _events = events;
    }

    /// <summary>
    ///     Register every event type a newly added projection handles, so a process that only reads can
    ///     still resolve those events by name.
    /// </summary>
    protected override void onAddProjection(object projection)
    {
        if (projection is ProjectionBase projectionBase)
        {
            foreach (var eventType in projectionBase.IncludedEventTypes)
            {
                _events.AddEventType(eventType);
            }
        }
    }

    /// <summary>
    ///     Register a self-aggregating document type to be snapshotted — the equivalent of Marten's
    ///     <c>Projections.Snapshot&lt;T&gt;()</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="SnapshotLifecycle.Inline" /> writes the snapshot in the same transaction as the
    ///         events that produced it; <see cref="SnapshotLifecycle.Async" /> hands it to the projection
    ///         daemon, which writes it in its own transaction alongside the shard's progress. Registering
    ///         the document mapping here is what gets the snapshot's table created with the rest of the
    ///         schema either way.
    ///     </para>
    /// </remarks>
    /// <typeparam name="T">
    ///     The aggregate type. It must be self-aggregating — carrying its own <c>Create</c> /
    ///     <c>Apply</c> methods. Use <c>Add</c> for a projection class.
    /// </typeparam>
    [RequiresDynamicCode("Closes SingleStreamProjection<,> over (T, T's id type) via Type.MakeGenericType.")]
    [RequiresUnreferencedCode("Resolves T's identity member reflectively through AggregateIdentity.")]
    public void Snapshot<T>(SnapshotLifecycle lifecycle) where T : notnull
    {
        if (typeof(T).CanBeCastTo<ProjectionBase>())
        {
            throw new InvalidOperationException(
                "Snapshot<T> is for self-aggregating document types. Register a projection class with " +
                $"Add() instead of {typeof(T).FullNameInCode()}.");
        }

        var projectionLifecycle = lifecycle == SnapshotLifecycle.Async
            ? ProjectionLifecycle.Async
            : ProjectionLifecycle.Inline;

        // Closed over the aggregate's own identity type, not the stream identity primitive — the same
        // rule live aggregation follows, and for the same source-generator reason. See CLAUDE.md.
        var idType = Storage.AggregateIdentity.ResolveIdType(typeof(T), _events.StreamIdentity);
        var source = typeof(SingleStreamProjection<,>)
            .CloseAndBuildAs<ProjectionBase>(typeof(T), idType);

        source.Lifecycle = projectionLifecycle;
        source.AssembleAndAssertValidity();

        // The snapshot needs somewhere to live; registering the mapping puts its table in the schema.
        _events.Options.Schema.MappingFor(typeof(T));

        Add((IProjectionSource<IDocumentSession, IQuerySession>)source, projectionLifecycle);
    }

    /// <summary>
    ///     Register several projections as one composite, running in ordered stages.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Always asynchronous — see <see cref="FisherCompositeProjection" /> for why a stage
    ///         boundary only means something inside a daemon batch.
    ///     </para>
    ///     <para>
    ///         The child projections' event types are registered on the event graph here rather than by
    ///         each child, because a child inside a composite is never registered on its own and would
    ///         otherwise contribute nothing to what the store knows how to deserialize.
    ///     </para>
    /// </remarks>
    public void CompositeProjectionFor(string name, Action<FisherCompositeProjection> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var composite = new FisherCompositeProjection(name, _events.Options);
        configure(composite);
        composite.AssembleAndAssertValidity();

        foreach (var child in composite.AllProjections().OfType<ProjectionBase>())
        {
            foreach (var eventType in child.IncludedEventTypes)
            {
                _events.AddEventType(eventType);
            }
        }

        All.Add(composite);
    }

    /// <summary>
    ///     Register an already-built projection.
    /// </summary>
    public void Add(ProjectionBase projection, ProjectionLifecycle lifecycle)
    {
        projection.Lifecycle = lifecycle;
        projection.AssembleAndAssertValidity();

        foreach (var published in projection.As<IProjectionSource<IDocumentSession, IQuerySession>>()
                     .PublishedTypes())
        {
            _events.Options.Schema.MappingFor(published);
        }

        Add((IProjectionSource<IDocumentSession, IQuerySession>)projection, lifecycle);
    }

    /// <summary>
    ///     Register a subscription — arbitrary code the async daemon drives over each range of events,
    ///     rather than a projection folding them into storage (fisher#21).
    /// </summary>
    /// <param name="subscription">The subscription. Deriving from <c>SubscriptionBase</c> is optional.</param>
    /// <param name="configure">
    ///     Its shard options — batch size, where to start from, event filtering. Only reached when the
    ///     subscription carries them, which a bare <c>ISubscription</c> does once wrapped.
    /// </param>
    /// <remarks>
    ///     A subscription is a daemon shard like a projection, so it needs the async daemon running to
    ///     do anything at all — <c>AddAsyncDaemon()</c>, or <c>BuildProjectionDaemonAsync</c> by hand.
    ///     There is no inline equivalent, because "inline" would just be code in the caller's own unit
    ///     of work.
    /// </remarks>
    public void Subscribe(Subscriptions.ISubscription subscription,
        Action<ISubscriptionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        var source = subscription as ISubscriptionSource<IDocumentSession, IQuerySession>
                     ?? new Subscriptions.SubscriptionWrapper(subscription);

        if (source is ISubscriptionOptions options)
        {
            configure?.Invoke(options);
        }

        registerSubscription(source);
    }

    /// <inheritdoc cref="Subscribe(Subscriptions.ISubscription, Action{ISubscriptionOptions})" />
    public void Subscribe<T>(Action<ISubscriptionOptions>? configure = null)
        where T : Subscriptions.ISubscription, new()
        => Subscribe(new T(), configure);

    /// <summary>
    ///     Every natural key declared by a registered aggregate projection (fisher#40).
    /// </summary>
    /// <remarks>
    ///     The definitions themselves are JasperFx's — <c>[NaturalKey]</c> on the aggregate and
    ///     <c>[NaturalKeySource]</c> or <c>NaturalKeyFor(...)</c> for the extractors, all discovered by
    ///     <c>JasperFxAggregationProjectionBase</c>. What a store supplies is the storage seam, the same
    ///     division as the async daemon. Fisher reaches them through the registered projections rather
    ///     than through a registry of its own, because that is where the discovery already put them.
    /// </remarks>
    internal IReadOnlyList<JasperFx.Events.NaturalKeyDefinition> NaturalKeys
        => All.OfType<IAggregateProjection>()
            .Select(x => x.NaturalKeyDefinition)
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();

    /// <inheritdoc cref="NaturalKeys" />
    internal JasperFx.Events.NaturalKeyDefinition? NaturalKeyFor(Type aggregateType)
        => NaturalKeys.FirstOrDefault(x => x.AggregateType == aggregateType);

    private IInlineProjection<IDocumentSession>[]? _inlineProjections;

    /// <summary>
    ///     The inline projections to apply on every commit, built once.
    /// </summary>
    internal IInlineProjection<IDocumentSession>[] BuildInlineProjections()
        => _inlineProjections ??= All
            .Where(x => x.Lifecycle == ProjectionLifecycle.Inline)
            .Select(x => x.BuildForInline())
            .ToArray();
}
