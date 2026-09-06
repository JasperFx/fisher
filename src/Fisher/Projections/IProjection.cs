using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;

namespace Fisher.Projections;

/// <summary>
///     Marker for a Fisher projection, closing JasperFx's <see cref="IJasperFxProjection{TOperations}" />
///     over Fisher's write session. Mirrors Marten's and Polecat's <c>IProjection</c>.
/// </summary>
public interface IProjection : IJasperFxProjection<IDocumentSession>, IFisherRegistrable
{
    /// <summary>
    ///     Register an implementation of this interface through the container (fisher#194).
    /// </summary>
    /// <remarks>
    ///     Explicit implementations, so an implementing class carries no visible static surface it did
    ///     not write — the registration is reached through the type parameter on
    ///     <c>AddProjectionWithServices&lt;T&gt;</c>, which is the only caller.
    /// </remarks>
    static void IFisherRegistrable.Register<TConcrete>(IServiceCollection services, ProjectionLifecycle lifecycle,
        ServiceLifetime lifetime, Action<ProjectionBase>? configure)
        => ContainerScopedRegistration.Register<TConcrete>(services, lifecycle, lifetime, configure,
            static (s, callback) => s.ConfigureFisher(callback),
            ContainerScopedRegistration.Plain(typeof(TConcrete)));

    /// <inheritdoc cref="Register{TConcrete}" />
    static void IFisherRegistrable.Register<TConcrete, TStore>(IServiceCollection services,
        ProjectionLifecycle lifecycle, ServiceLifetime lifetime, Action<ProjectionBase>? configure)
        => ContainerScopedRegistration.Register<TConcrete>(services, lifecycle, lifetime, configure,
            static (s, callback) => s.ConfigureFisher<TStore>(callback),
            ContainerScopedRegistration.Plain(typeof(TConcrete)));
}
