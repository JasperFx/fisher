using Weasel.Core;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.SqlGeneration;

/// <summary>
///     A locator wrapped in a SQL function — <c>lower(json_extract(data,'$.Name'))</c>. Carried as a
///     value so a parent comparison can compose against the wrapped form rather than the bare locator.
/// </summary>
internal class SqlFunctionLocator : ISqlFragment
{
    public string Function { get; }
    public string InnerLocator { get; }

    public SqlFunctionLocator(string function, string innerLocator)
    {
        Function = function;
        InnerLocator = innerLocator;
    }

    public string FullLocator => $"{Function}({InnerLocator})";

    public void Apply(ICommandBuilder builder)
    {
        builder.Append(FullLocator);
    }
}
