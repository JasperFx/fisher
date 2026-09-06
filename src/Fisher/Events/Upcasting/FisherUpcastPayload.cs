using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Fisher.Serialization;
using JasperFx.Events.Upcasting;

namespace Fisher.Events.Upcasting;

/// <summary>
///     Fisher's half of the shared upcasting contract (jasperfx#752): one stored event body, presented
///     to an <see cref="UpcastTransformation" /> without saying how Fisher read it.
/// </summary>
/// <remarks>
///     <para>
///         <b>A per-row struct, deliberately.</b> The contract says an implementation is short-lived
///         and that a transformation calls exactly one accessor exactly once per event, so there is
///         nothing to cache and no state to share. Making it a readonly struct keeps the ordinary read
///         path — the one where no transformation is registered and this is never constructed —
///         allocation-identical to what it was, and costs one boxing conversion on the rows that
///         actually upcast.
///     </para>
///     <para>
///         <b>Fisher is System.Text.Json-only, which is what makes the raw-JSON half unconditional
///         here.</b> The contract permits a store whose serializer cannot produce a
///         <see cref="JsonDocument" /> to refuse; Marten has to, because its serializer is
///         configurable. Fisher's <c>data</c> column is the literal text System.Text.Json wrote, so
///         <see cref="AsJsonDocument" /> is a parse of a string already in hand.
///     </para>
///     <para>
///         <b>The async accessors do not stream, and that is honest rather than lazy.</b> Fisher's
///         reads have already materialized the row's body into a <c>string</c> by the time hydration
///         runs — the row reader takes it off <c>DbDataReader.GetString</c> — so an "async" accessor
///         has nothing left to await. The contract offers the pair for stores that can stream; saying
///         so is better than wrapping the sync path in a <c>Task.Run</c> to look asynchronous.
///     </para>
///     <para>
///         <b>A binary body (fisher#93) is served through its own serializer, and refuses the JSON
///         accessor.</b> A binary row's <c>data</c> column holds only <c>EventsTable.JsonPlaceholder</c>,
///         so handing that to a raw-JSON transformation would upcast <c>{}</c> — an event with every
///         member at its default, silently. The refusal names the column and the setting, which is the
///         same discipline the binary read path already follows.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification =
        "Class-level: deserializes the stored body through the configured ISerializer. The old event type flows in from the registered UpcastTransformation, which the consumer preserves.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification =
        "Class-level: ISerializer.FromJson is annotated RDC. AOT consumers supply a source-generator-backed ISerializer.")]
internal readonly struct FisherUpcastPayload : IUpcastPayload
{
    private readonly EventGraph _events;
    private readonly ISerializer _serializer;
    private readonly string _json;
    private readonly byte[]? _binary;
    private readonly string _eventTypeName;

    internal FisherUpcastPayload(EventGraph events, ISerializer serializer, string json, byte[]? binary,
        string eventTypeName)
    {
        _events = events;
        _serializer = serializer;
        _json = json;
        _binary = binary;
        _eventTypeName = eventTypeName;
    }

    /// <summary>
    ///     Deserialize the stored body as the transformation's old CLR type.
    /// </summary>
    public T As<T>() where T : notnull
    {
        if (_binary is null)
        {
            return _serializer.FromJson<T>(_json);
        }

        // The old type is only known here, which is why the binary serializer is resolved at this
        // point rather than carried in: the row reader knows the row is binary, the transformation
        // knows what it is a body of, and neither knows both.
        var mapping = _events.EventMappingFor(typeof(T));

        if (mapping.BinarySerializer is not { } serializer)
        {
            throw new UpcastingException(
                $"The event at type name '{_eventTypeName}' has a BLOB body in data_binary, and no "
                + $"IEventBinarySerializer is registered for the upcast source type '{typeof(T).FullName}'. "
                + "Register one with StoreOptions.Events.UseBinarySerializer<T>(...), or set "
                + "StoreOptions.Events.DefaultBinarySerializer.");
        }

        return (T)serializer.Deserialize(typeof(T), _binary);
    }

    /// <inheritdoc cref="As{T}" />
    public ValueTask<T> AsAsync<T>(CancellationToken token) where T : notnull => new(As<T>());

    /// <summary>
    ///     The stored body as raw JSON, for a transformation that keeps no old CLR type.
    /// </summary>
    public JsonDocument AsJsonDocument()
    {
        if (_binary is not null)
        {
            throw new UpcastingException(
                $"The event at type name '{_eventTypeName}' has a BLOB body in data_binary, so it has "
                + "no JSON to transform — its data column holds only the placeholder. Register a typed "
                + "upcast over the old event type instead, which reads the body through its own "
                + "IEventBinarySerializer.");
        }

        return JsonDocument.Parse(_json);
    }

    /// <inheritdoc cref="AsJsonDocument" />
    public ValueTask<JsonDocument> AsJsonDocumentAsync(CancellationToken token) => new(AsJsonDocument());
}
