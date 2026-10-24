using Fisher.Storage;
using Weasel.Core;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.SqlGeneration;

/// <summary>
///     Membership against a fixed set — <c>locator in (@p0, @p1, ...)</c>.
/// </summary>
/// <remarks>
///     <para>
///         An empty set renders <c>1=0</c> rather than <c>in ()</c>, which is a syntax error.
///         Semantically that is right anyway: nothing is a member of the empty set.
///     </para>
///     <para>
///         Each value is normalised the same way <see cref="ComparisonFilter" /> normalises its one
///         (fisher#304). <c>IsOneOf</c> and <c>Contains</c> both land here, from opposite directions,
///         so a conversion made by either caller alone would have missed the other.
///     </para>
///     <para>
///         <b>A large set is bound as ONE parameter</b> — <c>locator in (select value from json_each(@p0))</c>
///         — the shape <c>LoadManyAsync</c> has always used (fisher#372). One parameter per value hit
///         SQLite's 32,766-variable ceiling, so a <c>Contains()</c> over a long id list threw
///         <c>too many SQL variables</c> while the load over the same ids worked. The JSON array is written
///         only when every value's <c>json_each</c> form is what its bound parameter would have been
///         (<see cref="SqliteJsonArrays.TryWriteComparisonValues" />), so the two shapes answer alike; a
///         small set keeps the parameter list, which is what every existing plan and <c>ToSql</c> reader
///         has seen.
///     </para>
/// </remarks>
internal class WhereInFilter : ISqlFragment
{
    private readonly string _locator;
    private readonly IReadOnlyList<object?> _values;

    public WhereInFilter(string locator, IReadOnlyList<object?> values)
    {
        _locator = locator;
        _values = [.. values.Select(SqliteParameterValue.NormalizeDecimal)];
    }

    /// <summary>
    ///     How many values a set holds before it is bound as one JSON array rather than one parameter
    ///     each (fisher#372) — far enough under SQLite's 32,766-variable ceiling that the rest of the
    ///     statement's parameters fit beside it.
    /// </summary>
    internal const int JsonArrayThreshold = 1_000;

    public void Apply(ICommandBuilder builder)
    {
        if (_values.Count == 0)
        {
            builder.Append("1=0");
            return;
        }

        if (_values.Count > JsonArrayThreshold
            && SqliteJsonArrays.TryWriteComparisonValues(_values, out var json))
        {
            builder.Append(_locator);
            builder.Append(" in (select value from json_each(");
            builder.AppendParameter(json);
            builder.Append("))");
            return;
        }

        builder.Append(_locator);
        builder.Append(" in (");
        for (var i = 0; i < _values.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.AppendParameter(_values[i]!);
        }

        builder.Append(')');
    }
}
