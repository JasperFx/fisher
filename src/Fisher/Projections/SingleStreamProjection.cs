using JasperFx.Events.Aggregation;

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
    : JasperFxSingleStreamProjectionBase<TDoc, TId, IDocumentSession, IQuerySession>
    where TDoc : notnull
    where TId : notnull
{
}
