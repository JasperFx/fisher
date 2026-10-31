using System.Linq.Expressions;
using JasperFx.Linq;

namespace Fisher.Linq;

/// <summary>
///     The Dynamic LINQ shapes Fisher's LINQ provider translates SILENTLY WRONG — it runs them and returns
///     the wrong rows — refused before they reach it (jasperfx#869). Every rule here was measured against a
///     LINQ-to-objects oracle over the same documents (<c>document_diagnostics_criteria</c>) and each is
///     pinned by a test of its own; a shape the provider refuses honestly needs no rule, because its
///     refusal already reaches the console as <c>DocumentCriteriaNotSupportedException</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>These are provider gaps, not Dynamic LINQ ones.</b> The typed control fails the same way. The
///         refusals protect a console user who cannot see the SQL; an application's own typed query is
///         untouched by them. Remove a rule when the provider learns the shape, and the matrix test will say
///         whether it did, as the date-part and <c>Nullable.Value</c> rules were when fisher#483 taught it.
///     </para>
/// </remarks>
internal static class CriteriaShapeRules
{
    public static DynamicQueryPolicy Policy { get; } = DynamicQueryPolicy.Default.WithRules(
        // jasperfx#869: <> / not on a member that can be null — three-valued logic drops the null rows. Found
        // here, measured on Marten and Polecat too, and lifted into JasperFx as the shared rule every
        // SQL-backed store adds.
        DynamicQueryShapeRules.SqlNullSemantics());
}
