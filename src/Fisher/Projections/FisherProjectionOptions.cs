using Fisher.Events;
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
}
