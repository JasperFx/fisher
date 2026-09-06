using Weasel.Core;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.SqlGeneration;

/// <summary>
///     A full-text predicate — <c>rowid in (select rowid from &lt;fts&gt; where &lt;fts&gt; match ?)</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>A sub-select rather than a join, and that is what made this cheap.</b> An FTS5 match
///         needs the virtual table in scope, which a join would put there — and a join would then have
///         to survive being composed with paging, ordering, the aggregate terminals and Fisher's own
///         join support, which is a change to <see cref="Statement" /> and to everything that wraps
///         one. As a sub-select it is an ordinary <see cref="ISqlFragment" />: it ANDs with any other
///         predicate, sits inside a <c>NOT</c>, survives every wrap, and the statement builder never
///         learns that full text exists. SQLite's planner runs the sub-select once and probes the
///         outer table by rowid.
///     </para>
///     <para>
///         <b>The rowid is qualified with the query's table alias when there is one</b>, so a
///         full-text predicate works on either side of a join for the same reason every other locator
///         does — see <c>MemberFactory</c>'s remarks on where an alias belongs.
///     </para>
///     <para>
///         <b>An empty query renders <c>1=0</c> rather than an empty <c>MATCH</c>.</b> FTS5 rejects an
///         empty query string outright, so the alternative is a runtime error for the caller who
///         searched for whitespace. Nothing matches the empty search, which is also the honest answer.
///     </para>
/// </remarks>
internal sealed class FullTextMatchFilter : ISqlFragment
{
    private readonly string _quotedTable;
    private readonly string _qualifier;
    private readonly string _query;

    public FullTextMatchFilter(string quotedTable, string qualifier, string query)
    {
        _quotedTable = quotedTable;
        _qualifier = qualifier;
        _query = query;
    }

    public void Apply(ICommandBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(_query))
        {
            builder.Append("1=0");
            return;
        }

        builder.Append(_qualifier);
        builder.Append("rowid in (select rowid from ");
        builder.Append(_quotedTable);
        builder.Append(" where ");
        builder.Append(_quotedTable);
        builder.Append(" match ");
        builder.AppendParameter(_query);
        builder.Append(')');
    }
}
