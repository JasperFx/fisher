using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Reflection;
using JasperFx.Events.Projections;
using JasperFx.Events.Projections.Composite;

namespace Fisher.Projections;

/// <summary>
///     Several projections composed into ordered stages, rebuilt together in one pass (fisher#19).
/// </summary>
/// <remarks>
///     <para>
///         Stages run in order; the projections inside a stage run together. The point is a projection
///         that reads what an earlier one wrote — a rebuild that ran them independently would have to
///         replay the events twice and could still interleave them wrongly.
///     </para>
///     <para>
///         <b>Composites are always asynchronous.</b> A stage boundary is a point where earlier stages'
///         writes must be visible to later ones, which is a property of a daemon batch rather than of a
///         caller's unit of work — so an inline composite would be a stage boundary with nothing on
///         either side of it.
///     </para>
///     <para>
///         Everything here is JasperFx's <c>CompositeProjection&lt;TOperations, TQuerySession&gt;</c>;
///         Fisher supplies the closed session pair and the registration entry point, the same way it
///         does for the daemon and the projection scenario.
///     </para>
/// </remarks>
public class FisherCompositeProjection : CompositeProjection<IDocumentSession, IQuerySession>
{
    private readonly StoreOptions _options;

    internal FisherCompositeProjection(string name, StoreOptions options) : base(name)
    {
        _options = options;
        Lifecycle = ProjectionLifecycle.Async;
    }

    /// <summary>
    ///     Add an already-built projection to a stage.
    /// </summary>
    public void Add(IProjectionSource<IDocumentSession, IQuerySession> projection, int stageNumber = 1)
    {
        ArgumentNullException.ThrowIfNull(projection);

        if (projection is ProjectionBase projectionBase)
        {
            projectionBase.Lifecycle = ProjectionLifecycle.Async;
            projectionBase.AssembleAndAssertValidity();
        }

        StageFor(stageNumber).Add(projection);
    }

    /// <summary>
    ///     Add a bare <see cref="IProjection" /> to a stage.
    /// </summary>
    /// <remarks>
    ///     Wrapped, because a composite stage holds an <c>IProjectionSource</c> — which knows its
    ///     shards and version — and an <see cref="IProjection" /> only applies events. See
    ///     <c>CompositeIProjectionSource</c>.
    /// </remarks>
    public void Add(IProjection projection, int stageNumber = 1)
    {
        ArgumentNullException.ThrowIfNull(projection);

        StageFor(stageNumber).Add(new CompositeIProjectionSource(projection));
    }

    /// <inheritdoc cref="Add(IProjectionSource{IDocumentSession,IQuerySession},int)" />
    public void Add<T>(int stageNumber = 1)
        where T : IProjectionSource<IDocumentSession, IQuerySession>, new()
        => Add(new T(), stageNumber);

    /// <summary>
    ///     Add a self-aggregating snapshot to a stage.
    /// </summary>
    /// <remarks>
    ///     Closed over the aggregate's own identity type rather than the stream identity primitive —
    ///     the same rule <c>Projections.Snapshot&lt;T&gt;</c> and live aggregation follow, for the same
    ///     source-generator reason. Registering the mapping is what puts the snapshot's table in the
    ///     schema.
    /// </remarks>
    [RequiresDynamicCode("Closes SingleStreamProjection<,> over (T, T's id type) via Type.MakeGenericType.")]
    [RequiresUnreferencedCode("Resolves T's identity member reflectively through AggregateIdentity.")]
    public void Snapshot<T>(int stageNumber = 1) where T : notnull
    {
        if (typeof(T).CanBeCastTo<ProjectionBase>())
        {
            throw new InvalidOperationException(
                "Snapshot<T> is for self-aggregating document types. Use Add() for a projection class "
                + $"such as {typeof(T).FullNameInCode()}.");
        }

        var idType = Storage.AggregateIdentity.ResolveIdType(typeof(T), _options.EventGraph.StreamIdentity);
        var source = typeof(SingleStreamProjection<,>).CloseAndBuildAs<ProjectionBase>(typeof(T), idType);

        source.Lifecycle = ProjectionLifecycle.Async;
        source.AssembleAndAssertValidity();

        _options.Schema.MappingFor(typeof(T));

        StageFor(stageNumber).Add((IProjectionSource<IDocumentSession, IQuerySession>)source);
    }
}
