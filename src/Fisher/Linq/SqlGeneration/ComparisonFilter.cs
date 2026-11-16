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
///     <para>
///         <b>So is a <see cref="float" /></b> (fisher#605), to the double its stored shortest text reads
///         back as — see <see cref="Storage.SqliteParameterValue.FloatAsStored" />. Widened, <c>0.3f</c>
///         equals no stored <c>0.3</c>.
///     </para>
/// </remarks>
internal class ComparisonFilter : ISqlFragment, INegatableFragment
{
    private readonly string _locator;
    private readonly string _op;
    private readonly object _value;
    private readonly bool _nullSafe;
    private readonly bool _negated;

    /// <param name="nullSafe">
    ///     The locator reads a member C# compares as a lifted (or reference) value, so a null there is C#'s
    ///     false for every operator but <c>!=</c>, which is true (fisher#608). <c>!=</c> then renders
    ///     <c>is not</c>, and <see cref="Negate" /> answers two-valued. Off for a string method's locator,
    ///     whose C# call throws on a null receiver, and for a member read through a union case, whose C#
    ///     cast throws on a row of another case: those keep SQL's semantics.
    /// </param>
    public ComparisonFilter(string locator, string op, object value, bool nullSafe = false)
        : this(locator, op, SqliteParameterValue.NormalizeComparisonValue(value)!, nullSafe, negated: false)
    {
    }

    private ComparisonFilter(string locator, string op, object normalized, bool nullSafe, bool negated)
    {
        _locator = locator;
        _op = op;
        _value = normalized;
        _nullSafe = nullSafe;
        _negated = negated;
    }

    public ISqlFragment Negate()
        => _nullSafe
            ? new ComparisonFilter(_locator, _op, _value, nullSafe: true, negated: !_negated)
            : new NotFragment(this);

    public void Apply(ICommandBuilder builder)
    {
        if (_nullSafe)
        {
            SqlNegation.ApplyNullSafe(builder, b => b.Append(_locator), _op, _negated,
                b => b.AppendParameter(_value));
            return;
        }

        builder.Append(_locator);
        builder.Append(' ');
        builder.Append(_op);
        builder.Append(' ');
        builder.AppendParameter(_value);
    }
}
