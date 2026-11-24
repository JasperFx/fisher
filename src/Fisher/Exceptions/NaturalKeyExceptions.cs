namespace Fisher.Exceptions;

/// <summary>
///     A natural key resolved to no live stream (fisher#40).
/// </summary>
/// <remarks>
///     <para>
///         <b>Fisher no longer throws this</b> (fisher#694). Since Fisher 2.0, a fetch for writing by a key
///         naming no live stream starts a new stream at version 0, as Marten 10 and Polecat 6 do, and
///         <c>FetchLatest</c> by such a key answers null. The type is kept so that code catching it still
///         compiles; such a <c>catch</c> no longer runs.
///     </para>
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
