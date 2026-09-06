using System.Globalization;

namespace Fisher.Linq.SqlGeneration;

/// <summary>
///     The <c>snippet()</c> and <c>highlight()</c> projection locators — fisher#220.
/// </summary>
/// <remarks>
///     <para>
///         Locator strings for the same reason <see cref="FullTextRank" /> is one: a projected column
///         is a locator in <see cref="Statement" />'s select list, so building these as SQL text is
///         what lets them travel through the projection machinery without it learning about full text.
///     </para>
///     <para>
///         <b>Markers are rendered as SQL literals, not bound.</b> Every other caller value in a Fisher
///         query is a parameter, so the exception needs its reason: these sit in the SELECT list, which
///         is assembled as a locator string rather than as a fragment that could carry parameters. They
///         are therefore escaped the only way a SQLite string literal can be — by doubling every single
///         quote — which is complete for this grammar rather than a filter that hopes to catch the
///         dangerous cases.
///     </para>
///     <para>
///         The column index is never a caller string: it is resolved from the index's own declared
///         columns, so an unknown name is refused before anything is rendered.
///     </para>
/// </remarks>
internal static class FullTextExtract
{
    /// <summary>FTS5's "pick the best-matching column".</summary>
    public const int BestColumn = -1;

    public static string Snippet(string quotedTable, int column, string startMarker, string endMarker,
        string ellipsis, int maxTokens)
        => $"snippet({quotedTable}, {column.ToString(CultureInfo.InvariantCulture)}, "
           + $"{Literal(startMarker)}, {Literal(endMarker)}, {Literal(ellipsis)}, "
           + $"{maxTokens.ToString(CultureInfo.InvariantCulture)})";

    public static string Highlight(string quotedTable, int column, string startMarker, string endMarker)
        => $"highlight({quotedTable}, {column.ToString(CultureInfo.InvariantCulture)}, "
           + $"{Literal(startMarker)}, {Literal(endMarker)})";

    private static string Literal(string value)
        => "'" + value.Replace("'", "''") + "'";
}
