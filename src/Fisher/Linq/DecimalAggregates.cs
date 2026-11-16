using System.Globalization;
using Fisher.Linq.Members;
using Microsoft.Data.Sqlite;
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

        // A collation through the function registry's extension point, since the registry has no collation
        // member of its own and a collation lives on the connection exactly as a function does. weasel#861
        // asks for one; when it ships, DecimalCollation goes.
        functions.Add(new DecimalCollation());
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
    private static decimal? ParseJson(string text)
    {
        var span = text.AsSpan().Trim();

        if (span.SequenceEqual("null".AsSpan()))
        {
            return null;
        }

        if (span.Length >= 2 && span[0] == '"' && span[^1] == '"')
        {
            span = span[1..^1];
        }

        return decimal.Parse(span, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static string Render(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     Immutable on purpose: Microsoft.Data.Sqlite hands every group the same seed, so a mutable reference
    ///     would be shared between groups.
    /// </summary>
    private readonly record struct Accumulator(decimal Value, long Count);

    private sealed class DecimalCollation() : SqliteFunction(CollationName)
    {
        public override void Register(SqliteConnection connection)
            => connection.CreateCollation(CollationName, Compare);

        private static int Compare(string? x, string? y)
            => decimal.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var left)
               && decimal.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var right)
                ? left.CompareTo(right)
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
