using System.Text;

namespace Fisher.Linq;

/// <summary>
///     One row of SQLite's <c>EXPLAIN QUERY PLAN</c> (fisher#202).
/// </summary>
/// <param name="Id">The step's own id.</param>
/// <param name="ParentId">The step this one sits under; 0 at the top level.</param>
/// <param name="Detail">
///     SQLite's own description of the step — <c>SEARCH fi_doc_angler USING INDEX idx_…</c>,
///     <c>SCAN fi_doc_angler</c>, <c>USE TEMP B-TREE FOR ORDER BY</c>.
/// </param>
public sealed record QueryPlanStep(int Id, int ParentId, string Detail);

/// <summary>
///     What SQLite says it would do with a query — <c>ExplainAsync</c> (fisher#202).
/// </summary>
/// <remarks>
///     <para>
///         The question this exists to answer is the one
///         <see href="https://github.com/JasperFx/fisher/issues/16">fisher#16</see> leaves a reader
///         unable to ask: <b>is the index I declared actually being used?</b> SQLite's planner uses an
///         expression index only when the query's expression matches the index's, so an index built
///         from a hand-written <c>json_extract</c> is created without error, never used, and reports
///         nothing. Until now the only way to check was to hand-write the SQL and the
///         <c>EXPLAIN QUERY PLAN</c> around it, which is what Fisher's own index tests do.
///     </para>
///     <para>
///         <b>Nothing like Marten's <c>QueryPlan</c>, deliberately.</b> PostgreSQL's
///         <c>EXPLAIN (FORMAT JSON)</c> yields a nested document with costs, row estimates and an
///         optional execution pass; SQLite's is a flat four-column result set of prose, with no costs
///         and no <c>ANALYZE</c> mode. So there is no portable shape to mirror and none is invented —
///         <see cref="Steps" /> is what SQLite said, in the order it said it.
///     </para>
/// </remarks>
public sealed class QueryPlan
{
    internal QueryPlan(string sql, IReadOnlyList<QueryPlanStep> steps)
    {
        Sql = sql;
        Steps = steps;
    }

    /// <summary>The statement that was explained, with parameter names rather than values.</summary>
    public string Sql { get; }

    /// <summary>SQLite's plan, in the order it reported it.</summary>
    public IReadOnlyList<QueryPlanStep> Steps { get; }

    /// <summary>
    ///     Whether any step reports reading through an index.
    /// </summary>
    /// <remarks>
    ///     <b>A reading of SQLite's own prose, and honestly so.</b> There is no structured field to
    ///     consult — the plan is text — so this looks for <c>USING INDEX</c> / <c>USING COVERING
    ///     INDEX</c> in <see cref="QueryPlanStep.Detail" />, which is exactly what a human reads it
    ///     for. Treat it as a convenience over <see cref="Steps" /> rather than as a guarantee, and
    ///     read <see cref="Steps" /> when the answer matters.
    /// </remarks>
    public bool UsesIndex
        => Steps.Any(x => x.Detail.Contains("USING INDEX", StringComparison.OrdinalIgnoreCase)
                          || x.Detail.Contains("USING COVERING INDEX", StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc cref="UsesIndex" />
    /// <summary>Whether any step reports a full table scan.</summary>
    public bool ScansTable
        => Steps.Any(x => x.Detail.StartsWith("SCAN ", StringComparison.OrdinalIgnoreCase));

    public override string ToString()
    {
        var text = new StringBuilder(Sql);

        foreach (var step in Steps)
        {
            text.AppendLine();
            text.Append("  ").Append(step.Detail);
        }

        return text.ToString();
    }
}
