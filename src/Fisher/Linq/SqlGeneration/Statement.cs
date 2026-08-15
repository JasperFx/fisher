using Weasel.Core;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.SqlGeneration;

/// <summary>
///     A complete SELECT: columns, table, WHERE, ORDER BY and paging.
/// </summary>
/// <remarks>
///     <para>
///         This is the one file in <c>SqlGeneration</c> that is genuinely dialect-specific rather than a
///         straight mirror of Polecat's, because paging is where T-SQL and SQLite diverge most:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <c>TOP(n)</c> and <c>OFFSET n ROWS FETCH NEXT m ROWS ONLY</c> become
///                 <c>limit m offset n</c>. SQLite has one form for both, so the "TOP when there is no
///                 offset" special case Polecat needs simply does not arise.
///             </description>
///         </item>
///         <item>
///             <description>
///                 T-SQL requires an ORDER BY before OFFSET, which is why Polecat emits
///                 <c>ORDER BY (SELECT NULL)</c> as a filler. SQLite does not, so there is no filler —
///                 emitting one would impose a sort the caller never asked for.
///             </description>
///         </item>
///         <item>
///             <description>
///                 An offset with no limit is <c>limit -1 offset n</c>. SQLite reads a negative limit as
///                 "no limit", and <c>offset</c> is not valid without a preceding <c>limit</c>.
///             </description>
///         </item>
///     </list>
///     <para>
///         Joins live here rather than in a parallel statement type of their own, which is the other
///         place Polecat diverges — its <c>JoinStatement</c> re-implements the select list, the wheres,
///         the ordering and the paging, so anything built for one shape has to be built again for the
///         other. Fisher's <c>Count</c>, <c>Any</c>, paging and <c>ToSql</c> all serve a join without
///         knowing it is one.
///     </para>
/// </remarks>
internal class Statement
{
    public string FromTable { get; set; } = "";

    /// <summary>
    ///     An alias for <see cref="FromTable" />, set only when the statement joins (fisher#25).
    /// </summary>
    /// <remarks>
    ///     Null everywhere else on purpose: an unjoined query's locators are unqualified, and aliasing
    ///     the table without a reason would make every rendered statement in every test diff.
    /// </remarks>
    public string? FromAlias { get; set; }

    /// <summary>The joined tables, in order. Empty for every query that is not a join.</summary>
    public List<Joins.JoinClause> Joins { get; } = [];

    /// <summary>
    ///     Every document type this statement reads from — the source type, plus one per joined side.
    /// </summary>
    /// <remarks>
    ///     Carried so <c>FisherQueryProvider.CommandFor</c> can provision the tables before executing
    ///     (fisher#74). It is the only thing on a statement that is not about rendering SQL, and it is
    ///     here rather than at each terminal because <c>CommandFor</c> is where every terminal converges
    ///     — a per-terminal call would be a dozen copies of one line, one of which would be forgotten,
    ///     and the terminal that forgot it would be the one nobody exercises against a fresh database.
    /// </remarks>
    public List<Type> DocumentTypes { get; } = [];

    public string SelectColumns { get; set; } = "data";
    public List<ISqlFragment> Wheres { get; } = [];
    public List<(string Locator, bool Descending)> OrderBys { get; } = [];
    public int? Limit { get; set; }
    public int? Offset { get; set; }

    /// <summary>
    ///     When set, this statement selects from the nested statement instead of from
    ///     <see cref="FromTable" />.
    /// </summary>
    /// <remarks>
    ///     Exists for counting a paged query: <c>Take(5).CountAsync()</c> must count the page, not the
    ///     table, so the paged query becomes a subquery and the count wraps it. Modelled as a nested
    ///     <see cref="Statement" /> rather than pre-rendered SQL text so the inner statement's
    ///     parameters are appended to the same command builder — rendering it separately would leave
    ///     its parameters behind.
    /// </remarks>
    public Statement? Subquery { get; set; }

    /// <summary>
    ///     Wraps the statement so it yields a single 0/1 rather than rows — what
    ///     <c>EventsExistAsync</c> reads. SQLite has no boolean type, so this is an INTEGER by
    ///     necessity, matching how Fisher stores every other boolean.
    /// </summary>
    public bool IsExistsWrapper { get; set; }

