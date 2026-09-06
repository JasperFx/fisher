namespace Fisher.Exceptions;

/// <summary>
///     Thrown when appending to a stream that does not exist, from an append that had to read the
///     stream's current version first — <c>AppendOptimistic</c> and <c>AppendExclusive</c>.
/// </summary>
/// <remarks>
///     <para>
///         A plain <c>Append</c> does not throw this. It queues the events and lets the write fail at
///         <c>SaveChangesAsync</c>, because there is nothing to read up front.
///     </para>
///     <para>
///         Subclasses the shared <see cref="JasperFx.Events.NonExistentStreamException" /> for the
///         reason its sibling
///         <see cref="ExistingStreamIdCollisionException" /> does. Fisher's message ends in a full
///         stop where the canonical one does not, so it goes through the message-overriding
///         constructor rather than adopting the base's wording.
///     </para>
/// </remarks>
public class NonExistentStreamException : JasperFx.Events.NonExistentStreamException
{
    public NonExistentStreamException(object id)
        : base($"Attempt to append to a nonexistent event stream '{id}'.", id)
    {
    }
}
