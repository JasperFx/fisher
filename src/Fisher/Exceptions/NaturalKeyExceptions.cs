namespace Fisher.Exceptions;

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
