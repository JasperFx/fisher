namespace Fisher.Exceptions;

/// <summary>
///     Two different streams claimed one natural key (fisher#40).
/// </summary>
/// <remarks>
///     <para>
///         <b>Fisher refuses this where Polecat repoints.</b> Polecat's <c>MERGE</c> updates the stream
///         id whenever the key already exists, so a second stream claiming <c>ORD-1234</c> silently
///         takes it and every later lookup resolves to the newcomer — the original stream is still
///         there and simply becomes unreachable by the identifier it was created with. A natural key
///         exists to name one stream, so a second claimant is a bug in the caller's key derivation
///         rather than an instruction.
///     </para>
///     <para>
///         Re-asserting the <em>same</em> mapping is not this: every event carrying the key rewrites
///         the row, and that is idempotent by design.
///     </para>
/// </remarks>
public class DuplicateNaturalKeyException : Exception
{
    internal DuplicateNaturalKeyException(Type aggregateType, object key)
        : base($"The natural key '{key}' is already mapped to a different stream for aggregate type "
               + $"'{aggregateType.Name}'. A natural key identifies one stream; if the mapping is meant "
               + "to move, delete the existing row first.")
    {
        AggregateType = aggregateType;
        Key = key;
    }

    public Type AggregateType { get; }
    public object Key { get; }
}

/// <summary>
///     A natural key resolved to no live stream (fisher#40).
/// </summary>
/// <remarks>
///     Either nothing ever claimed the key, or the stream it names has been archived — the lookup joins
///     <c>fi_streams</c> and filters archived rows out, so the two are one answer here. That is
///     deliberate: an archived stream is not available for writing, and reporting "no such key" for a
///     stream the caller cannot write to anyway is the more useful of the two answers.
/// </remarks>
public class UnknownNaturalKeyException : Exception
{
    internal UnknownNaturalKeyException(Type aggregateType, object key)
        : base($"No live stream is mapped to the natural key '{key}' for aggregate type "
               + $"'{aggregateType.Name}'. The key may never have been assigned, or its stream may have "
               + "been archived.")
    {
        AggregateType = aggregateType;
        Key = key;
    }

    public Type AggregateType { get; }
    public object Key { get; }
}
