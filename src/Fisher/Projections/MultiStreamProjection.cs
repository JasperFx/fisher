using JasperFx.Events.Aggregation;

namespace Fisher.Projections;

/// <summary>
///     A projection that groups events from <em>many</em> streams onto one aggregate document, using
///     <c>Identity</c> / <c>Identities</c> / <c>FanOut</c> to decide which aggregate each event
///     belongs to rather than inheriting that from the stream.
/// </summary>
/// <remarks>
///     <para>
///         Closing the shared JasperFx base over Fisher's session types is all this does — every
///         grouping construct, the slicer, and the aggregation itself live in <c>JasperFx.Events</c>,
///         exactly as they do for Marten and Polecat.
///     </para>
///     <para>
///         Unlike <see cref="SingleStreamProjection{TDoc,TId}" />, <typeparamref name="TId" /> is
///         genuinely free of the stream identity: a multi-stream projection keyed by a string
///         department name is perfectly ordinary over Guid-identified streams. Fisher's document
///         storage has to support that identity type, which is why the four identity flavors landing
///         before this type did was load-bearing rather than incidental.
///     </para>
/// </remarks>
/// <typeparam name="TDoc">The aggregate document type.</typeparam>
/// <typeparam name="TId">The identity events are grouped by, which need not be the stream identity.</typeparam>
public abstract class MultiStreamProjection<TDoc, TId>
    : JasperFxMultiStreamProjectionBase<TDoc, TId, IDocumentSession, IQuerySession>
    where TDoc : notnull
    where TId : notnull
{
}
