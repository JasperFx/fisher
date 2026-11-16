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
///         whether it did, as the date-part and <c>Nullable.Value</c> rules were when fisher#483 taught it,
///         and as most of JasperFx's <c>SqlNullSemantics()</c> was when fisher#608 did (fisher#612).
///     </para>
/// </remarks>
internal static class CriteriaShapeRules
{
    public static DynamicQueryPolicy Policy { get; } = DynamicQueryPolicy.Default.WithRules(
        new NegatedStringMethodOnNullableMember());
}

/// <summary>
///     <c>not</c> over a string method (<c>Contains</c>, <c>StartsWith</c>, <c>EndsWith</c>, <c>Equals</c>, …)
///     called on a member that can be null — unless the text says what a null should do.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why only this shape is left</b> (fisher#612). JasperFx's <c>SqlNullSemantics()</c> refuses every
///         <c>&lt;&gt;</c> and every <c>not</c> over a nullable member, because SQL's three-valued logic drops
///         the null rows. Since fisher#608 Fisher's provider answers those as C# does — <c>!=</c> is
///         <c>is not</c>, and a negated comparison is two-valued — so the rule refused queries the store gets
///         right. With it removed, the matrix found exactly one shape still silently wrong:
///         <c>not Note.Contains("frag")</c>. A string method on a null member deliberately keeps SQL's answer
///         in the provider, because C# would throw rather than answer, so its negation still drops the rows
///         where the member is null while the text, read with null propagation, keeps them.
///     </para>
///     <para>
///         <b>The guard logic is JasperFx's, reused rather than restated.</b> This decorates the shared rule:
///         it is shown every node, so it records each <c>not</c> and each <c>X = null</c> / <c>X != null</c>
///         guard exactly as before, and its refusal is passed on only when the node it refuses is a string
///         method call. Every comparison it would have refused is now let through. So
///         <c>not Note.Contains("x") or Note = null</c> and <c>Note != null and not Note.Contains("x")</c> still
///         run, and a member the document declares non-nullable is still never refused.
///     </para>
/// </remarks>
internal sealed class NegatedStringMethodOnNullableMember : IDynamicQueryShapeRule
{
    // One instance per policy, which is static; the shared rule keys its state by the parsed nodes, which
    // are fresh per parse, so concurrent parses never share an entry.
    private readonly IDynamicQueryShapeRule _sqlNullSemantics = DynamicQueryShapeRules.SqlNullSemantics();

    public string? Refuse(Expression node)
    {
        // Always shown to the shared rule, whatever the node: its guards and negations are recorded on the
        // way down, and a node it is not shown is a guard it never sees.
        var refusal = _sqlNullSemantics.Refuse(node);

        if (refusal is null || node is not MethodCallExpression { Object: { Type: var type } target } call ||
            type != typeof(string))
        {
            return null;
        }

        var path = PathOf(target);

        return $"negates {call.Method.Name}(...) on '{path}', and '{path}' can be null: a string method on a null "
               + "member is unknown to the database, so the negation leaves rows where it is null OUT, where the "
               + $"text read with null propagation would keep them. Say what null should do — '... or {path} = null' "
               + $"keeps those rows, '{path} != null and ...' drops them.";
    }

    private static string PathOf(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            expression = convert.Operand;
        }

        var names = new List<string>();
        while (expression is MemberExpression { Expression: { } owner } member)
        {
            names.Add(member.Member.Name);
            expression = owner;
        }

        names.Reverse();
        return string.Join('.', names);
    }
}
