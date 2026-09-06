using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Reflection;
using JasperFx.Events.Projections;
using JasperFx.Events.Projections.ContainerScoped;
using Microsoft.Extensions.DependencyInjection;

namespace Fisher.Projections;

/// <summary>
///     The one implementation of "register this projection through the container" that every
///     <see cref="IFisherRegistrable" /> implementation routes through (fisher#194).
/// </summary>
/// <remarks>
///     <para>
///         <b>Scope lifetime is the whole design, and it is settled by the shared wrappers rather than
///         by Fisher.</b> A projection lives as long as the store; an async projection outlives every
///         request scope in the process by construction, since the daemon is a hosted service with no
///         request at all. So a container-scoped projection cannot be resolved once and held — the
///         service graph behind it would be resolved from a scope that is disposed long before the
///         daemon's next batch, and the first scoped dependency touched would throw
///         <c>ObjectDisposedException</c>. Nor can the wrapper open a scope and keep it: that leaks one
///         scope, and everything in it, per registration for the life of the process.
///     </para>
///     <para>
///         The shared <c>ContainerScoped</c> wrappers answer this by opening and disposing an
///         <see cref="IServiceScope" /> <em>per unit of work</em> — one per inline
///         <c>ApplyAsync</c> (so per <c>SaveChangesAsync</c>), one per daemon page, and one per slicing
///         pass — resolving the projection inside it and letting go of it before the scope closes.
///         Nothing is captured across a batch boundary, so nothing can be resolved from a disposed
///         provider and nothing accumulates. That is also why <see cref="ServiceLifetime.Transient" />
///         is treated as <see cref="ServiceLifetime.Scoped" /> below rather than refused: the wrapper
///         resolves afresh per batch either way, so the two lifetimes describe the same behaviour and
///         a transient registration would only differ in disposing later.
///     </para>
///     <para>
///         <b>The provider the scopes come from is the root provider</b>, which is what
///         <c>IConfigureFisher</c> hands the callback and what the store is built from. A scope created
///         from a scoped provider would be a child of something that gets disposed; a scope created
///         from the root is not.
///     </para>
///     <para>
///         <b>Both paths land on Fisher's own <c>Add(ProjectionBase, lifecycle)</c></b> rather than the
///         base graph's <c>Add(IProjectionSource, ...)</c> that Marten's equivalent calls. That
///         overload is what sweeps <c>PublishedTypes()</c> into the schema, so a container-scoped
///         projection's document table is created with the rest of the schema exactly as a directly
///         registered one's is. Going through the narrower overload would leave the projection working
///         against a table that was never migrated — the silent half of fisher#111.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2026",
    Justification = "Class-level: consumes CloseAndBuildAs, which reflects over a constructed generic "
                    + "type. The projection type flows in from the caller's own "
                    + "AddProjectionWithServices<T> registration and is preserved by the trimmer at that "
                    + "boundary — the same reasoning JasperFx's own container-scoped wrappers carry.")]
[UnconditionalSuppressMessage("Trimming", "IL2091",
    Justification = "Class-level: TConcrete is the caller's own projection type, registered into the "
                    + "container by the caller, so its constructors are preserved at that boundary.")]
