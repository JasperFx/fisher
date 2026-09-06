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

        // The schema is asked lazily rather than captured, because it is created after this is — and a
        // registration's ordering check is the whole reason it needs to ask at all.
        StorageProviders = new ProjectionStorageRegistry(type => _events.Options.Schema.HasMappingFor(type));
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
        // Unless it already has somewhere else to live, in which case mapping it would put a second,
        // empty table in the migration and leave the reader wondering which one the projection uses.
        if (!StorageProviders.HasProviderFor(typeof(T)))
        {
            _events.Options.Schema.MappingFor(typeof(T));
        }

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
    /// <remarks>
    ///     <para>
    ///         Most projection base classes are their own <see cref="IProjectionSource{T,T}" /> — an
    ///         aggregation projection knows what it publishes and how to slice for it. A bare
    ///         <see cref="IProjection" /> is not: it is a lump of code that applies events and nothing
    ///         more, so it is wrapped in JasperFx's <c>ProjectionWrapper</c> to give it the shard and
    ///         lifecycle plumbing. Polecat wraps in the same place and for the same reason.
    ///     </para>
    ///     <para>
    ///         A wrapped projection publishes no types, so nothing is mapped for it — which is correct
    ///         rather than a shortcoming: what such a projection writes, and where, is its own business,
    ///         and Fisher only creates tables for types it was told about.
    ///     </para>
    ///     <para>
    ///         <b>A published type with a registered storage provider is skipped</b>, the same check
    ///         <see cref="Snapshot{T}" /> makes and for the same reason: its rows are not in a Fisher
    ///         document table, so mapping it would put a second, empty table in the migration and leave
    ///         a reader wondering which one the projection uses. <b>This was fisher#111</b>, and only
    ///         <c>Snapshot&lt;T&gt;</c> had the guard — so an EF Core-backed
    ///         <c>SingleStreamProjection</c> registered here, and <em>every</em> EF-backed
    ///         <c>MultiStreamProjection</c>, since this overload is the only door for one. Silent in
    ///         both directions: the projection works, because storage resolution checks the registry
    ///         first, and the stray table sits in the schema forever.
    ///     </para>
    /// </remarks>
    public void Add(ProjectionBase projection, ProjectionLifecycle lifecycle)
    {
        projection.Lifecycle = lifecycle;
        projection.AssembleAndAssertValidity();

        if (projection is not IProjectionSource<IDocumentSession, IQuerySession> source)
        {
            if (projection is not IProjection bare)
            {
                throw new ArgumentOutOfRangeException(nameof(projection),
                    $"'{projection.GetType().Name}' is neither an IProjectionSource nor a Fisher "
                    + "IProjection, so there is nothing for the daemon to run. Derive from one of the "
                    + "projection base classes, or implement Fisher.Projections.IProjection.");
            }

            Add(new ProjectionWrapper<IDocumentSession, IQuerySession>(bare, lifecycle), lifecycle);

            return;
        }

        foreach (var published in source.PublishedTypes())
        {
            // Unless it already has somewhere else to live, exactly as Snapshot<T> checks — see the
            // remarks. Asked per published type rather than per projection, because a projection may
            // publish several and only some of them be registered.
            if (StorageProviders.HasProviderFor(published))
            {
                continue;
            }

            _events.Options.Schema.MappingFor(published);
        }

        Add(source, lifecycle);
    }

    /// <summary>
    ///     Register a projection by type, constructing it — the spelling both siblings use.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>Projections.Add&lt;OrderProjection&gt;(ProjectionLifecycle.Inline)</c> is what
    ///         registration code in the wild is written against, and it is the one line that stopped an
    ///         otherwise identical block from being shared across the three stores (fisher#76). It shows
    ///         up once per projection, and <see cref="Snapshot{T}" /> already takes its type argument, so
    ///         the two halves of one registration block were inconsistent with each other as well.
    ///     </para>
    ///     <para>
    ///         <b>This hides an inherited overload rather than adding a missing one, and that is the
    ///         point.</b> <c>ProjectionGraph.Add&lt;TProjectionType&gt;</c> compiles today and goes
    ///         straight to <c>All.Add</c> — bypassing <c>Register</c>, so it invokes neither
    ///         <see cref="onAddProjection" /> (which registers the projection's event types, without
    ///         which a process that only reads cannot resolve them by name) nor the
    ///         <c>PublishedTypes()</c> sweep above (without which the projection's document type is
    ///         never mapped and its <b>table is never created</b>). Both failures are silent at
    ///         registration. Routing through the instance overload is what makes the generic form mean
    ///         the same thing as the form beside it.
    ///     </para>
    ///     <para>
    ///         The constraint is deliberately weaker than the base's, which demands
    ///         <c>IProjectionSource</c>: the instance overload accepts a bare
    ///         <see cref="IProjection" /> and wraps it, so requiring more here would refuse a projection
    ///         the store can perfectly well run. Every call that satisfied the base still compiles.
    ///     </para>
    /// </remarks>
    /// <typeparam name="TProjection">
    ///     The projection class, which must have a parameterless constructor. Use the instance overload
    ///     for a projection that needs constructor arguments.
    /// </typeparam>
    public new void Add<TProjection>(ProjectionLifecycle lifecycle,
        Action<AsyncOptions>? asyncConfiguration = null)
        where TProjection : ProjectionBase, new()
    {
        var projection = new TProjection();

        // Before Add, matching the base's ordering: the options a rebuild reads have to be in place
        // before AssembleAndAssertValidity runs over them.
        asyncConfiguration?.Invoke(projection.Options);

        Add(projection, lifecycle);
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
    ///     Projection document types whose storage is something other than a Fisher document table
    ///     (fisher#50).
    /// </summary>
    /// <remarks>
    ///     Empty for every store that has not asked for one, and consulted before Fisher's own document
    ///     storage is built — see <see cref="ProjectionStorageRegistry" />.
    /// </remarks>
    public ProjectionStorageRegistry StorageProviders { get; }

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

    private Fisher.Events.Storage.NaturalKeyProjection? _naturalKeyProjection;

    /// <summary>
    ///     The projection that maintains the natural key lookup, on the append path and on the daemon's
    ///     replay alike (fisher#206).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Built lazily and cached, because <see cref="NaturalKeys" /> walks every registered
    ///         projection on every read and both of its callers are on a hot path — the session's
    ///         commit and the daemon's batch.
    ///     </para>
    ///     <para>
    ///         <b>Not a member of <see cref="ProjectionGraph{TProjection,TOperations,TQuerySession}.All" />.</b>
    ///         It is an index over streams rather than a projection anybody registered, so giving it a
    ///         shard would put a rebuildable projection in front of an operator that indexes streams
    ///         they did not ask about. Marten's equivalent is likewise an inline projection plus a
    ///         daemon hook rather than an <c>IProjectionSource</c>.
    ///     </para>
    /// </remarks>
    internal Fisher.Events.Storage.NaturalKeyProjection NaturalKeyProjection
        => _naturalKeyProjection ??= new Fisher.Events.Storage.NaturalKeyProjection(_events, NaturalKeys);

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
