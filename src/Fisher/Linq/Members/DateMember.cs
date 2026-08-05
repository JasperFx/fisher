using System.Text.Json;

namespace Fisher.Linq.Members;

/// <summary>
///     A <see cref="DateTime" /> / <see cref="DateTimeOffset" /> document member.
/// </summary>
/// <remarks>
///     <para>
///         This type has no Polecat counterpart, and the reason is worth stating plainly. Polecat writes
///         <c>CAST(JSON_VALUE(data,'$.When') AS datetimeoffset)</c> and lets SQL Server compare real
///         timestamps. SQLite has no date type, so the comparison is against whatever text
///         System.Text.Json wrote into the document — and that text is <em>not</em> order-preserving:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 STJ trims trailing fractional zeros, so the same instant can be written
///                 <c>12:34:56</c> or <c>12:34:56.789</c> depending on its precision.
///             </description>
///         </item>
///         <item>
///             <description>
///                 The offset is preserved rather than normalised, so <c>12:34:56-05:00</c> sorts before
///                 <c>12:34:56.789+00:00</c> as text while being five hours <em>later</em> in fact.
///             </description>
///         </item>
///     </list>
///     <para>
///         Equality still works, because the literal is rendered through the very serializer that wrote
///         the document. Ordering and range comparison do not, so
///         <see cref="AllowsRangeComparison" /> is false and the parser raises
///         <see cref="BadLinqExpressionException" /> rather than emitting a predicate that returns
///         plausible-but-wrong rows. Lifting the restriction means storing a normalised, sortable
///         duplicate — the same shape Fisher will need for duplicated fields generally.
///     </para>
///     <para>
///         Note this concerns dates <em>inside document JSON</em> only. The <c>fi_events</c> and
///         <c>fi_streams</c> timestamp columns are a different thing entirely: those are written by
///         <see cref="Storage.SqliteTimestamp" /> in a fixed-width UTC format precisely so they
///         <em>do</em> sort as text.
///     </para>
/// </remarks>
internal class DateMember : IQueryableMember
{
    private readonly JsonSerializerOptions _serializerOptions;

    public DateMember(string locator, Type memberType, JsonSerializerOptions serializerOptions)
    {
        RawLocator = locator;
        TypedLocator = locator;
        MemberType = memberType;
        _serializerOptions = serializerOptions;
    }

    public Type MemberType { get; }
    public string TypedLocator { get; }
    public string RawLocator { get; }
    public bool IsBoolean => false;
    public bool AllowsRangeComparison => false;

    /// <summary>
    ///     Renders the value through the store's own serializer and strips the surrounding quotes, so
    ///     the literal is byte-for-byte what serializing the document would have written. Deriving the
    ///     format by hand instead would mean reproducing STJ's trailing-zero trimming, which no single
    ///     format string does.
    /// </summary>
    public object? ConvertValue(object? value)
    {
        if (value == null)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(value, value.GetType(), _serializerOptions);
        return json.Length >= 2 && json[0] == '"' && json[^1] == '"'
            ? json[1..^1]
            : json;
    }
}
