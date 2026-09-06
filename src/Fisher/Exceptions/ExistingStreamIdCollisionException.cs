namespace Fisher.Exceptions;

/// <summary>
///     Thrown when starting a stream with an id that already exists.
/// </summary>
/// <remarks>
///     <para>
///         <b>Subclasses the shared <see cref="JasperFx.Events.ExistingStreamIdCollisionException" /></b>
///         (jasperfx#751 / #756, in JasperFx.Events 2.64.0), which was lifted from the three stores'
///         identically-shaped copies. Subclassing rather than deleting is the compatible choice: an
///         existing <c>catch (Fisher.Exceptions.ExistingStreamIdCollisionException)</c> keeps working
///         and a <c>catch</c> on the shared type starts working, which is what
///         <c>ProjectionSideEffectCompliance</c> requires. A literal <c>TypeForwardedTo</c> is not
///         available — forwarding needs the same fully qualified name and the namespaces differ.
///     </para>
///     <para>
///         The message is already the canonical wording, so the base's two-argument constructor would
///         produce the same text; it goes through the message-overriding one anyway so that a later
///         change to the canonical wording cannot silently move Fisher's.
///     </para>
///     <para>
///         The shared type's <c>AggregateType</c> is Marten's addition and stays null here: Fisher
///         raises this by translating a SQLite primary key violation, where the aggregate type is not
///         in hand.
///     </para>
/// </remarks>
public class ExistingStreamIdCollisionException : JasperFx.Events.ExistingStreamIdCollisionException
{
    public ExistingStreamIdCollisionException(object id)
        : base($"Stream with id '{id}' already exists.", id, null)
    {
    }
}
