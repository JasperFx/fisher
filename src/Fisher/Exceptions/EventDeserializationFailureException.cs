namespace Fisher.Exceptions;

/// <summary>
///     An event body in <c>fi_events</c> could not be deserialized or upcast into its event type
///     (fisher#308).
/// </summary>
/// <remarks>
///     <para>
///         <b>Subclasses the shared <see cref="JasperFx.Events.EventDeserializationFailureException" /></b>,
///         the same way <see cref="UnknownEventTypeException" /> and
///         <see cref="ExistingStreamIdCollisionException" /> do, and for the same reason: an existing
///         <c>catch</c> on Fisher's type keeps working and a <c>catch</c> on the shared one starts
///         working. Everything but the message wording is the base's — the sequence, the stored type
///         alias, <see cref="JasperFx.Events.Daemon.IEventFailureContext" /> and the
///         <c>ToDeadLetterEvent</c> helper.
///     </para>
///     <para>
///         <b>Raising it at all is the point, not the wording.</b> Fisher raised nothing of the kind,
///         so the async daemon's <c>SkipSerializationErrors</c> policy had nothing to catch: a single
///         unreadable body escaped the loader as a bare <c>JsonException</c>, was wrapped in
///         <c>EventLoaderException</c> after the resilient loader's retries, and paused the shard as
///         <see cref="JasperFx.Events.Daemon.ShardFailureCategory.Other" /> with no dead letter. The
///         flag defaults to true, so it was silently a no-op. The base's
///         <see cref="JasperFx.Events.Daemon.ShardFailureCategory.EventSerialization" /> is what
///         classifies the pause when the policy is off, and the daemon never sniffs a store's
///         exception type names to get there.
///     </para>
///     <para>
///         <b>Deliberately distinct from <see cref="UnknownEventTypeException" /></b>, which that
///         type's own remarks already draw: an alias resolving to nothing is normally a missing
///         registration or a rolled-back deployment — a deployment fix — where a body that will not
///         parse is a data or serializer problem. An operator responds to the two differently, which
///         is why they are governed by two separate policies.
///     </para>
///     <para>
///         <b>Raised from the row reader, so every read path shares it</b> — the daemon's loader, the
///         stream reads, live aggregation and the DCB tag queries all converge on
///         <c>FisherEventsRowReader</c>. Marten does the same thing in one place in
///         <c>EventDocumentStorage</c>. Only the loader acts on the policy; everywhere else the
///         improvement is simply that the exception names the sequence and the event type instead of
///         being whatever the serializer threw.
///     </para>
/// </remarks>
public class EventDeserializationFailureException : JasperFx.Events.EventDeserializationFailureException
{
    public EventDeserializationFailureException(long sequence, string? eventTypeName, Exception innerException)
        : base($"Could not deserialize the body of event '{eventTypeName}' at sequence {sequence}. "
               + "The stored JSON or binary body does not match the event type this process resolved "
               + "for it — check for a changed event schema (StoreOptions.Events.Upcasters is how an "
               + "old schema is reinterpreted), or set Projections.Errors.SkipSerializationErrors to "
               + "quarantine it as a dead letter instead of stopping the shard.",
            sequence, eventTypeName, innerException)
    {
    }
}
