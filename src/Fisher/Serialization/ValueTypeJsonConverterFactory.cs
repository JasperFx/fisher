using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Core.Reflection;

namespace Fisher.Serialization;

/// <summary>
///     Serializes every wrapper registered with <c>StoreOptions.RegisterValueType</c> as the primitive
///     it wraps rather than as an object (fisher#356).
/// </summary>
/// <remarks>
///     <para>
///         Without it System.Text.Json writes <c>readonly record struct OrderId(Guid Value)</c> as
///         <c>{"value":"…"}</c>. That round-trips perfectly and is invisible until something reads the
///         JSON rather than the object, which is exactly what LINQ does: <c>json_extract(data, '$.orderId')</c>
///         returns the object's text, so a <c>Where</c> compares a wrapper against a JSON object and a
///         <c>Select</c> hands one to the materializer. Written as its primitive, the member is a plain
///         value to SQLite and every locator, index and duplicated column treats it as one.
///     </para>
///     <para>
///         <b>Registered wrappers only, never discovered ones.</b> Discovery answers "does this type
///         have the shape of a wrapper?", and plenty of ordinary single-property types do. Changing how
///         those serialize because they happened to fit would rewrite application JSON nobody asked to
///         change. Registering is the assertion that the type IS an identifier.
///     </para>
///     <para>
///         <b>Reading accepts both shapes.</b> Rows written before the wrapper was registered, or by an
///         earlier Fisher, hold the object form. Reading that still works, so registering a type on a
///         live store needs no migration. What does not change until a row is rewritten is what LINQ
///         sees in it, which is why the docs say to register before data is written.
///     </para>
///     <para>
///         <b>A type carrying its own <see cref="JsonConverterAttribute" /> is left alone.</b> A
///         converter in <c>JsonSerializerOptions.Converters</c> outranks a type-level attribute in
///         System.Text.Json, so claiming such a type would silently override the application's
///         converter — Vogen's and StronglyTypedId's generated ones included, which already write the
///         primitive.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2055:MakeGenericType",
    Justification = "Closes the converter over a wrapper type the application registered by name. Registered value types are preserved by the RegisterValueType call on the caller side.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "See the trimming justification above.")]
[UnconditionalSuppressMessage("Trimming", "IL2067",
    Justification = "See the trimming justification above.")]
[UnconditionalSuppressMessage("Trimming", "IL2070",
    Justification = "See the trimming justification above.")]
[UnconditionalSuppressMessage("Trimming", "IL2072",
    Justification = "See the trimming justification above.")]
internal sealed class ValueTypeJsonConverterFactory : JsonConverterFactory
{
    private readonly IReadOnlyDictionary<Type, ValueTypeInfo> _valueTypes;
    private readonly IReadOnlyDictionary<Type, Func<JsonConverter>> _converters;

    public ValueTypeJsonConverterFactory(IReadOnlyDictionary<Type, ValueTypeInfo> valueTypes,
        IReadOnlyDictionary<Type, Func<JsonConverter>>? converters = null)
    {
        _valueTypes = valueTypes;
        _converters = converters ?? new Dictionary<Type, Func<JsonConverter>>();
    }

    public override bool CanConvert(Type typeToConvert)
        => _valueTypes.ContainsKey(typeToConvert)
           && !typeToConvert.IsDefined(typeof(JsonConverterAttribute), inherit: false);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        // Closed statically by RegisterValueType<T>(), which is what works in a Native AOT image
        // (fisher#386). A type registered by Type falls through to the reflective close below.
        if (_converters.TryGetValue(typeToConvert, out var build))
        {
            return build();
        }

        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new NotSupportedException(
                $"Fisher cannot serialize '{typeToConvert.FullName}' as its primitive value in a Native AOT " +
                $"image, because it was registered with RegisterValueType(Type). Register it with " +
                $"RegisterValueType<{typeToConvert.Name}>() instead. See fisher#386.");
        }

        var info = _valueTypes[typeToConvert];
        var converterType = typeof(ValueTypeJsonConverter<>).MakeGenericType(typeToConvert);

        return (JsonConverter)Activator.CreateInstance(converterType, info)!;
    }
}

internal sealed class ValueTypeJsonConverter<TOuter> : JsonConverter<TOuter>
{
    private readonly ValueTypeInfo _info;
    private readonly Func<TOuter, object?> _unwrap;

    public ValueTypeJsonConverter(ValueTypeInfo info)
    {
        _info = info;
        var property = info.ValueProperty;
        _unwrap = outer => property.GetValue(outer);
    }

    public override TOuter? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return default;
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            return ReadObjectForm(ref reader, options);
        }

        return Wrap(ReadInner(ref reader));
    }

    /// <summary>
    ///     The shape System.Text.Json wrote before the wrapper was registered: <c>{"value": …}</c>, keyed
    ///     by the wrapped property under whatever naming policy was in force then.
    /// </summary>
    private TOuter? ReadObjectForm(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        object? inner = null;
        var found = false;
        var name = _info.ValueProperty.Name;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var key = reader.GetString();
            reader.Read();

            if (!found && key is not null && string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                inner = reader.TokenType == JsonTokenType.Null ? null : ReadInner(ref reader);
                found = true;
            }
            else
            {
                reader.Skip();
            }
        }

        return found && inner is not null ? Wrap(inner) : default;
    }

    private object ReadInner(ref Utf8JsonReader reader)
    {
        var simple = _info.SimpleType;

        if (simple == typeof(string)) return reader.GetString()!;
        if (simple == typeof(Guid)) return reader.GetGuid();

        if (simple == typeof(int))
        {
            return reader.TokenType == JsonTokenType.String
                ? int.Parse(reader.GetString()!, CultureInfo.InvariantCulture)
                : reader.GetInt32();
        }

        if (simple == typeof(long))
        {
            return reader.TokenType == JsonTokenType.String
                ? long.Parse(reader.GetString()!, CultureInfo.InvariantCulture)
                : reader.GetInt64();
        }

        throw new JsonException($"{typeof(TOuter).FullName} wraps {simple.FullName}, which Fisher cannot store.");
    }

    public override void Write(Utf8JsonWriter writer, TOuter value, JsonSerializerOptions options)
    {
        switch (_unwrap(value))
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case Guid g:
                writer.WriteStringValue(g);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case var other:
                throw new JsonException(
                    $"{typeof(TOuter).FullName} wraps {other.GetType().FullName}, which Fisher cannot store.");
        }
    }

    // A wrapper as a dictionary key: the same primitive, as a property name.
    public override TOuter ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        var text = reader.GetString()!;
        var simple = _info.SimpleType;

        object inner = simple == typeof(string) ? text
            : simple == typeof(Guid) ? Guid.Parse(text)
            : simple == typeof(int) ? int.Parse(text, CultureInfo.InvariantCulture)
            : long.Parse(text, CultureInfo.InvariantCulture);

        return Wrap(inner)!;
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, TOuter value, JsonSerializerOptions options)
    {
        var inner = _unwrap(value);
        writer.WritePropertyName(inner switch
        {
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => inner?.ToString() ?? string.Empty
        });
    }

    private TOuter? Wrap(object inner) => (TOuter)Storage.StrongTypedId.Wrap(_info, inner);
}
