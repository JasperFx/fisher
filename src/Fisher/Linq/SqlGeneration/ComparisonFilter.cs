using Fisher.Storage;
using Weasel.Core;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.SqlGeneration;

/// <summary>
///     A comparison between a locator and a parameterised value — <c>locator = @p0</c>.
/// </summary>
/// <remarks>
///     <para>
///         The value is always bound as a parameter rather than inlined. Beyond the obvious injection
///         reason, it is what routes the value through Microsoft.Data.Sqlite's type mapping — and Fisher
///         stores Guids, booleans and timestamps as TEXT/INTEGER rather than native types, so callers
///         hand in values already converted by <see cref="Storage.SqliteStorageDialect{T}" />'s rules.
///     </para>
///     <para>
///         <b>A <see cref="decimal" /> is normalised here rather than by the caller</b> (fisher#304).
///         Four producers build a comparison and only one of them has a member to ask — see
///         <see cref="Storage.SqliteParameterValue.NormalizeDecimal" /> for the full argument, and
///         <see cref="ModuloFilter" />, which has made the same conversion at the same point since
///         fisher#161. The failure it prevents is silent and asymmetric: the provider binds a raw
///         decimal as TEXT, <c>json_extract</c> yields REAL, and SQLite orders every number below every
///         string — so <c>&gt;</c> and <c>=</c> matched nothing while <c>&lt;</c> matched everything.
///     </para>
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
        _value = SqliteParameterValue.NormalizeDecimal(value)!;
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
