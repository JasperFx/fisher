using System.Globalization;
using Fisher.Linq.Members;
using Fisher.Linq.SqlGeneration;
using Fisher.Storage;
using Weasel.Core;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq;

/// <summary>
///     Reading, comparing and ordering a <see cref="decimal" /> member exactly, rather than as the double
///     <c>json_extract</c> turns it into (fisher#609).
/// </summary>
/// <remarks>
///     <para>
///         <b>A decimal past fifteen significant digits is not a double.</b> <c>json_extract</c> hands a JSON
///         number back as REAL, so <c>0.1000000000000000001m</c> and <c>0.1000000000000000002m</c> were the
///         same value to every <c>Select</c>, <c>Where</c>, <c>OrderBy</c>, <c>Distinct</c> and
///         <c>GroupBy</c>, while loading the documents gave them back exactly. fisher#606 fixed the
///         aggregates by reading <see cref="IQueryableMember.JsonTextLocator" /> (the number as written) and
///         comparing it through the <c>fi_decimal</c> collation; this class is the same machinery for
///         everything else.
///     </para>
///     <para>
///         <b>The exact key is <c>(nullif(data -&gt; '$.path', 'null') collate fi_decimal)</c>.</b> The
///         <c>nullif</c> matters: <c>-&gt;</c> renders a JSON <c>null</c> as the TEXT <c>'null'</c>, which
///         would sort after every number and group apart from a missing key, where C# and
///         <c>json_extract</c> both have a null. It is what a projection selects (so the value keeps the scale
///         it was written with) and what a <c>HAVING</c> over a decimal <c>g.Key</c> compares.
///     </para>
///     <para>
///         <b>Ordering and grouping use <c>fi_decimal_key</c> instead</b>, a BLOB whose bytes sort as the
///         number (<see cref="DecimalAggregates.SortKey" />): <c>OrderBy</c>, <c>ThenBy</c>, <c>GroupBy</c>,
///         <c>DistinctBy</c>, a <c>Distinct</c> over a projected decimal, a join key and a keyset cursor's
///         seek. <c>1.0m</c> and <c>1.00m</c> get one key, as they are one value in C#. See
///         <see cref="OrderingFor" /> for why a key and not the collation.
///     </para>
///     <para>
///         ⚠️ <b>A comparison keeps the REAL as a band, and the band is wide on purpose.</b> SQLite's
///         text-to-double conversion is <em>not</em> correctly rounded (measured against SQLite 3.53: 350 of
///         400,000 random decimals came back one ulp from .NET's <c>double.Parse</c>), so the obvious
///         prefilter <c>json_extract(…) = ?</c> with a .NET-converted double drops a true match. Instead each
///         comparison is <c>lo &lt;= R &lt;= hi</c> around the value, with a tolerance of 2<sup>-40</sup> of
///         its magnitude — some two thousand ulps — and the exact text decides only what falls inside it.
///         That depends on nothing but each conversion being within an ulp or two of the truth, not on
///         either being monotone or on the two agreeing. The band is what lets an expression index or a
///         duplicated field's index still serve the predicate, and it keeps the collation, an application
///         function, off every row but the handful the REAL cannot tell apart from the value.
///     </para>
///     <para>
///         <b>Only a comparison C# makes in <see cref="decimal" /> is made exactly.</b> The value decides:
///         <c>x.Amount &gt; 0.5m</c> binds a decimal, but <c>(double)x.Amount &gt; 0.5</c> compares in
///         double in C# too, so it keeps the REAL comparison that agrees with it.
///     </para>
/// </remarks>
internal static class DecimalPrecision
{
    /// <summary>The band's half-width, relative to the value: 2^-40, about 2,000 ulps.</summary>
    private static readonly double Tolerance = Math.ScaleB(1.0, -40);

    /// <summary>Whether <paramref name="member" /> is a decimal that can be read exactly from the JSON.</summary>
    internal static bool IsExact(IQueryableMember? member)
        => member is not null && DecimalAggregates.Applies(member) && member.JsonTextLocator is not null;

    /// <summary>The member's number as written, collated to compare and order as a decimal.</summary>
    internal static string ExactKey(IQueryableMember member)
        => $"(nullif({member.JsonTextLocator}, 'null') collate {DecimalAggregates.CollationName})";

    /// <summary>
    ///     What a projection selects: the exact key for a decimal, so a <c>Distinct</c> over it compares as a
    ///     decimal, and the typed locator for anything else.
    /// </summary>
    internal static string ColumnFor(IQueryableMember member)
        => IsExact(member) ? ExactKey(member) : member.TypedLocator;

