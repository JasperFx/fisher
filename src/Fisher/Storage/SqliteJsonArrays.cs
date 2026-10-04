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
}
