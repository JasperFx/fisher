using System.Buffers;
using System.Collections;
using System.Text;
using System.Text.Json;

namespace Fisher.Storage;

/// <summary>
///     A list of id values written as the JSON array a <c>json_each(?)</c> parameter expects.
/// </summary>
/// <remarks>
///     <para>
///         <b><see cref="Utf8JsonWriter" />, never <c>JsonSerializer</c></b>, for Native AOT
///         (fisher#385, fisher#398). The serializer's generic overloads are reflection-based and carry
///         <c>RequiresDynamicCode</c>; the append path called one with no options, so in a native image
///         every event append threw "reflection-based serialization has been disabled". The shape here
///         is fixed — an array of scalars — so there is nothing for reflection to discover.
///     </para>
///     <para>
///         <b>A Guid is written as its lowercase canonical text</b>, the form every Fisher column holds;
///         <see cref="Guid.ToString()" /> is that form. Binding one any other way matches nothing,
///         silently — the recurring trap.
///     </para>
///     <para>
///         Self-contained, with no Fisher types in its signature, so it can move to Weasel.Sqlite as is.
///     </para>
/// </remarks>
internal static class SqliteJsonArrays
{
    internal static string Write(IEnumerable values)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();

            foreach (var value in values)
            {
                switch (value)
                {
                    case Guid guid:
                        writer.WriteStringValue(guid.ToString());
                        break;
                    case string text:
                        writer.WriteStringValue(text);
                        break;
                    case int number:
                        writer.WriteNumberValue(number);
                        break;
                    case long number:
                        writer.WriteNumberValue(number);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(values),
                            $"Unsupported id value type {value?.GetType().FullName ?? "null"}; expected Guid, string, int, or long.");
                }
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
    /// <summary>
    ///     A list of already-converted comparison values as a JSON array whose <c>json_each</c> values are
    ///     exactly what binding each value as its own parameter would compare (fisher#372) — or false when
    ///     some value has no JSON form that does.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>json_each</c> yields a JSON string as TEXT, an integer as INTEGER, any other number as
    ///         REAL, <c>true</c>/<c>false</c> as INTEGER 1/0 and <c>null</c> as NULL — the same storage
    ///         classes Microsoft.Data.Sqlite binds a <see cref="string" />, an integral type, a
    ///         <see cref="double" />/<see cref="float" />, a <see cref="bool" /> and a null as. So for those
    ///         types the two shapes compare alike, and only for those.
    ///     </para>
    ///     <para>
    ///         Anything else — a <see cref="Guid" />, a timestamp, a <see cref="decimal" /> — is declined
    ///         rather than rendered, because its JSON text is not what a bound parameter would have been. The
    ///         member conversions turn those into one of the types above before they get here anyway. A
    ///         non-finite <see cref="double" /> has no JSON form at all and is declined too.
    ///     </para>
    /// </remarks>
    internal static bool TryWriteComparisonValues(IReadOnlyList<object?> values, out string json)
    {
        json = string.Empty;
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();

            foreach (var value in values)
            {
                switch (value)
                {
                    case null:
                        writer.WriteNullValue();
                        break;
                    case string text:
                        writer.WriteStringValue(text);
                        break;
                    case bool flag:
                        writer.WriteBooleanValue(flag);
                        break;
                    case int number:
                        writer.WriteNumberValue(number);
                        break;
                    case long number:
                        writer.WriteNumberValue(number);
                        break;
                    case short number:
                        writer.WriteNumberValue(number);
                        break;
                    case byte number:
                        writer.WriteNumberValue(number);
                        break;
                    case sbyte number:
                        writer.WriteNumberValue(number);
                        break;
                    case ushort number:
                        writer.WriteNumberValue(number);
                        break;
                    case uint number:
                        writer.WriteNumberValue(number);
                        break;
                    case double number when double.IsFinite(number):
                        writer.WriteNumberValue(number);
                        break;
                    case float number when float.IsFinite(number):
                        writer.WriteNumberValue((double)number);
                        break;
                    default:
                        return false;
                }
            }

            writer.WriteEndArray();
        }

        json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return true;
    }
}
