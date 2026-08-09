using System.Linq.Expressions;

namespace Fisher.Linq.Joins;

/// <summary>
///     What a <c>GroupJoin(...).SelectMany(...)</c> chain said, before any of it is turned into SQL.
/// </summary>
/// <remarks>
///     <para>
///         The two calls are one feature: <c>GroupJoin</c> alone yields a grouping per outer row, which
///         is a shape Fisher would have to materialize by reading every inner row of every group; the
///         <c>SelectMany</c> that follows flattens it back into the one-row-per-match shape a SQL join
///         already produces. So the parser captures both and the provider refuses the first without the
///         second, naming the required form — as Polecat does.
///     </para>
///     <para>
///         Everything here is still expressions. Resolving them needs both sides' member factories, and
///         the inner side's document type is only known from the <c>GroupJoin</c> node itself, so the
///         translation happens in the provider rather than in the parser.
///     </para>
/// </remarks>
internal sealed class GroupJoinData
{
    /// <summary>The inner queryable's own expression — its <c>Where</c> clauses included.</summary>
    public required Expression InnerSource { get; init; }

    /// <summary>The outer side of the <c>ON</c>, as a member of the outer document.</summary>
    public required LambdaExpression OuterKeySelector { get; init; }

    /// <summary>The inner side of the <c>ON</c>, as a member of the inner document.</summary>
    public required LambdaExpression InnerKeySelector { get; init; }

    /// <summary>
    ///     The join operator's own result selector — <c>(c, orders) =&gt; new { c, orders }</c> for a
    ///     <c>GroupJoin</c>, <c>(c, o) =&gt; …</c> for a plain <c>Join</c>.
    /// </summary>
    /// <remarks>
    ///     Its second parameter is the group in the first case and one inner row in the second, which
    ///     makes no difference to what it is used for: saying which member of the shape it builds came
    ///     from which side, so that whatever follows can be rewritten against the two documents.
    /// </remarks>
    public required LambdaExpression IntermediateSelector { get; init; }

    /// <summary>
    ///     Whether the operator was <c>GroupJoin</c> rather than <c>Join</c> — which decides only
    ///     whether a <c>SelectMany</c> is still owed.
    /// </summary>
    public required bool IsGrouped { get; init; }

    /// <summary>
    ///     Whether the chain has said everything the join needs. A <c>GroupJoin</c> without its
    ///     <c>SelectMany</c> has not, and is refused.
    /// </summary>
    public bool IsComplete => !IsGrouped || FinalSelector is not null;

    public required Type OuterType { get; init; }

    public required Type InnerType { get; init; }

    /// <summary>
    ///     What the caller asked each joined row to become, phrased against the intermediate shape —
    ///     the <c>SelectMany</c>'s <c>(temp, o) =&gt; new { temp.c.Name, o.Amount }</c>, or the
    ///     <c>Select</c> that follows a plain <c>Join</c> whose selector built a transparent identifier.
    /// </summary>
    /// <remarks>
    ///     Null when the join operator's own selector is already the answer, which is the plain
    ///     <c>Join</c> written in method syntax with the shape spelled out.
    /// </remarks>
    public LambdaExpression? FinalSelector { get; set; }

    /// <summary>
    ///     Set by a <c>DefaultIfEmpty()</c> in the <c>SelectMany</c> collection selector, which is how
    ///     LINQ spells a left outer join.
    /// </summary>
    public bool IsLeftJoin { get; set; }

    /// <summary>
    ///     Predicates written after the join, over the projected shape or over the intermediate one.
    /// </summary>
    /// <remarks>
    ///     Kept as expressions for the same reason the ordering keys are, and applied to the statement's
    ///     <c>WHERE</c> rather than to the <c>ON</c> — which is what a post-join <c>where</c> means. It
    ///     filters rows the join has already produced, so on a left join it can legitimately remove an
    ///     outer row whose inner side came back empty. That is the caller's own semantics: in memory,
    ///     the same clause after the same <c>DefaultIfEmpty()</c> would drop it too.
    /// </remarks>
    public List<LambdaExpression> Wheres { get; } = [];

    /// <summary>
    ///     Ordering keys named <em>after</em> the <c>SelectMany</c>, so over the projected shape rather
    ///     than over either document. Kept as expressions and resolved once the result selector has been
    ///     rewritten, which is what maps a projected member back to the document member behind it.
    /// </summary>
    public List<(LambdaExpression Key, bool Descending)> OrderBys { get; } = [];
}
