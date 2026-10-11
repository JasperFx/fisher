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
///         <b>These are provider gaps, not Dynamic LINQ ones.</b> The typed control fails the same way:
///         <c>Query&lt;T&gt;().Where(x =&gt; x.PlacedAt.Year == 2026)</c> renders
///         <c>json_extract(data, '$.placedAt.year')</c> and matches nothing. The refusals protect a console
///         user who cannot see the SQL; an application's own typed query is untouched by them. Remove a rule
///         when the provider learns the shape, and the matrix test will say whether it did.
///     </para>
/// </remarks>
internal static class CriteriaShapeRules
{
    private const string DatePartReason =
        "reads a part of a date or time (Year, Month, Day, Date, Hour, TotalHours, …), which Fisher renders as a "
        + "path INTO the stored value and so matches nothing. Compare the whole value against a range instead: "
        + "PlacedAt >= @0 and PlacedAt < @1.";

    public static DynamicQueryPolicy Policy { get; } = DynamicQueryPolicy.Default.WithRules(
        DynamicQueryShapeRules.NoMemberAccessOn<DateTime>(DatePartReason),
        DynamicQueryShapeRules.NoMemberAccessOn<DateTimeOffset>(DatePartReason),
        DynamicQueryShapeRules.NoMemberAccessOn<DateOnly>(DatePartReason),
        DynamicQueryShapeRules.NoMemberAccessOn<TimeOnly>(DatePartReason),
        DynamicQueryShapeRules.NoMemberAccessOn<TimeSpan>(DatePartReason),
        DynamicQueryShapeRules.For(node =>
            node is MemberExpression { Member.Name: nameof(Nullable<int>.Value), Expression: { } owner }
            && Nullable.GetUnderlyingType(owner.Type) is not null
                ? "reads .Value off a nullable member, which Fisher renders as a path into the stored value and so "
                  + "matches nothing. Compare the member itself — Discount > 5 — which leaves out rows where it is null."
                : null),
        // jasperfx#869: <> / not on a member that can be null — three-valued logic drops the null rows. Found
        // here, measured on Marten and Polecat too, and lifted into JasperFx as the shared rule every
        // SQL-backed store adds.
        DynamicQueryShapeRules.SqlNullSemantics());
}
