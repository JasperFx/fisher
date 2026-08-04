using JasperFx.Events.Projections;

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
///         <b>Storing an entity throws today.</b> The one member Fisher has to supply is
///         <see cref="storeEntity{T}" />, which is document storage, and Fisher has none. The type
///         exists ahead of that because it is what the <c>ComplianceEventProjection</c> global alias
///         binds to, and the compliance suites declare their projection types at file scope where
///         they cannot reach their suite's generic parameters. Registering one of these as an Inline
///         or Async projection needs the projection graph as well.
///     </para>
/// </remarks>
public abstract class EventProjection : JasperFxEventProjectionBase<IDocumentSession, IQuerySession>
{
    protected sealed override void storeEntity<T>(IDocumentSession ops, T entity)
        => throw new NotImplementedException(
            "Fisher cannot store a projected document yet — there is no document storage for an " +
            "EventProjection to write to.");
}
