using System.Diagnostics.CodeAnalysis;
using Fisher.Events;
using JasperFx.Core.Reflection;
using JasperFx.Events.Projections;

namespace Fisher.Projections;

/// <summary>
///     Projection registration and configuration for Fisher — <c>StoreOptions.Projections</c>.
/// </summary>
/// <remarks>
///     <para>
///         Deliberately thin. Fisher cannot persist a projection yet, so there is no <c>Snapshot&lt;T&gt;</c>
///         and no inline application during <c>SaveChangesAsync</c>; what the graph is carrying its
///         weight for today is the two things that need no storage at all — the live aggregator cache
///         behind <see cref="ProjectionGraph{TProjection,TOperations,TQuerySession}.AggregatorFor{T}" />,
///         and <c>DiscoverGeneratedEvolvers</c> / <c>AllAggregateTypes</c>, which report the
///         self-aggregating types whose evolvers the source generator emitted.
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
    ///     Only <see cref="SnapshotLifecycle.Inline" /> works today: an inline snapshot is written in
    ///     the same transaction as the events that produced it, whereas an async one needs the daemon
    ///     Fisher does not have. Registering the document mapping here is what gets the snapshot's
    ///     table created with the rest of the schema.
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

        if (lifecycle == SnapshotLifecycle.Async)
        {
            throw new NotSupportedException(
                "Fisher has no async projection daemon, so a snapshot can only be maintained inline.");
        }

        // Closed over the aggregate's own identity type, not the stream identity primitive — the same
        // rule live aggregation follows, and for the same source-generator reason. See CLAUDE.md.
        var idType = Storage.AggregateIdentity.ResolveIdType(typeof(T), _events.StreamIdentity);
        var source = typeof(SingleStreamProjection<,>)
            .CloseAndBuildAs<ProjectionBase>(typeof(T), idType);

        source.Lifecycle = ProjectionLifecycle.Inline;
        source.AssembleAndAssertValidity();

        // The snapshot needs somewhere to live; registering the mapping puts its table in the schema.
        _events.Options.Schema.MappingFor(typeof(T));

        Add((IProjectionSource<IDocumentSession, IQuerySession>)source, ProjectionLifecycle.Inline);
    }

    /// <summary>
    ///     Register an already-built projection.
    /// </summary>
    public void Add(ProjectionBase projection, ProjectionLifecycle lifecycle)
    {
        if (lifecycle == ProjectionLifecycle.Async)
        {
            throw new NotSupportedException(
                "Fisher has no async projection daemon; register the projection as Inline or Live.");
        }

        projection.Lifecycle = lifecycle;
        projection.AssembleAndAssertValidity();

        foreach (var published in projection.As<IProjectionSource<IDocumentSession, IQuerySession>>()
                     .PublishedTypes())
        {
            _events.Options.Schema.MappingFor(published);
        }

        Add((IProjectionSource<IDocumentSession, IQuerySession>)projection, lifecycle);
    }

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
