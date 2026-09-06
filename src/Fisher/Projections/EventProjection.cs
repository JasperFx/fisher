using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;

namespace Fisher.Projections;

/// <summary>
///     Base class for projections that apply arbitrary per-event logic, through conventional
///     <c>Project</c> / <c>Create</c> methods rather than by folding a stream into one aggregate.
/// </summary>
/// <remarks>
///     <para>
///         Like <see cref="SingleStreamProjection{TDoc,TId}" />, this only closes the shared JasperFx
///         base over Fisher's session pair — the projection machinery itself lives in
///         <c>JasperFx.Events</c>.
///     </para>
///     <para>
///         The one member Fisher has to supply is <see cref="storeEntity{T}" />, which is an ordinary
///         document upsert onto the same unit of work the events are committing in — so an entity a
///         <c>Project</c> method stores lands in the same transaction as the event that produced it,
///         exactly as an inline snapshot does.
///     </para>
///     <para>
///         The type is what the <c>ComplianceEventProjection</c> global alias binds to, because the
///         compliance suites declare their projection types at file scope where they cannot reach
///         their suite's generic parameters.
///     </para>
/// </remarks>
public abstract class EventProjection : JasperFxEventProjectionBase<IDocumentSession, IQuerySession>, IFisherRegistrable
{
    protected sealed override void storeEntity<T>(IDocumentSession ops, T entity) => ops.Store(entity);

    /// <inheritdoc />
    public static void Register<TConcrete>(IServiceCollection services, ProjectionLifecycle lifecycle,
        ServiceLifetime lifetime, Action<ProjectionBase>? configure) where TConcrete : class
        => ContainerScopedRegistration.Register<TConcrete>(services, lifecycle, lifetime, configure,
            static (s, callback) => s.ConfigureFisher(callback),
            ContainerScopedRegistration.Plain(typeof(TConcrete)));

    /// <inheritdoc />
    public static void Register<TConcrete, TStore>(IServiceCollection services, ProjectionLifecycle lifecycle,
        ServiceLifetime lifetime, Action<ProjectionBase>? configure)
        where TStore : class, IDocumentStore where TConcrete : class
        => ContainerScopedRegistration.Register<TConcrete>(services, lifecycle, lifetime, configure,
            static (s, callback) => s.ConfigureFisher<TStore>(callback),
            ContainerScopedRegistration.Plain(typeof(TConcrete)));
}
