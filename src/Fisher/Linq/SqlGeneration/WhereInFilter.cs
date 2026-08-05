using Weasel.Core;

namespace Fisher.Linq.SqlGeneration;

/// <summary>
///     Membership against a fixed set — <c>locator in (@p0, @p1, ...)</c>.
/// </summary>
/// <remarks>
///     An empty set renders <c>1=0</c> rather than <c>in ()</c>, which is a syntax error. Semantically
///     that is right anyway: nothing is a member of the empty set.
/// </remarks>
internal class WhereInFilter : ISqlFragment
{
    private readonly string _locator;
    private readonly IReadOnlyList<object?> _values;

    public WhereInFilter(string locator, IReadOnlyList<object?> values)
    {
        _locator = locator;
        _values = values;
    }

    public void Apply(ICommandBuilder builder)
    {
        if (_values.Count == 0)
        {
            builder.Append("1=0");
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
