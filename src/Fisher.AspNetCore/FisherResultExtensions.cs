using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;

namespace Fisher.AspNetCore;

/// <summary>
///     The shorthands that make the results read as endpoint code (fisher#49).
/// </summary>
/// <remarks>
///     Every one of these is <c>new StreamX(...)</c>, so a handler that wants to configure a result —
///     a different status, no ETag — constructs it directly. These exist because
///     <c>session.Query&lt;T&gt;().Where(...).StreamOne()</c> is what an endpoint actually wants to
///     say.
/// </remarks>
public static class FisherResultExtensions
{
    /// <inheritdoc cref="StreamOne{T}" />
    public static IResult StreamOne<T>(this IQueryable<T> queryable) where T : notnull
        => new StreamOne<T>(queryable);

    /// <inheritdoc cref="StreamMany{T}" />
    public static IResult StreamMany<T>(this IQueryable<T> queryable) where T : notnull
        => new StreamMany<T>(queryable);

    /// <inheritdoc cref="StreamPaged{T}" />
    public static IResult StreamPaged<T>(this IQueryable<T> queryable, int pageNumber, int pageSize)
        where T : notnull
        => new StreamPaged<T>(queryable, pageNumber, pageSize);

    /// <inheritdoc cref="StreamPagedByCursor{T}" />
    public static IResult StreamPagedByCursor<T>(this IQueryable<T> queryable, string? cursor, int pageSize)
        where T : notnull
        => new StreamPagedByCursor<T>(queryable, cursor, pageSize);

    /// <inheritdoc cref="StreamEventState" />
    public static IResult StreamEventState(this IQuerySession session, StreamKey stream)
        => new StreamEventState(session, stream);

    /// <inheritdoc cref="StreamEvents" />
    public static IResult StreamEvents(this IQuerySession session, StreamKey stream,
        long version = 0, long fromVersion = 0, DateTimeOffset? timestamp = null)
        => new StreamEvents(session, stream)
        {
            Version = version,
            FromVersion = fromVersion,
            Timestamp = timestamp
        };

    /// <inheritdoc cref="StreamAggregate{T}" />
    [UnconditionalSuppressMessage("Trimming", "IL2091:DynamicallyAccessedMembers",
        Justification = "Constructs StreamAggregate<T>; the aggregation requirement is declared on its ExecuteAsync.")]
    public static IResult StreamAggregate<T>(this IQuerySession session, StreamKey stream) where T : class
        => new StreamAggregate<T>(session, stream);
}
