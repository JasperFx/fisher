using Weasel.Core;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.Joins;

/// <summary>
///     One joined table on a <see cref="SqlGeneration.Statement" /> — <c>join fi_doc_order inner_t on
///     … = … and …</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every condition about the inner side goes in the <c>ON</c> clause, not the <c>WHERE</c>.</b>
///         For a left join that is a correctness matter rather than a preference: an inner-side term in
///         the <c>WHERE</c> is evaluated after the join has already produced NULLs for a non-matching
///         outer row, so <c>inner_t.is_deleted = 0</c> there turns the outer join back into an inner one
///         and drops exactly the rows the left join exists to keep. For an inner join the two placements
///         mean the same thing, so putting everything in the <c>ON</c> is one rule that is always right
///         instead of a branch that is right twice.
///     </para>
///     <para>
///         The <c>WHERE</c> is still where the <em>outer</em> side's terms go, including the outer half
///         of the implicit tenant, hierarchy and soft-delete filters.
///     </para>
/// </remarks>
internal sealed class JoinClause
{
    /// <summary>The joined table, quoted.</summary>
    public required string Table { get; init; }

    /// <summary>
    ///     The alias every locator on the inner side is qualified with, or null to join the table
    ///     under its own name.
    /// </summary>
    /// <remarks>
    ///     Null only for the FTS5 join (fisher#220), and not as a style choice: SQLite refuses an
    ///     alias on the left of <c>MATCH</c> — <c>… join ftsdoc f … where f match ?</c> fails with
    ///     "no such column: f", because the expression parser resolves a bare identifier there as a
    ///     column rather than as a table. The FTS5 table therefore has to appear under its own name,
    ///     which is also what <c>bm25()</c> has to be handed.
    /// </remarks>
    public string? Alias { get; init; }

    /// <summary>The outer key locator, already qualified with the outer alias.</summary>
    public required string OuterKeyLocator { get; init; }

    /// <summary>The inner key locator, already qualified with <see cref="Alias" />.</summary>
    public required string InnerKeyLocator { get; init; }

    /// <summary>
    ///     <c>left join</c> rather than <c>join</c> — set by a <c>DefaultIfEmpty()</c> in the
    ///     <c>SelectMany</c>.
    /// </summary>
    public bool IsLeftJoin { get; init; }

    /// <summary>Everything else the inner side must satisfy, ANDed onto the <c>ON</c>.</summary>
    public List<ISqlFragment> On { get; } = [];

    public void Apply(ICommandBuilder builder)
    {
        builder.Append(IsLeftJoin ? " left join " : " join ");
        builder.Append(Table);

        if (Alias is not null)
        {
            builder.Append(' ');
            builder.Append(Alias);
        }

        builder.Append(" on ");
        builder.Append(OuterKeyLocator);
        builder.Append(" = ");
        builder.Append(InnerKeyLocator);

        foreach (var fragment in On)
        {
            builder.Append(" and ");
            fragment.Apply(builder);
        }
    }
}