    /// <summary>
    ///     What <c>OrderBy</c>, <c>GroupBy</c>, <c>DistinctBy</c> and a join's key use: a decimal's
    ///     <c>fi_decimal_key</c>, whose bytes sort as the number, and the typed locator for anything else.
    /// </summary>
    /// <remarks>
    ///     A sort key rather than the collated text, measured over 10,000 rows: an <c>ORDER BY</c> through the
    ///     collation calls back into .NET for every comparison and took 31 ms where the REAL took 6, while the
    ///     key is one call per row and took 11. The REAL followed by the text would have been 7, and exact only
    ///     if SQLite's text-to-double conversion were monotone, which it does not promise (see the class
    ///     remarks); the key is exact by construction.
    /// </remarks>
    internal static string OrderingFor(IQueryableMember member)
        => IsExact(member) ? $"{DecimalAggregates.SortKeyFunctionName}({member.JsonTextLocator})" : member.TypedLocator;

    /// <summary><c>member op value</c>, exactly when C# compares in decimal.</summary>
    /// <param name="nullSafe">As on <see cref="ComparisonFilter" />: a null member answers as C# does (fisher#608).</param>
    internal static ISqlFragment Compare(IQueryableMember member, string op, object value, bool nullSafe = false)
        => IsExact(member) && value is decimal money
            ? new DecimalComparison(member.TypedLocator, ExactKey(member), op, money, nullSafe, negated: false)
            : new ComparisonFilter(member.TypedLocator, op, value, nullSafe);

    /// <summary><c>member in (values)</c>, exactly when every value is a decimal.</summary>
    /// <param name="nullLocator">As on <see cref="WhereInFilter" />: what a null in the set is tested against.</param>
    internal static ISqlFragment In(IQueryableMember member, IReadOnlyList<object?> values, string? nullLocator = null)
        => IsExact(member) && values.All(x => x is null or decimal)
            ? new DecimalInFilter(member.TypedLocator, ExactKey(member), values.OfType<decimal>().ToArray(),
                values.Any(x => x is null), nullLocator ?? member.TypedLocator, negated: false)
            : new WhereInFilter(member.TypedLocator, values, nullLocator);

    /// <summary>The value as it is bound for the collation to read.</summary>
    internal static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The REAL band around <paramref name="value" />.</summary>
    internal static (double Low, double High) Band(decimal value)
    {
        // double.Parse is correctly rounded, where (double)decimal is not; either would do inside the band.
        var real = double.Parse(Text(value), CultureInfo.InvariantCulture);
        var tolerance = Math.Abs(real) * Tolerance;
        return (real - tolerance, real + tolerance);
    }

