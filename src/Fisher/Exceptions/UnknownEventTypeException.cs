using JasperFx.Events.Daemon;

namespace Fisher.Exceptions;

/// <summary>
///     An event's persisted <c>dotnet_type</c> resolves to no type this deployment knows.
/// </summary>
/// <remarks>
///     <para>
///         Implements <see cref="IEventFailureContext" />, which is how the daemon classifies a shard
///         failure and names the offending event without knowing Fisher's concrete exception types.
///         JasperFx owns only <c>ApplyEventException</c>; the read-side failures belong to the store,
///         because that is where rows are read and deserialized. Marten and Polecat each carry their
///         own equivalent.
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
public class UnknownEventTypeException : Exception, IEventFailureContext
{
    /// <summary>
    ///     Reported when the throw site had no <c>fi_events</c> row in hand.
    ///     <see cref="IEventFailureContext.Sequence" /> is non-nullable by contract, so a sentinel is
    ///     unavoidable.
    /// </summary>
    public const long UnknownSequence = -1;

    public UnknownEventTypeException(string? eventTypeName) : this(eventTypeName, UnknownSequence)
    {
    }

    public UnknownEventTypeException(string? eventTypeName, long sequence)
        : base($"Unknown event type '{eventTypeName}' at sequence {sequence}. Register it through "
               + "StoreOptions.Events.AddEventType(type), or configure the projection to skip unknown events.")
    {
        EventTypeName = eventTypeName;
        Sequence = sequence;
    }

    public ShardFailureCategory Category => ShardFailureCategory.UnknownEventType;

    /// <summary>
    ///     The offending row's <c>seq_id</c>, or <see cref="UnknownSequence" />.
    /// </summary>
    public long Sequence { get; }

    /// <summary>
    ///     The unresolvable type name as stored.
    /// </summary>
    public string? EventTypeName { get; }

    // The type never resolved, so no event was materialized to read these from.
    Guid? IEventFailureContext.EventId => null;
    Guid? IEventFailureContext.StreamId => null;
    string? IEventFailureContext.StreamKey => null;
    string? IEventFailureContext.TenantId => null;
    long? IEventFailureContext.Version => null;
}
