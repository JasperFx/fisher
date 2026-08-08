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
///         DISTINCT, GROUP BY / HAVING and <c>DistinctBy</c>'s <c>ROW_NUMBER()</c> subquery are
///         deliberately absent. They belong with the projection and grouping work that is deferred
///         until there are tests driving it; adding them now would be speculative SQL nothing exercises.
///     </para>
/// </remarks>
internal class Statement
{
    public string FromTable { get; set; } = "";
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
        }

        AppendWheres(builder);

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
