using System.Text.Json;

namespace Fisher.Linq.Members;

/// <summary>
///     A <see cref="DateOnly" /> / <see cref="TimeOnly" /> document member.
/// </summary>
/// <remarks>
///     <para>
///         Unlike a timestamp, these two are already sortable in the form System.Text.Json writes them,
///         so they need no normalisation — which is why they are not
///         <see cref="TimestampMember" />. A <c>DateOnly</c> is a fixed-width <c>yyyy-MM-dd</c> with no
///         offset to preserve and no fractional part to trim. A <c>TimeOnly</c> is a fixed-width
///         <c>HH:mm:ss</c> whose optional fractional part is a strict suffix, so trailing-zero trimming
///         shortens the string without ever changing which of two values compares smaller.
///     </para>
///     <para>
///         There is consequently no locator wrapping here: <see cref="TypedLocator" /> and
///         <see cref="RawLocator" /> are the same bare <c>json_extract</c>, as they are for every member
///         whose stored form needs no help.
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