    public void Apply(ICommandBuilder builder)
    {
        if (IsExistsWrapper)
        {
            builder.Append("select exists (");
            ApplyInner(builder);
            builder.Append(')');
            return;
        }

        ApplyInner(builder);
    }

    /// <summary>
    ///     Emits <c>select distinct</c>.
    /// </summary>
    /// <remarks>
    ///     Only ever set on a projected statement. DISTINCT over the document's own columns would
    ///     compare whole serialized JSON strings byte for byte, which is an answer nobody wants and one
    ///     that would look right on small test data — so the provider refuses it rather than emitting
    ///     it. <c>DistinctBy</c> is the operator for deduplicating documents.
    /// </remarks>
    public bool IsDistinct { get; set; }

    /// <summary>
    ///     How long to wait for the async daemon to catch up before running — <c>QueryForNonStaleData</c>.
    /// </summary>
    /// <remarks>
    ///     Not SQL, and it sits here anyway because <see cref="Statement" /> is what every execution path
    ///     is handed. <see cref="EffectiveNonStaleTimeout" /> reads through <see cref="Subquery" /> so
    ///     that wrapping a statement — for a count, a page, an aggregate or a reversal — carries it
    ///     without each wrap site having to remember to.
    /// </remarks>
    public TimeSpan? NonStaleTimeout { get; set; }

    /// <inheritdoc cref="NonStaleTimeout" />
    public TimeSpan? EffectiveNonStaleTimeout => NonStaleTimeout ?? Subquery?.EffectiveNonStaleTimeout;

    /// <summary>The <c>GROUP BY</c> key expression, or null.</summary>
    public string? GroupBy { get; set; }

    /// <summary>
    ///     <c>HAVING</c> terms, ANDed. Separate from <see cref="Wheres" /> because they run after the
    ///     rows have been collapsed, which is the whole distinction.
    /// </summary>
    public List<ISqlFragment> Havings { get; } = [];

    private void ApplyInner(ICommandBuilder builder)
    {
        builder.Append("select ");

        if (IsDistinct)
        {
            builder.Append("distinct ");
        }

        builder.Append(SelectColumns);
        builder.Append(" from ");

        if (Subquery != null)
        {
            builder.Append('(');
            Subquery.Apply(builder);
            builder.Append(')');
        }
        else
        {
            builder.Append(FromTable);

            if (FromAlias is not null)
            {
                builder.Append(' ');
                builder.Append(FromAlias);
            }
        }

        foreach (var join in Joins)
        {
            join.Apply(builder);
        }

        AppendWheres(builder);

        if (GroupBy is not null)
        {
            builder.Append(" group by ");
            builder.Append(GroupBy);
        }

        if (Havings.Count > 0)
        {
            builder.Append(" having ");

            for (var i = 0; i < Havings.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(" and ");
                }

                Havings[i].Apply(builder);
            }
        }

        if (OrderBys.Count > 0)
        {
            builder.Append(" order by ");
            for (var i = 0; i < OrderBys.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(OrderBys[i].Locator);
                if (OrderBys[i].Descending)
                {
                    builder.Append(" desc");
                }
            }
        }

        AppendPaging(builder);
    }

    private void AppendPaging(ICommandBuilder builder)
    {
        if (Limit.HasValue)
        {
            builder.Append(" limit ");
            builder.Append(Limit.Value.ToString());
        }
        else if (Offset.HasValue)
        {
            // offset is not valid on its own, and a negative limit means "unbounded".
            builder.Append(" limit -1");
        }

        if (Offset.HasValue)
        {
            builder.Append(" offset ");
            builder.Append(Offset.Value.ToString());
        }
    }

    private void AppendWheres(ICommandBuilder builder)
    {
        if (Wheres.Count == 0)
        {
            return;
        }

        builder.Append(" where ");
        for (var i = 0; i < Wheres.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(" and ");
            }

            Wheres[i].Apply(builder);
        }
    }
}
