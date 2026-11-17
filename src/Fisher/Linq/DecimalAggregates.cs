using System.Globalization;
using Fisher.Linq.Members;
using Weasel.Core;
using Weasel.Core.SqlGeneration;
using Weasel.Sqlite.Functions;

namespace Fisher.Linq;

/// <summary>
///     <c>Sum</c>, <c>Average</c>, <c>Min</c> and <c>Max</c> over a <see cref="decimal" /> member, computed in
///     <see cref="decimal" /> rather than in SQLite's <c>double</c> (fisher#606).
/// </summary>
/// <remarks>
///     <para>
///         <b>SQLite has no decimal arithmetic, so its built-in aggregates were wrong in four ways.</b>
///         <c>json_extract</c> hands a JSON number back as REAL, so <c>sum(0.1, 0.2)</c> is
///         <c>0.30000000000000004</c> and a <c>HAVING</c> equality against <c>0.3m</c> missed while a
///         <c>&gt;</c> matched. A sum or a mean needing more than 15 significant digits came back rounded by
///         the double-to-decimal read. And a single value no double can hold — sixteen digits, or two values
///         differing in the nineteenth — was wrong even through <c>Min</c> and <c>Max</c>, which do not
///         accumulate but do convert.
///     </para>
///     <para>
///         <b>So the aggregates read the JSON text, not the REAL.</b> <c>data -&gt; '$.path'</c> hands back the
///         number exactly as System.Text.Json wrote it, and <c>fi_decimal_sum</c> and its three siblings parse
///         it as <see cref="decimal" /> and return the result as TEXT. Their semantics are LINQ to Objects':
///         <c>Average</c> is the decimal sum divided by the count, and NULL is skipped, as
///         <c>Sum(decimal?)</c> skips it.
///     </para>
///     <para>
///         <b>A TEXT result needs the <c>fi_decimal</c> collation to compare and order as a number</b>, so the
///         grouped forms are rendered <c>(fi_decimal_sum(…) collate fi_decimal)</c>. That one collation is what
///         makes a <c>HAVING</c> comparison, a grouped <c>OrderBy</c> and a <c>Distinct</c> over the column all
///         agree with C#, provided the value it is compared with is TEXT too —
///         <see cref="DecimalAggregateComparison" /> binds it that way, where fisher#304's normalisation would
///         bind a REAL that sorts below every TEXT.
///     </para>
///     <para>
///         <b>Application-defined functions are safe here</b> where vector search refused a trigger over one:
///         a query runs on Fisher's own connection, which registers them, and nothing a foreign writer does to
///         the file ever calls them.
///     </para>
/// </remarks>
internal static class DecimalAggregates
{
    internal const string CollationName = "fi_decimal";

    internal const string SortKeyFunctionName = "fi_decimal_key";

    /// <summary>
    ///     A BLOB whose byte order is the decimal's numeric order, so SQLite sorts and groups by
    ///     <c>memcmp</c> rather than calling back into .NET per comparison (fisher#609). Equal decimals of
    ///     different scale (<c>1.0m</c>, <c>1.00m</c>) get the same key.
    /// </summary>
    /// <remarks>
    ///     <c>0x80</c> is zero. A positive value is <c>0xFF</c>, a biased exponent byte and its significant
    ///     digits as ASCII, trailing zeros dropped, so a shorter digit string that is a prefix sorts first, as
    ///     <c>0.12</c> sorts before <c>0.125</c>. A negative value is <c>0x00</c>, the inverted exponent byte,
    ///     the nines' complement of its digits and a <c>0xFF</c> terminator, so the larger magnitude sorts
    ///     first and <c>-0.12</c> after <c>-0.125</c>.
    /// </remarks>
    internal static byte[] SortKey(decimal value)
    {
        Span<char> buffer = stackalloc char[64];
        Math.Abs(value).TryFormat(buffer, out var written, default, CultureInfo.InvariantCulture);
        return KeyFromDigits(buffer[..written], value < 0m)!;
    }

    /// <summary>
    ///     <c>fi_decimal_key</c> itself: the key of a JSON number's text. A plain number (<c>-12.50</c>, which
    ///     is how System.Text.Json writes every decimal) is keyed straight from its digits, with no
    ///     <see cref="decimal" /> parsed or formatted; anything else — an exponent, a quoted number, more
    ///     digits than a decimal holds — goes through <see cref="decimal" />, so the two can never disagree.
    /// </summary>
    internal static byte[]? SortKeyOf(object? value)
    {
        if (value is string text)
        {
            var span = text.AsSpan();

            if (span.SequenceEqual("null".AsSpan()))
            {
                return null;
            }

            var negative = span is ['-', ..];
            if (KeyFromDigits(negative ? span[1..] : span, negative) is { } key)
            {
                return key;
            }
        }

        return Read(value) is { } x ? SortKey(x) : null;
    }

