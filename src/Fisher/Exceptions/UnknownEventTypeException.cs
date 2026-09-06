namespace Fisher.Exceptions;

/// <summary>
///     An event's persisted <c>dotnet_type</c> resolves to no type this deployment knows.
/// </summary>
/// <remarks>
///     <para>
///         <b>Subclasses the shared <see cref="JasperFx.Events.UnknownEventTypeException" /></b>
///         (jasperfx#751 / #756, in JasperFx.Events 2.64.0), which was lifted from the three stores'
///         copies once jasperfx#565 had given all three the same
///         <see cref="JasperFx.Events.Daemon.IEventFailureContext" /> contract. Everything this type
///         used to declare — the <c>UnknownSequence</c> sentinel, the <c>Sequence</c> and
///         <c>EventTypeName</c> properties, the failure-context implementation and the
///         <c>UnknownEventType</c> category — is now the base's; only Fisher's message wording is
///         left, and it goes through the base's message-overriding constructor.
///     </para>
///     <para>
///         Subclassing rather than deleting is the compatible choice — see
///         <see cref="ExistingStreamIdCollisionException" /> for the reasoning, which is the same for
///         all three of the lifted types.
///     </para>
///     <para>
///         Deliberately distinct from a deserialization failure. An alias that resolves to nothing is
///         normally a missing registration or a deployment rolled back past the event type's
///         introduction — a deployment fix, not a data fix — so an operator responds to it differently.
///     </para>
///     <para>
///         Note the asymmetry with Fisher's stream reads, which skip an unresolvable row
///         unconditionally so a deployment can still read events it does not understand. The daemon
///         cannot afford that: silently skipping would leave the projection permanently wrong with no
///         signal, so it throws unless <c>SkipUnknownEvents</c> says otherwise.
///     </para>
/// </remarks>
public class UnknownEventTypeException : JasperFx.Events.UnknownEventTypeException
{
    public UnknownEventTypeException(string? eventTypeName)
        : this(eventTypeName, JasperFx.Events.UnknownEventTypeException.UnknownSequence)
    {
    }

    public UnknownEventTypeException(string? eventTypeName, long sequence)
        : base($"Unknown event type '{eventTypeName}' at sequence {sequence}. Register it through "
               + "StoreOptions.Events.AddEventType(type), or configure the projection to skip unknown events.",
            eventTypeName, sequence)
    {
    }
}
