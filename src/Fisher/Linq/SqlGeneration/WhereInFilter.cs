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
internal class WhereInFilter : ISqlFragment, INegatableFragment
{
    private readonly string _locator;
    private readonly string _nullLocator;
    private readonly IReadOnlyList<object?> _values;
    private readonly bool _matchesNull;
    private readonly bool _negated;

    /// <param name="nullLocator">
    ///     What a null in the set is tested against — the member's raw locator, since a null test asks
    ///     whether the member is present. Defaults to <paramref name="locator" />.
    /// </param>
    public WhereInFilter(string locator, IReadOnlyList<object?> values, string? nullLocator = null)
    {
        _locator = locator;
        _nullLocator = nullLocator ?? locator;

        // fisher#608: SQL's `in` never matches NULL, where C#'s Contains(null) does, so a null in the set
        // becomes an `is null` arm rather than a parameter.
        _matchesNull = values.Any(x => x is null);
        _values = [.. values.Where(x => x is not null).Select(SqliteParameterValue.NormalizeComparisonValue)];
    }

    private WhereInFilter(WhereInFilter source, bool negated)
    {
        _locator = source._locator;
        _nullLocator = source._nullLocator;
        _values = source._values;
        _matchesNull = source._matchesNull;
        _negated = negated;
    }

    /// <summary>
    ///     <c>!set.Contains(x.N)</c> is true in C# when <c>x.N</c> is null and the set holds no null, and
    ///     <c>not (NULL in (…))</c> is NULL, so the negation is <c>not coalesce(…, 0)</c> (fisher#608).
    /// </summary>
    public ISqlFragment Negate() => new WhereInFilter(this, !_negated);

    public void Apply(ICommandBuilder builder)
    {
        if (!_negated && !_matchesNull)
        {
            ApplyMembership(builder);
            return;
        }

        if (_negated)
        {
            builder.Append("not coalesce(");
        }

        if (_matchesNull)
        {
            builder.Append('(');
            ApplyMembership(builder);
            builder.Append(" or ");
            builder.Append(_nullLocator);
            builder.Append(" is null)");
        }
        else
        {
            ApplyMembership(builder);
        }

        if (_negated)
        {
            builder.Append(", 0)");
        }
    }

    /// <summary>
    ///     How many values a set holds before it is bound as one JSON array rather than one parameter
    ///     each (fisher#372) — far enough under SQLite's 32,766-variable ceiling that the rest of the
    ///     statement's parameters fit beside it.
    /// </summary>
    internal const int JsonArrayThreshold = 1_000;

    private void ApplyMembership(ICommandBuilder builder)
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
