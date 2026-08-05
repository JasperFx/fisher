using Weasel.Core;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.SqlGeneration;

/// <summary>
///     A comparison between a locator and a parameterised value — <c>locator = @p0</c>.
/// </summary>
/// <remarks>
///     The value is always bound as a parameter rather than inlined. Beyond the obvious injection
///     reason, it is what routes the value through Microsoft.Data.Sqlite's type mapping — and Fisher
///     stores Guids, booleans and timestamps as TEXT/INTEGER rather than native types, so callers hand
///     in values already converted by <see cref="Storage.SqliteStorageDialect{T}" />'s rules.
/// </remarks>
internal class ComparisonFilter : ISqlFragment
{
    private readonly string _locator;
    private readonly string _op;
    private readonly object _value;

    public ComparisonFilter(string locator, string op, object value)
    {
        _locator = locator;
        _op = op;
        _value = value;
    }

    public void Apply(ICommandBuilder builder)
    {
        builder.Append(_locator);
        builder.Append(' ');
        builder.Append(_op);
        builder.Append(' ');
        builder.AppendParameter(_value);
    }
}
