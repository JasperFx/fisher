using Weasel.Core;

namespace Fisher.Linq.SqlGeneration;

/// <summary>
///     Raw SQL appended verbatim, with no parameterisation.
/// </summary>
/// <remarks>
///     Only ever constructed from SQL the parser itself composed — a locator, an operator, a column
///     name. Never from a caller-supplied value; those go through <see cref="ComparisonFilter" /> so
///     they are bound as parameters.
/// </remarks>
internal class LiteralSqlFragment : ISqlFragment
{
    private readonly string _sql;

    public LiteralSqlFragment(string sql)
    {
        _sql = sql;
    }

    public void Apply(ICommandBuilder builder)
    {
        builder.Append(_sql);
    }
}
