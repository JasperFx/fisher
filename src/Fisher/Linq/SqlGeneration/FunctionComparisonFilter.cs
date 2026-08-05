using Weasel.Core;

namespace Fisher.Linq.SqlGeneration;

/// <summary>
///     A comparison whose locator is wrapped in a SQL function — <c>lower(locator) = @p0</c>.
/// </summary>
/// <remarks>
///     This is how case-insensitive string comparison is expressed, and on SQLite that matters more
///     than it does on the siblings. SQL Server's default collation is case-<em>in</em>sensitive, so
///     Polecat gets `StringComparison.OrdinalIgnoreCase` nearly for free and only reaches for
///     <c>LOWER</c> deliberately. SQLite's default collation is case-<em>sensitive</em> (the same
///     property behind the Guid-casing trap in CLAUDE.md), so an ordinal-ignore-case comparison must
///     lower both sides explicitly or it silently returns nothing.
/// </remarks>
internal class FunctionComparisonFilter : ISqlFragment
{
    private readonly string _function;
    private readonly string _locator;
    private readonly string _op;
    private readonly object _value;

    public FunctionComparisonFilter(string function, string locator, string op, object value)
    {
        _function = function;
        _locator = locator;
        _op = op;
        _value = value;
    }

    public void Apply(ICommandBuilder builder)
    {
        builder.Append(_function);
        builder.Append('(');
        builder.Append(_locator);
        builder.Append(") ");
        builder.Append(_op);
        builder.Append(' ');
        builder.AppendParameter(_value);
    }
}
