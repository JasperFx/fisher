using System.Globalization;

namespace Fisher.Linq.Members;

/// <summary>
///     A <see cref="DateTime" /> / <see cref="DateTimeOffset" /> document member, compared through
///     SQLite's own date parser rather than against the raw JSON text.
/// </summary>
/// <remarks>
///     <para>
///         The text System.Text.Json writes for a timestamp is <em>not</em> order-preserving, for two
///         independent reasons: trailing fractional zeros are trimmed, so the same instant may be
///         written <c>12:34:56</c> or <c>12:34:56.000</c>; and the original UTC offset is kept rather
///         than normalised, so <c>12:34:56-05:00</c> sorts before <c>12:34:56.789+00:00</c> while being
///         five hours later. Comparing that text directly answers ordering questions wrongly, which is
///         why Fisher refused them outright until fisher#1.
///     </para>
///     <para>
///         <strong>The fix is to normalise inline, not to duplicate the column.</strong>
///         <c>strftime('%Y-%m-%dT%H:%M:%f', json_extract(data,'$.when'))</c> hands the stored text to
///         SQLite's date parser, which understands the trailing offset, converts to UTC and renders a
///         fixed-width millisecond form — so both problems disappear and the result sorts as text
///         because it is fixed-width UTC, exactly like <see cref="Storage.SqliteTimestamp" />'s event
///         columns. Verified against SQLite 3.51; an unparseable value yields NULL, which compares as
///         no-match rather than as a wrong match.
///     </para>
///     <para>
///         Polecat instead writes <c>CAST(JSON_VALUE(...) AS datetimeoffset)</c> and Marten casts to
///         <c>timestamptz</c>. This is the same move — hand the text to the engine's date type — spelled
///         the way SQLite spells it.
///     </para>
///     <para>
///         <strong>Equality goes through the same normalisation as ordering</strong>, rather than
///         staying on the exact serializer rendering. Two spellings of one instant must not be equal for
///         <c>&gt;=</c> and unequal for <c>==</c>. The cost is that <c>==</c> now discriminates only to
///         the millisecond, since <c>%f</c> has no sub-millisecond form — the siblings truncate too
///         (<c>timestamptz</c> is microsecond precision, so neither store compares a
///         <see cref="DateTimeOffset" /> tick for tick).
///     </para>
///     <para>
///         <see cref="RawLocator" /> stays the bare <c>json_extract</c>, because a null test asks
///         whether the member is present, not whether it parses as a date.
///     </para>
///     <para>
///         Indexing this is a separate concern: <c>strftime</c> over <c>json_extract</c> is computed per
///         row, so a large collection wants the duplicated, indexable column fisher#2 tracks. Correct
///         first, fast second.
///     </para>
/// </remarks>
internal class TimestampMember : IQueryableMember
{
    /// <summary>
    ///     What <c>strftime('%Y-%m-%dT%H:%M:%f', …)</c> renders — note <c>%f</c> is
    ///     <c>SS.SSS</c>, seconds included, so the format has no separate seconds specifier.
    /// </summary>
    internal const string SqliteFormat = "%Y-%m-%dT%H:%M:%f";

    /// <summary>The .NET format that reproduces <see cref="SqliteFormat" /> exactly.</summary>
    internal const string ClrFormat = "yyyy-MM-ddTHH:mm:ss.fff";

    public TimestampMember(string locator, Type memberType)
    {
        RawLocator = locator;
        TypedLocator = $"strftime('{SqliteFormat}', {locator})";
        MemberType = memberType;
    }

    public Type MemberType { get; }
    public string TypedLocator { get; }
    public string RawLocator { get; }
    public bool IsBoolean => false;

    /// <summary>
    ///     Render the comparison value the way <see cref="TypedLocator" /> renders the stored one:
    ///     UTC, fixed width, milliseconds.
    /// </summary>
    /// <remarks>
    ///     A <see cref="DateTime" /> whose <see cref="DateTime.Kind" /> is
    ///     <see cref="DateTimeKind.Unspecified" /> is left where it is rather than being shifted, because
    ///     System.Text.Json writes it with no offset and SQLite reads an offsetless string as already
    ///     UTC — converting it here would move the literal off the values it is meant to match.
    /// </remarks>
    public object? ConvertValue(object? value)
        => value switch
        {
            null => null,
            DateTimeOffset timestamp => timestamp.UtcDateTime.ToString(ClrFormat, CultureInfo.InvariantCulture),
            DateTime { Kind: DateTimeKind.Unspecified } timestamp
                => timestamp.ToString(ClrFormat, CultureInfo.InvariantCulture),
            DateTime timestamp => timestamp.ToUniversalTime().ToString(ClrFormat, CultureInfo.InvariantCulture),
            _ => value
        };
}
