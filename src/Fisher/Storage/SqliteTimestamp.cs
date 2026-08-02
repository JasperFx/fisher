using System.Globalization;

namespace Fisher.Storage;

/// <summary>
///     Fisher's single answer to "how does a timestamp get into and out of SQLite".
/// </summary>
/// <remarks>
///     <para>
///         SQLite has no date/time type. Marten leans on <c>timestamptz</c> and Polecat on
///         <c>datetimeoffset</c>; Fisher stores ISO-8601 UTC text, which is the only representation
///         that sorts correctly as a string — the property the async daemon's ordering and the
///         event-store explorer's range queries both depend on.
///     </para>
///     <para>
///         <c>CURRENT_TIMESTAMP</c> is deliberately not used: it renders as
///         <c>'YYYY-MM-DD HH:MM:SS'</c>, which has no sub-second component and does not round-trip
///         through <see cref="DateTimeOffset" /> as UTC. Events appended within the same second would
///         become indistinguishable by timestamp.
///     </para>
/// </remarks>
internal static class SqliteTimestamp
{
    /// <summary>
    ///     The round-trip format: ISO-8601, milliseconds, explicit <c>Z</c>. Matches
    ///     <see cref="NowExpression" /> exactly so server- and client-generated values are comparable
    ///     as text.
    /// </summary>
    public const string Format = "yyyy-MM-ddTHH:mm:ss.fffZ";

    /// <summary>
    ///     SQL expression producing the current UTC time in <see cref="Format" />, for use inside a
    ///     statement (an INSERT value, an UPDATE assignment).
    /// </summary>
    public const string NowExpression = "strftime('%Y-%m-%dT%H:%M:%fZ','now')";

    /// <summary>
    ///     The same expression as a column DEFAULT.
    /// </summary>
    /// <remarks>
    ///     The parentheses are required, not stylistic. SQLite's <c>column-def</c> grammar accepts a
    ///     bare literal after DEFAULT but demands any expression be parenthesized, so
    ///     <c>DEFAULT strftime(...)</c> fails at CREATE TABLE with <c>near "(": syntax error</c>.
    /// </remarks>
    public const string NowDefaultExpression = "(" + NowExpression + ")";

    /// <summary>
    ///     Render a timestamp for binding to a TEXT column.
    /// </summary>
    public static string ToDatabaseValue(DateTimeOffset value)
        => value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>
    ///     Parse a timestamp read back out of a TEXT column.
    /// </summary>
    public static DateTimeOffset FromDatabaseValue(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal |
                                                                     DateTimeStyles.AssumeUniversal);
}