internal static class ContainerScopedRegistration
{
    /// <summary>
    ///     Register a projection resolved from the container, against whichever store
    ///     <paramref name="configureStore" /> targets.
    /// </summary>
    /// <param name="services">The application's services.</param>
    /// <param name="lifecycle">Fisher's projection lifecycle — Inline, Async or Live.</param>
    /// <param name="lifetime">The IoC lifetime. Transient is treated as Scoped; see the remarks.</param>
    /// <param name="configure">Optional configuration of the projection's name, version and filtering.</param>
    /// <param name="configureStore">
    ///     How to contribute to the target store's options — <c>ConfigureFisher</c> for the primary
    ///     store, <c>ConfigureFisher&lt;TStore&gt;</c> for a secondary one. Passed as a delegate so the
    ///     two differ in one line rather than in a duplicated body.
    /// </param>
    /// <param name="buildScopedWrapper">
    ///     Builds the container-scoped wrapper for this <em>kind</em> of projection, which only the
    ///     projection's own base class knows — an aggregation wrapper needs the document and identity
    ///     types, a plain projection wrapper does not.
    /// </param>
    internal static void Register<TConcrete>(
        IServiceCollection services,
        ProjectionLifecycle lifecycle,
        ServiceLifetime lifetime,
        Action<ProjectionBase>? configure,
        Action<IServiceCollection, Action<IServiceProvider, StoreOptions>> configureStore,
        Func<IServiceProvider, ProjectionBase> buildScopedWrapper)
        where TConcrete : class
    {
        ArgumentNullException.ThrowIfNull(services);

        switch (lifetime)
        {
            case ServiceLifetime.Singleton:
                // A singleton projection has no scope problem to solve, so it is registered as itself
                // and no wrapper is involved at all. The container still builds it, which is the point
                // — its constructor dependencies are resolved rather than absent.
                services.AddSingleton<TConcrete>();
                configureStore(services, (s, options) =>
                {
                    var projection = s.GetRequiredService<TConcrete>();

                    // A bare IProjection is not a ProjectionBase and carries none of the name, version
                    // or filtering surface `configure` writes to, so it is wrapped here rather than at
                    // the graph — which is where the non-DI Add(ProjectionBase, ...) would otherwise do
                    // it, too late for the lambda to reach.
                    var projectionBase = projection as ProjectionBase
                                         ?? (projection is IProjection bare
                                             ? new ProjectionWrapper<IDocumentSession, IQuerySession>(bare, lifecycle)
                                             : throw new InvalidOperationException(
                                                 $"'{typeof(TConcrete).FullNameInCode()}' is registered with "
                                                 + "AddProjectionWithServices but is neither a projection base "
                                                 + "class nor a Fisher.Projections.IProjection, so there is "
                                                 + "nothing for the store to run."));

                    configure?.Invoke(projectionBase);
                    options.Projections.Add(projectionBase, lifecycle);
                });
                break;

            case ServiceLifetime.Scoped:
            case ServiceLifetime.Transient:
                services.AddScoped<TConcrete>();
                configureStore(services, (s, options) =>
                {
                    var wrapper = buildScopedWrapper(s);

                    // Before configure, so a caller's lambda can override what the wrapper copied off
                    // the projection it wraps rather than being overwritten by it.
                    wrapper.Lifecycle = lifecycle;
                    configure?.Invoke(wrapper);

                    options.Projections.Add(wrapper, lifecycle);
                });
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime,
                    "Only Singleton, Scoped and Transient are meaningful for a projection.");
        }
    }

    /// <summary>
    ///     The scoped wrapper for an aggregation projection — single stream or multi stream.
    /// </summary>
    /// <remarks>
    ///     Through the shared non-generic factory rather than by closing a wrapper type directly, so a
    ///     single stream projection registered Scoped keeps its
    ///     <c>IAggregatorSource</c> and stays usable from live aggregation and single stream rebuilds.
    ///     See marten#5095, which is where that distinction was found.
    /// </remarks>
    internal static Func<IServiceProvider, ProjectionBase> Aggregation(Type concreteType, Type documentType,
        Type identityType)
        => s => ScopedAggregationWrapper.Build(s, concreteType, documentType, identityType,
            typeof(IDocumentSession), typeof(IQuerySession));

    /// <summary>
    ///     The scoped wrapper for a projection that is not an aggregation — an
    ///     <see cref="EventProjection" /> or a bare <see cref="IProjection" />.
    /// </summary>
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Closes ScopedProjectionWrapper<,,> over the projection type and Fisher's session "
                        + "pair through MakeGenericType. The projection type flows in from the caller's own "
                        + "AddProjectionWithServices<T> registration and is preserved at that boundary — the "
                        + "same reasoning JasperFx's own ScopedAggregationWrapper.Build carries.")]
    internal static Func<IServiceProvider, ProjectionBase> Plain(Type concreteType)
        => s => typeof(ScopedProjectionWrapper<,,>).CloseAndBuildAs<ProjectionBase>(s, concreteType,
            typeof(IDocumentSession), typeof(IQuerySession));
}
