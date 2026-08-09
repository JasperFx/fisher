namespace Fisher.Events;

/// <summary>
///     Store this event type's body as a binary blob rather than as JSON text (fisher#43).
/// </summary>
/// <remarks>
///     <para>
///         <b>Worth more on SQLite than the same feature is on Marten.</b> Fisher is embedded, so the
///         store's disk footprint <em>is</em> the application's — there is no server absorbing it. And
///         SQLite has no <c>jsonb</c>: where PostgreSQL keeps a compact binary form for free, Fisher
///         stores the literal JSON text of every event forever, and for a high-volume stream of small
///         events the property names dominate the payload.
///     </para>
///     <para>
///         <b>The trade is per event type, which is why this is an attribute rather than a store-wide
///         switch.</b> A binary body is not readable by <c>json_extract</c>, so everything that reads
///         <em>into</em> a body loses it for this type: <c>QueryEventDataAsync&lt;T&gt;</c> (fisher#41)
///         refuses it by name, and any hand-written SQL an operator runs against the file sees a BLOB.
///         What is unaffected is everything that reads the row's <em>columns</em> — stream reads, the
///         daemon's loader, DCB tag queries, <c>QueryEventsAsync</c>'s metadata filters — because none
///         of those looks inside the body.
///     </para>
///     <para>
///         Requires <see cref="EventStoreOptions.BinarySerializer" /> to be set <b>before the schema is
///         created</b>: it is what adds the <c>data_binary</c> column and makes <c>data</c> nullable,
///         which is a schema decision, not a runtime one. Appending a binary event to a store that was
///         created without it is refused by name rather than failing on a NOT NULL constraint.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface,
    Inherited = false)]
public sealed class BinaryEventAttribute : Attribute
{
}

/// <summary>
///     Turns an event body into bytes and back, for event types marked
///     <see cref="BinaryEventAttribute" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>Fisher ships no implementation, and that is the end state rather than a gap</b> — the
///         same position <c>IMessageOutbox</c> holds. A binary encoding is a choice with real
///         consequences for schema evolution (MessagePack, protobuf and a compressed-JSON blob fail
///         differently when an event type gains a member), and picking one for the application would
///         be Fisher deciding how its data ages. The seam is here; the encoding is yours.
///     </para>
///     <para>
///         An implementation must round-trip a body it wrote earlier for the same type, including after
///         the type has gained members — which is the whole of what an event store asks of a
///         serializer, and the whole of what makes this choice consequential.
///     </para>
/// </remarks>
public interface IEventBinarySerializer
{
    /// <summary>Encode an event body.</summary>
    byte[] Serialize(object eventBody, Type eventType);

    /// <summary>Decode an event body written by <see cref="Serialize" />.</summary>
    object Deserialize(byte[] data, Type eventType);
}
