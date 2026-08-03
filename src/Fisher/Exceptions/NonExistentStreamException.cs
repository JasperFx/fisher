namespace Fisher.Exceptions;

/// <summary>
///     Thrown when appending to a stream that does not exist, from an append that had to read the
///     stream's current version first — <c>AppendOptimistic</c> and <c>AppendExclusive</c>.
/// </summary>
/// <remarks>
///     A plain <c>Append</c> does not throw this. It queues the events and lets the write fail at
///     <c>SaveChangesAsync</c>, because there is nothing to read up front.
/// </remarks>
public class NonExistentStreamException : Exception
{
    public NonExistentStreamException(object id)
        : base($"Attempt to append to a nonexistent event stream '{id}'.")
    {
        Id = id;
    }

    public object Id { get; }
}
