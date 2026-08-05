using Weasel.Core;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.SqlGeneration;

/// <summary>
///     A raw SQL WHERE fragment with no parameters — <c>"col is null"</c> and the like.
/// </summary>
internal class WhereFragment : ISqlFragment
{
    private readonly string _sql;

    public WhereFragment(string sql)
    {
        _sql = sql;
    }

    public void Apply(ICommandBuilder builder)
    {
        builder.Append(_sql);
    }
}