    /// <summary>
    ///     The key of an unsigned number written as digits with at most one point, or null when the text is
    ///     anything else or carries more significant digits than a <see cref="decimal" />.
    /// </summary>
    private static byte[]? KeyFromDigits(ReadOnlySpan<char> text, bool negative)
    {
        if (text.IsEmpty || text.Length > 64)
        {
            return null;
        }

        Span<byte> digits = stackalloc byte[64];
        var count = 0;
        var leading = 0;
        var integerDigits = text.Length;
        var seenNonZero = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '.')
            {
                if (integerDigits != text.Length || i == 0 || i == text.Length - 1)
                {
                    return null;
                }

                integerDigits = i;
                continue;
            }

            if (c is < '0' or > '9')
            {
                return null;
            }

            if (!seenNonZero && c == '0')
            {
                leading++;
                continue;
            }

            seenNonZero = true;
            digits[count++] = (byte)c;
        }

        while (count > 0 && digits[count - 1] == (byte)'0')
        {
            count--;
        }

        if (count == 0)
        {
            return [0x80];
        }

        // More than a decimal holds: let decimal.Parse round it, as loading the document would.
        if (count > 29)
        {
            return null;
        }

        // The power of ten of the most significant digit's place, plus one: -27 for 1e-28, 29 for MaxValue.
        var exponent = (byte)(integerDigits - leading + 64);

        if (!negative)
        {
            var key = new byte[count + 2];
            key[0] = 0xFF;
            key[1] = exponent;
            digits[..count].CopyTo(key.AsSpan(2));
            return key;
        }

        var inverted = new byte[count + 3];
        inverted[1] = (byte)(0xFF - exponent);
        for (var i = 0; i < count; i++)
        {
            inverted[i + 2] = (byte)('9' - digits[i] + '0');
        }

        inverted[^1] = 0xFF;
        return inverted;
    }

    /// <summary>
    ///     The decimal a <see cref="SortKey" /> was made from, at its smallest scale — what a keyset cursor
    ///     reads back from the ordering column.
    /// </summary>
    internal static decimal FromSortKey(byte[] key)
    {
        if (key is [0x80])
        {
            return 0m;
        }

        var positive = key[0] == 0xFF;
        var exponent = (positive ? key[1] : 0xFF - key[1]) - 64;
        var digits = positive ? key.AsSpan(2) : key.AsSpan(2, key.Length - 3);

        // 0.d1d2…dn × 10^exponent is the integer d1d2…dn × 10^(exponent - n), which a decimal holds exactly:
        // the value came from one, and dropping trailing zeros only ever lowered the scale it needs.
        var text = new char[digits.Length];
        for (var i = 0; i < digits.Length; i++)
        {
            text[i] = (char)(positive ? digits[i] : '9' - digits[i] + '0');
        }

        var value = decimal.Parse($"{new string(text)}E{exponent - digits.Length}", NumberStyles.Float,
            CultureInfo.InvariantCulture);

        return positive ? value : -value;
    }

    internal static void Register(SqliteFunctionRegistry functions)
    {
        functions.AddAggregate<Accumulator, string?>(FunctionFor(AggregateFunction.Sum), default,
            static (acc, value) => Read(value) is { } x ? new Accumulator(acc.Value + x, acc.Count + 1) : acc,
            static acc => acc.Count == 0 ? null : Render(acc.Value));

        functions.AddAggregate<Accumulator, string?>(FunctionFor(AggregateFunction.Average), default,
            static (acc, value) => Read(value) is { } x ? new Accumulator(acc.Value + x, acc.Count + 1) : acc,
            static acc => acc.Count == 0 ? null : Render(acc.Value / acc.Count));

        functions.AddAggregate<Accumulator, string?>(FunctionFor(AggregateFunction.Min), default,
            static (acc, value) => Read(value) is { } x
                ? new Accumulator(acc.Count == 0 || x < acc.Value ? x : acc.Value, acc.Count + 1)
                : acc,
            static acc => acc.Count == 0 ? null : Render(acc.Value));

        functions.AddAggregate<Accumulator, string?>(FunctionFor(AggregateFunction.Max), default,
            static (acc, value) => Read(value) is { } x
                ? new Accumulator(acc.Count == 0 || x > acc.Value ? x : acc.Value, acc.Count + 1)
                : acc,
            static acc => acc.Count == 0 ? null : Render(acc.Value));

        functions.AddScalar<byte[]?>(SortKeyFunctionName,
            static (object? value) => SortKeyOf(value), isDeterministic: true);

        functions.AddCollation(CollationName, CompareAsNumbers);
    }

    internal static string FunctionFor(AggregateFunction function) => function switch
    {
        AggregateFunction.Sum => "fi_decimal_sum",
        AggregateFunction.Average => "fi_decimal_avg",
        AggregateFunction.Min => "fi_decimal_min",
        AggregateFunction.Max => "fi_decimal_max",
        _ => throw new ArgumentOutOfRangeException(nameof(function))
    };

    /// <summary>Whether <paramref name="member" /> is a decimal, whose aggregates have to go through here.</summary>
    internal static bool Applies(IQueryableMember member)
        => (Nullable.GetUnderlyingType(member.MemberType) ?? member.MemberType) == typeof(decimal);

    /// <summary>
    ///     The JSON text of a decimal member, refusing by name a member that has none rather than falling back to
    ///     the REAL that loses digits.
    /// </summary>
    internal static string JsonTextOf(IQueryableMember member, string described)
        => member.JsonTextLocator
           ?? throw new BadLinqExpressionException(
               $"Fisher cannot aggregate the decimal '{described}' exactly: it is not read from the document's "
               + "JSON, and SQLite would compute it in double.");

    /// <summary>The grouped form: the aggregate, collated so it compares and orders as a number.</summary>
    internal static string Grouped(AggregateFunction function, string jsonText)
        => $"({FunctionFor(function)}({jsonText}) collate {CollationName})";

    private static decimal? Read(object? value) => value switch
    {
        null or DBNull => null,
        string text => ParseJson(text),
        long integer => integer,
        double real => (decimal)real,
        _ => throw new ArgumentException($"fi_decimal: cannot read a {value.GetType().Name} as a decimal")
    };

    /// <summary>
    ///     A JSON number's text, or a number written as a JSON string (<c>JsonNumberHandling.WriteAsString</c>).
    ///     JSON <c>null</c> is skipped, like SQL NULL.
    /// </summary>
    internal static decimal? ParseJson(string text)
        => TryParseJson(text, out var value)
            ? value
            : throw new FormatException($"fi_decimal: '{text}' is not a JSON number");

    /// <summary>
    ///     <see cref="ParseJson" /> without the throw, for the collation, which has to order text it cannot
    ///     read rather than fail the statement. True with a null value for JSON <c>null</c>.
    /// </summary>
    internal static bool TryParseJson(string? text, out decimal? value)
    {
        value = null;

        if (text is null)
        {
            return false;
        }

        var span = text.AsSpan().Trim();

        if (span.SequenceEqual("null".AsSpan()))
        {
            return true;
        }

        if (span.Length >= 2 && span[0] == '"' && span[^1] == '"')
        {
            span = span[1..^1];
        }

        if (decimal.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static string Render(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     Immutable on purpose: Microsoft.Data.Sqlite hands every group the same seed, so a mutable reference
    ///     would be shared between groups.
    /// </summary>
    private readonly record struct Accumulator(decimal Value, long Count);

    // The fi_decimal collation. fisher#609: the same reading the aggregates make, so a number written as a JSON
    // string compares as the number. Identical text is equal without parsing, which is the common case for a key.
    private static int CompareAsNumbers(string? x, string? y)
    {
        if (string.Equals(x, y, StringComparison.Ordinal))
        {
            return 0;
        }

        return TryParseJson(x, out var left) && left.HasValue
               && TryParseJson(y, out var right) && right.HasValue
            ? left.Value.CompareTo(right.Value)
            : string.CompareOrdinal(x, y);
    }
}

/// <summary>
///     <c>HAVING (fi_decimal_sum(…) collate fi_decimal) &gt; ?</c>, binding the value as decimal TEXT so the
///     collation compares it exactly.
/// </summary>
internal sealed class DecimalAggregateComparison(string aggregate, string op, object value) : ISqlFragment
{
    public void Apply(ICommandBuilder builder)
    {
        builder.Append(aggregate);
        builder.Append(" ");
        builder.Append(op);
        builder.Append(" ");
        builder.AppendParameter(Convert.ToDecimal(value, CultureInfo.InvariantCulture)
            .ToString(CultureInfo.InvariantCulture));
    }
}
