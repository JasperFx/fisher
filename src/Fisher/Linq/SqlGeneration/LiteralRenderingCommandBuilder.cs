using System.Data.Common;
using System.Globalization;
using System.Text;
using Fisher.Storage;
using Microsoft.Data.Sqlite;
using Weasel.Core;
using Weasel.Core.SqlGeneration;

namespace Fisher.Linq.SqlGeneration;

/// <summary>
///     Renders a parsed <see cref="ISqlFragment" /> with its values written into the SQL as literals
///     instead of bound as parameters — for the one place that cannot take parameters at all, a
///     partial index's <c>WHERE</c> clause (fisher#218).
/// </summary>
/// <remarks>
///     <para>
///         ⚠️ <b>This is the exception to "values are always bound", and it exists because DDL has
///         nowhere to bind them.</b> <c>CREATE INDEX … WHERE …</c> is a schema definition SQLite
///         stores and re-evaluates per row; there is no command to carry a parameter collection. So
///         either the predicate is rendered, or partial indexes are not offered.
///     </para>
///     <para>
///         <b>What makes it safe is not the escaping, it is the reach.</b> The values reaching here
///         are constants a developer wrote in a configuration lambda at startup — the same trust class
///         as a <c>[JsonPropertyName]</c>, which <c>MemberFactory</c> escapes for defence in depth
///         (fisher#162). They are never a request value: an index is declared once, when the store is
///         configured. The escaping is defence in depth on top of that, and
///         <see cref="Render(object?)" /> <b>refuses by name</b> anything it cannot render
///         unambiguously rather than reaching for <c>ToString()</c> — which is exactly the
///         marten#4954 class fisher#162 closed one type over.
///     </para>
///     <para>
///         <b>Why route through the ordinary parser at all, rather than taking a SQL string?</b>
///         Because SQLite uses a partial index only when the query's <c>WHERE</c> implies the index's,
///         and that check works over the terms as written. A predicate built by the same
///         <c>WhereClauseParser</c> and the same <c>MemberFactory</c> the query goes through therefore
///         matches; a hand-written one is the fisher#16 failure — created without error, never used,
///         reporting nothing.
///     </para>
/// </remarks>
internal sealed class LiteralRenderingCommandBuilder : ICommandBuilder
{
    private readonly StringBuilder _sql = new();

    public string TenantId { get; set; } = string.Empty;

    public string? LastParameterName => null;

    public override string ToString() => _sql.ToString();

    public void Append(string sql) => _sql.Append(sql);

    public void Append(char character) => _sql.Append(character);

    public DbParameter AppendParameter(object value)
    {
        _sql.Append(Render(value is DBNull ? null : value));

        // Nothing reads it back — the value is already in the text — but the contract says a parameter
        // comes out, and a fragment is free to stamp a DbType on what it is handed.
        return new SqliteParameter { Value = value };
    }

    public IGroupedParameterBuilder CreateGroupedParameterBuilder(char? separator = null)
        => new GroupedParameterBuilder(this, separator);

    public void AppendParameters(params object[] parameters)
    {
        foreach (var parameter in parameters)
        {
            AppendParameter(parameter);
        }
    }

    /// <summary>
    ///     A literal for one value, or a refusal naming the type.
    /// </summary>
    /// <remarks>
    ///     The conversions match <see cref="SqliteParameterValue" />'s, because an index predicate has
    ///     to compare against what Fisher actually stored: a Guid as lowercase canonical text, a
    ///     timestamp in the fixed-width UTC form, a decimal as a REAL. An index whose predicate
    ///     disagreed with the query's would simply never be used, which is the silent failure this
    ///     whole area is careful about.
    /// </remarks>
    internal static string Render(object? value)
        => value switch
        {
            null => "null",
            bool flag => flag ? "1" : "0",
            string text => Quote(text),
            Guid guid => Quote(guid.ToString()),
            DateTimeOffset or DateTime => Quote(
                (string)SqliteParameterValue.ToDatabaseValue(value)),
            char character => Quote(character.ToString()),
            decimal number => ((double)number).ToString("R", CultureInfo.InvariantCulture),
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            float number => number.ToString("R", CultureInfo.InvariantCulture),
            IFormattable number when value is sbyte or byte or short or ushort or int or uint
                or long or ulong => number.ToString(null, CultureInfo.InvariantCulture),
            _ => throw new BadLinqExpressionException(
                $"An index predicate cannot compare against a value of type '{value.GetType().Name}'. "
                + "It has to be written into the index's DDL rather than bound as a parameter, and "
                + "Fisher only renders the types it can render unambiguously — the numbers, bool, "
                + "string, char, Guid and the timestamps. Duplicate the member and index the column "
                + "instead, or express the predicate over a member Fisher can render.")
        };

    private static string Quote(string text) => $"'{text.Replace("'", "''")}'";

    // Everything below is on the neutral contract for building a real command and has no meaning when
    // the destination is a CREATE INDEX. Refused rather than silently ignored: a fragment reaching one
    // of these would otherwise render an index predicate missing the term it was asked for, which is
    // a wrong index rather than a missing one.
    public DbParameter[] AppendWithDbParameters(string text) => throw NotForDdl();

    public DbParameter[] AppendWithDbParameters(string text, char placeholder) => throw NotForDdl();

    public void StartNewCommand() => throw NotForDdl();

    public void AddParameters(object parameters) => throw NotForDdl();

    public void AddParameters(IDictionary<string, object?> parameters) => throw NotForDdl();

    public void AddParameters<T>(IDictionary<string, T> parameters) => throw NotForDdl();

    private static BadLinqExpressionException NotForDdl()
        => new("This predicate cannot be rendered into an index definition. A partial index's WHERE "
               + "clause is DDL, so it carries no parameters and no second command — MatchesSql and "
               + "the raw-SQL paths are not available in one. Use a predicate built from the "
               + "document's own members.");
}
