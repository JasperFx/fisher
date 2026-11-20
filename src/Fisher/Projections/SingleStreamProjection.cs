using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;

namespace Fisher.Projections;

/// <summary>
///     A projection that folds the events of one stream into a single aggregate document, using
///     JasperFx's conventional <c>Create</c> / <c>Apply</c> / <c>ShouldDelete</c> method discovery.
/// </summary>
/// <remarks>
///     <para>
///         Closing the shared JasperFx base over Fisher's session types is all this does — the
///         aggregation machinery itself lives in <c>JasperFx.Events</c>, exactly as it does for Marten
///         and Polecat.
///     </para>
///     <para>
///         Fisher does not yet persist snapshots, so today this type is reached only through live
///         aggregation: <c>EventGraph</c> closes it over an auto-discovered aggregate type for
///         <c>AggregateStreamAsync</c>. Registering a subclass as an Inline or Async projection needs
///         document storage and the projection graph, neither of which exists yet.
///     </para>
/// </remarks>
/// <typeparam name="TDoc">The aggregate document type.</typeparam>
/// <typeparam name="TId">The aggregate's identity type, which must match the stream identity.</typeparam>
public class SingleStreamProjection<TDoc, TId>
    : JasperFxSingleStreamProjectionBase<TDoc, TId, IDocumentSession, IQuerySession>, IFisherRegistrable,
        IFisherBackfillSource
    where TDoc : notnull
    where TId : notnull
{
    private readonly List<FisherBackfill<TDoc>> _backfills = new();

    /// <summary>
    ///     jasperfx#1222 / fisher#643: declare a backfill that fills a new member on the documents this projection
    ///     already wrote, from only the events that produce it. Start the projection's live version from the same
    ///     point with <c>Options.SubscribeFromPriorVersionMark(name)</c>.
    /// </summary>
    /// <remarks>
    ///     A backfill runs as a Wolverine job through <see cref="JasperFx.Events.IEventStore.RunBackfillAsync" />,
    ///     so the store refuses the declaration at startup unless <see cref="JasperFx.Events.EventRegistry.HasJobHost" />
    ///     is set, and refuses it on a projection that is not asynchronous.
    /// </remarks>
    public FisherBackfill<TDoc> Backfill(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_backfills.Any(x => x.Name == name))
        {
            throw new InvalidOperationException($"Projection {Name} already declares a backfill named '{name}'.");
        }

        var backfill = new FisherBackfill<TDoc>(name);
        _backfills.Add(backfill);
        return backfill;
    }

    IReadOnlyList<IFisherBackfill> IFisherBackfillSource.Backfills => _backfills;

    Type IFisherBackfillSource.DocumentType => typeof(TDoc);

    IFisherBackfillRunner IFisherBackfillSource.RunnerFor(IFisherBackfill backfill)
        => new FisherBackfillRunner<TDoc, TId>((FisherBackfill<TDoc>)backfill);

    /// <inheritdoc />
    public static void Register<TConcrete>(IServiceCollection services, ProjectionLifecycle lifecycle,
        ServiceLifetime lifetime, Action<ProjectionBase>? configure) where TConcrete : class
        => ContainerScopedRegistration.Register<TConcrete>(services, lifecycle, lifetime, configure,
            static (s, callback) => s.ConfigureFisher(callback),
            ContainerScopedRegistration.Aggregation(typeof(TConcrete), typeof(TDoc), typeof(TId)));

    /// <inheritdoc />
    public static void Register<TConcrete, TStore>(IServiceCollection services, ProjectionLifecycle lifecycle,
        ServiceLifetime lifetime, Action<ProjectionBase>? configure)
        where TStore : class, IDocumentStore where TConcrete : class
        => ContainerScopedRegistration.Register<TConcrete>(services, lifecycle, lifetime, configure,
            static (s, callback) => s.ConfigureFisher<TStore>(callback),
            ContainerScopedRegistration.Aggregation(typeof(TConcrete), typeof(TDoc), typeof(TId)));
}
