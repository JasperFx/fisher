namespace Fisher.Exceptions;

/// <summary>
///     Thrown when appending to a stream that does not exist, from an append that had to read the
///     stream's current version first — <c>AppendOptimistic</c> and <c>AppendExclusive</c>.
/// </summary>
/// <remarks>
///     <para>
///         A plain <c>Append</c> does not throw this: it starts a missing stream at
///         <c>SaveChangesAsync</c>, which is the remedy the message names
///         (<c>refusal_messages.a_plain_append_starts_a_missing_stream_as_the_remedy_says</c>). A
///         non-zero expected version is the exception, and is refused rather than recreated
///         (fisher#378).
///     </para>
///     <para>
///         Subclasses the shared <see cref="JasperFx.Events.NonExistentStreamException" /> for the
///         reason its sibling
///         <see cref="ExistingStreamIdCollisionException" /> does, and takes the base's canonical
///         message (fisher#399, jasperfx#872), which names the remedy: call <c>StartStream</c> first,
///         or use a plain <c>Append</c>, which starts a missing stream.
///     </para>
/// </remarks>
public class NonExistentStreamException : JasperFx.Events.NonExistentStreamException
{
    public NonExistentStreamException(object id)
        : base(id)
    {
    }
}
