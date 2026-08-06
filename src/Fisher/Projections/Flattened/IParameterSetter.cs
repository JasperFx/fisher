using Fisher.Storage;
using JasperFx.Events;

namespace Fisher.Projections.Flattened;

/// <summary>
///     Pulls one parameter value out of an event for a flat table statement.
/// </summary>
internal interface IParameterSetter
{
    object? ValueFor(IEvent source);
}

/// <summary>
///     Reads a member off the event body through a compiled accessor.
/// </summary>
internal sealed class EventDataParameterSetter<TEvent, TValue> : IParameterSetter
{
    private readonly Func<TEvent, TValue> _accessor;

    public EventDataParameterSetter(Func<TEvent, TValue> accessor) => _accessor = accessor;

    public object? ValueFor(IEvent source) => FlatTableValue.ToDatabaseValue(_accessor((TEvent)source.Data));
}

/// <summary>The stream's Guid identity, for a table keyed on the stream.</summary>
internal sealed class StreamIdParameterSetter : IParameterSetter
{
    public object? ValueFor(IEvent source) => FlatTableValue.ToDatabaseValue(source.StreamId);
}

/// <summary>The stream's string identity, for a store configured <c>AsString</c>.</summary>
internal sealed class StreamKeyParameterSetter : IParameterSetter
{
    public object? ValueFor(IEvent source) => source.StreamKey;
}

/// <summary>
///     Converts a CLR value to what SQLite should actually hold.
/// </summary>
/// <remarks>
///     <para>
///         The same three conversions the rest of Fisher makes explicitly on the way in, for the same
///         reasons: <strong>a Guid must go down as lowercase canonical text</strong> or
///         Microsoft.Data.Sqlite writes a 16-byte BLOB — or, bound as text without conversion, the
///         uppercase form, which SQLite's case-sensitive default collation then never matches against
///         the lowercase rows every other Fisher write produces. A bool has no SQLite type and rides
///         an INTEGER 0/1. A timestamp is <see cref="SqliteTimestamp" />'s fixed-width UTC text.
///     </para>
///     <para>
///         Everything else is handed over untouched and takes the column's affinity, which is what
///         makes an int column readable back as an int without a cast.
///     </para>
/// </remarks>
internal static class FlatTableValue
{
    public static object? ToDatabaseValue(object? value)
        => value switch
        {
            null => null,
            Guid guid => guid.ToString(),
            bool flag => flag ? 1 : 0,
            DateTimeOffset timestamp => SqliteTimestamp.ToDatabaseValue(timestamp),
            DateTime timestamp => SqliteTimestamp.ToDatabaseValue(new DateTimeOffset(timestamp.ToUniversalTime())),
            Enum enumeration => Convert.ToInt64(enumeration, System.Globalization.CultureInfo.InvariantCulture),
            _ => value
        };
}