    /// <summary>
    ///     <c>R</c> is the REAL locator (an index can serve it), <c>E</c> the exact key, <c>[lo, hi]</c> the
    ///     band around the value <c>v</c>:
    ///     <list type="bullet">
    ///         <item><c>=</c> → <c>R between lo and hi and E = v</c></item>
    ///         <item><c>!=</c> → <c>R &lt; lo or R &gt; hi or E != v</c></item>
    ///         <item><c>&gt;</c> → <c>R &gt;= lo and (R &gt; hi or E &gt; v)</c>, and <c>&gt;=</c> alike</item>
    ///         <item><c>&lt;</c> → <c>R &lt;= hi and (R &lt; lo or E &lt; v)</c>, and <c>&lt;=</c> alike</item>
    ///     </list>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Why it is exact: for a stored value <c>x</c>, <c>R</c> is within <c>ε|x|</c> of <c>x</c> and
    ///         the band's centre within <c>ε|v|</c> of <c>v</c>. So <c>x &gt;= v</c> puts <c>R</c> at or above
    ///         <c>lo</c> whenever the tolerance exceeds <c>2ε|v|</c>, and <c>x &lt;= v</c> puts it at or below
    ///         <c>hi</c>. Read the other way round, a REAL below the band means <c>x &lt; v</c> and one above it
    ///         means <c>x &gt; v</c>, with no conversion having to be monotone. Inside the band the text
    ///         decides, exactly.
    ///     </para>
    ///     <para>
    ///         Over a null member every arm reads <c>R</c>, which is NULL, so the body is NULL. That is
    ///         C#'s false for <c>=</c> and a range, which is what a plain <c>where</c> makes of it, and the
    ///         body is left bare there so an index still serves it. Where the fragment is null-safe
    ///         (fisher#608), <c>!=</c> and every negation wrap it in <c>coalesce(…, answer)</c> with C#'s
    ///         answer for a null member, as <see cref="SqlNegation.ApplyNullSafe" /> does for a plain
    ///         comparison.
    ///     </para>
    /// </remarks>
    private sealed class DecimalComparison(string real, string exact, string op, decimal value, bool nullSafe,
        bool negated) : ISqlFragment, INegatableFragment
    {
        public ISqlFragment Negate()
            => nullSafe
                ? new DecimalComparison(real, exact, op, value, nullSafe: true, negated: !negated)
                : new NotFragment(this);

        public void Apply(ICommandBuilder builder)
        {
            // C#'s answer for a null member: only != is true.
            var nullAnswer = op == "!=" ? "1" : "0";

            if (negated)
            {
                builder.Append("not coalesce(");
                ApplyBody(builder);
                builder.Append($", {nullAnswer})");
                return;
            }

            if (nullSafe && op == "!=")
            {
                builder.Append("coalesce(");
                ApplyBody(builder);
                builder.Append(", 1)");
                return;
            }

            ApplyBody(builder);
        }

        private void ApplyBody(ICommandBuilder builder)
        {
            var (low, high) = Band(value);
            var text = Text(value);

            switch (op)
            {
                case "=":
                    builder.Append($"({real} between ");
                    builder.AppendParameter(low);
                    builder.Append(" and ");
                    builder.AppendParameter(high);
                    builder.Append($" and {exact} = ");
                    builder.AppendParameter(text);
                    builder.Append(")");
                    break;

                case "!=":
                    builder.Append($"({real} < ");
                    builder.AppendParameter(low);
                    builder.Append($" or {real} > ");
                    builder.AppendParameter(high);
                    builder.Append($" or {exact} != ");
                    builder.AppendParameter(text);
                    builder.Append(")");
                    break;

                case ">" or ">=":
                    builder.Append($"({real} >= ");
                    builder.AppendParameter(low);
                    builder.Append($" and ({real} > ");
                    builder.AppendParameter(high);
                    builder.Append($" or {exact} {op} ");
                    builder.AppendParameter(text);
                    builder.Append("))");
                    break;

                case "<" or "<=":
                    builder.Append($"({real} <= ");
                    builder.AppendParameter(high);
                    builder.Append($" and ({real} < ");
                    builder.AppendParameter(low);
                    builder.Append($" or {exact} {op} ");
                    builder.AppendParameter(text);
                    builder.Append("))");
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(op), op, "Not a comparison operator");
            }
        }
    }

    /// <summary>
    ///     <c>IsOneOf</c> and <c>Contains</c> over a decimal: <c>(R in band₁ or R in band₂ …) and E in (v₁, v₂ …)</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A null in the set and a negation answer as <see cref="WhereInFilter" /> does (fisher#608): the
    ///         null becomes an <c>is null</c> arm rather than a value, and a negation is
    ///         <c>not coalesce(…, 0)</c>.
    ///     </para>
    ///     <para>
    ///         Past <see cref="WhereInFilter.JsonArrayThreshold" /> values the bands are dropped too, and the
    ///         set is bound as one JSON array of strings (fisher#372's reason): a TEXT <c>json_each</c> value,
    ///         so the collation compares it rather than SQLite's REAL.
    ///     </para>
    /// </remarks>
    private sealed class DecimalInFilter(string real, string exact, decimal[] values, bool matchesNull,
        string nullLocator, bool negated) : ISqlFragment, INegatableFragment
    {
        public ISqlFragment Negate()
            => new DecimalInFilter(real, exact, values, matchesNull, nullLocator, !negated);

        public void Apply(ICommandBuilder builder)
        {
            if (!negated && !matchesNull)
            {
                ApplyMembership(builder);
                return;
            }

            if (negated)
            {
                builder.Append("not coalesce(");
            }

            if (matchesNull)
            {
                builder.Append('(');
                ApplyMembership(builder);
                builder.Append($" or {nullLocator} is null)");
            }
            else
            {
                ApplyMembership(builder);
            }

            if (negated)
            {
                builder.Append(", 0)");
            }
        }

        private void ApplyMembership(ICommandBuilder builder)
        {
            if (values.Length == 0)
            {
                builder.Append("1=0");
                return;
            }

            if (values.Length > WhereInFilter.JsonArrayThreshold
                && SqliteJsonArrays.TryWriteComparisonValues(values.Select(x => (object?)Text(x)).ToList(),
                    out var json))
            {
                builder.Append($"({real} is not null and {exact} in (select value from json_each(");
                builder.AppendParameter(json);
                builder.Append(")))");
                return;
            }

            builder.Append("((");
            for (var i = 0; i < values.Length; i++)
            {
                var (low, high) = Band(values[i]);

                builder.Append(i == 0 ? $"{real} between " : $" or {real} between ");
                builder.AppendParameter(low);
                builder.Append(" and ");
                builder.AppendParameter(high);
            }

            builder.Append($") and {exact} in (");
            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.AppendParameter(Text(values[i]));
            }

            builder.Append("))");
        }
    }
}
