namespace Fisher.Exceptions;

/// <summary>
///     Two different streams claimed one natural key (fisher#40).
/// </summary>
/// <remarks>
///     <para>
///         <b>Refusing is now the shared contract, and it was Fisher's behaviour that became it.</b>
///         Polecat's <c>MERGE</c>-based lookup write repointed the key at the newcomer, so a second
///         stream claiming <c>ORD-1234</c> silently took it and the original became unreachable by
///         the identifier it was created with. jasperfx#764 ruled for refusing — a natural key exists
///         to name one stream, so a second claimant is a bug in the caller's key derivation rather
///         than an instruction, and silently losing the original mapping is the worse failure because
///         nothing reports it. <c>NaturalKeyCompliance</c> pins it for every store, and Polecat is
///         changing to match (polecat#549).
///     </para>
///     <para>
///         So this <b>subclasses <see cref="JasperFx.Events.DuplicateNaturalKeyException" /></b>
///         (fisher#178): the canonical type was lifted from this one, with the message adopted
///         verbatim, so there is nothing to reconcile. Subclassing rather than deleting keeps an
///         existing <c>catch (Fisher.Exceptions.DuplicateNaturalKeyException)</c> working while a
///         <c>catch</c> on the shared type starts working — which the compliance suite needs, since
///         it asserts on the shared type. The base's nullable <c>ExistingStreamId</c> /
///         <c>ClaimingStreamId</c> pair is for a store that probes the lookup before writing; Fisher
///         infers the conflict from a guarded upsert that returned no row, so neither id is in hand
///         and both stay null.
///     </para>
///     <para>
///         Re-asserting the <em>same</em> mapping is not this: every event carrying the key rewrites
///         the row, and that is idempotent by design.
///     </para>
/// </remarks>
public class DuplicateNaturalKeyException : JasperFx.Events.DuplicateNaturalKeyException
{
    internal DuplicateNaturalKeyException(Type aggregateType, object key)
        : base(aggregateType, key)
    {
    }
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
