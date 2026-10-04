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
///         <b>The message is the base's canonical wording, remedy included</b> (fisher#399,
///         jasperfx#872): <c>StartStream</c> needs a new id, so use <c>Append</c> or
///         <c>FetchForWriting</c> for an existing stream. Fisher used to pass its own text through the
///         message-overriding constructor, so that a later change to the canonical wording could not
///         silently move Fisher's. That worked as designed and was the problem: when the remedy was
///         added upstream, Fisher kept the old text without it. Fisher's semantics match the remedy, so
///         adopting it costs nothing.
///     </para>
///     <para>
///         <b>One Fisher-specific case the message does not name: <c>StartStream</c> over an
///         ARCHIVED id raises this, not <see cref="ArchivedStreamException" />.</b> An archived id is
///         still an id in use, and "that id is taken" is the more useful answer to a start than "that
///         stream is archived".
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
        : base(id, null)
    {
    }
}
