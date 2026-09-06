using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;

namespace Fisher.Projections;

/// <summary>
///     A projection type that knows how to register <em>itself</em> into an IoC container, so it can be
///     resolved from the application's services rather than constructed by Fisher (fisher#194).
/// </summary>
/// <remarks>
///     <para>
///         The same static-abstract shape as Marten's <c>IMartenRegistrable</c>, and for the same
///         reason: which container-scoped wrapper a projection needs depends on what <em>kind</em> of
///         projection it is — an aggregation wrapper knows the document and identity types, a plain
///         projection wrapper does not — and the projection base class is the only place that knows
///         which it is. A static abstract member lets <c>AddProjectionWithServices&lt;T&gt;</c> dispatch
///         to the right one from a bare type parameter, with no reflection over base types at the call
///         site.
///     </para>
///     <para>
///         <b>Fisher implements this interface and supplies nothing else.</b> Every wrapper the
///         implementations reach for lives in <c>JasperFx.Events.Projections.ContainerScoped</c> and is
///         already generic over the store's operations and query session types, so closing them over
///         <see cref="IDocumentSession" /> and <see cref="IQuerySession" /> is the whole of Fisher's
///         container-scoped projection support.
///     </para>
/// </remarks>
public interface IFisherRegistrable
{
    /// <summary>
    ///     Register <typeparamref name="TConcrete" /> against the primary store.
    /// </summary>
    static abstract void Register<TConcrete>(IServiceCollection services, ProjectionLifecycle lifecycle,
        ServiceLifetime lifetime, Action<ProjectionBase>? configure) where TConcrete : class;

    /// <summary>
    ///     Register <typeparamref name="TConcrete" /> against the secondary store
    ///     <typeparamref name="TStore" />.
    /// </summary>
    static abstract void Register<TConcrete, TStore>(IServiceCollection services, ProjectionLifecycle lifecycle,
        ServiceLifetime lifetime, Action<ProjectionBase>? configure)
        where TStore : class, IDocumentStore where TConcrete : class;
}
