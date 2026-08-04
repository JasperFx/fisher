using JasperFx.Events.Projections;

namespace Fisher.Projections;

/// <summary>
///     Marker for a Fisher projection, closing JasperFx's <see cref="IJasperFxProjection{TOperations}" />
///     over Fisher's write session. Mirrors Marten's and Polecat's <c>IProjection</c>.
/// </summary>
public interface IProjection : IJasperFxProjection<IDocumentSession>
{
}
