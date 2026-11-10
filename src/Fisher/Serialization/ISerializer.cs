using System.Diagnostics.CodeAnalysis;

namespace Fisher.Serialization;

/// <summary>
///     Serialization abstraction for Fisher. Uses System.Text.Json exclusively. Extends the shared
///     <see cref="Weasel.Core.ISerializer" /> (the intersection both Critter Stack document stores rely
///     on) with Fisher's string-based deserialization overloads.
/// </summary>
/// <remarks>
///     The default <see cref="Serializer" /> implementation routes through
///     <c>System.Text.Json.JsonSerializer</c>, which the .NET trimmer cannot statically analyse.
///     AOT-publishing apps should plug in a custom <c>ISerializer</c> implementation backed by an STJ
///     source-generator context (<c>JsonSerializerContext</c>) rather than the reflection-based default.
///     The shared base is intentionally unannotated (for Marten parity); the concrete
///     <see cref="Serializer" /> carries its own trim/AOT suppressions.
/// </remarks>
public interface ISerializer : Weasel.Core.ISerializer
{
    /// <summary>
    ///     Deserialize a JSON string to an object of type T.
    /// </summary>
    [RequiresUnreferencedCode("Default ISerializer uses STJ reflection; AOT consumers should supply a source-generator-backed ISerializer impl.")]
    [RequiresDynamicCode("Default ISerializer uses STJ reflection which requires runtime code generation.")]
    T FromJson<T>(string json);

    /// <summary>
    ///     Deserialize a JSON string to an object of the specified type.
    /// </summary>
    [RequiresUnreferencedCode("Default ISerializer uses STJ reflection; AOT consumers should supply a source-generator-backed ISerializer impl.")]
    [RequiresDynamicCode("Default ISerializer uses STJ reflection which requires runtime code generation.")]
    object FromJson(Type type, string json);

    /// <summary>
    ///     Deserialize UTF-8 JSON held in a span, without first materializing a string (fisher#461).
    /// </summary>
    /// <remarks>
    ///     Default-implemented through the stream overload so existing implementations keep compiling; a
    ///     serializer that can parse a span directly should override it.
    /// </remarks>
    [RequiresUnreferencedCode("Default ISerializer uses STJ reflection; AOT consumers should supply a source-generator-backed ISerializer impl.")]
    [RequiresDynamicCode("Default ISerializer uses STJ reflection which requires runtime code generation.")]
    T FromJson<T>(ReadOnlySpan<byte> utf8Json)
    {
        using var stream = new MemoryStream(utf8Json.ToArray(), writable: false);
        return FromJson<T>(stream);
    }

    /// <summary>
    ///     Deserialize UTF-8 JSON held in a span to the specified type.
    /// </summary>
    [RequiresUnreferencedCode("Default ISerializer uses STJ reflection; AOT consumers should supply a source-generator-backed ISerializer impl.")]
    [RequiresDynamicCode("Default ISerializer uses STJ reflection which requires runtime code generation.")]
    object FromJson(Type type, ReadOnlySpan<byte> utf8Json)
    {
        using var stream = new MemoryStream(utf8Json.ToArray(), writable: false);
        return FromJson(type, stream);
    }
}
