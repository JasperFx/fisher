using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using JasperFx.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

namespace Fisher.AspNetCore;

/// <summary>
///     Identifies a stream by whichever identity style the store uses, so the results below need one
///     shape rather than two of everything.
/// </summary>
/// <remarks>
///     Polecat carries a <c>FetchStreamPlan</c> per read for the same reason. This is smaller because
///     Fisher's reads take their bounds as ordinary arguments.
/// </remarks>
public readonly record struct StreamKey
{
    private readonly Guid _id;
    private readonly string? _key;

    /// <summary>A Guid-identified stream.</summary>
    public StreamKey(Guid id)
    {
        _id = id;
        _key = null;
    }

    /// <summary>A string-identified stream.</summary>
    public StreamKey(string key)
    {
        _key = key ?? throw new ArgumentNullException(nameof(key));
        _id = Guid.Empty;
    }

    /// <summary>Whether this names a Guid-identified stream.</summary>
    public bool IsGuid => _key is null;

    /// <summary>The Guid identity. Meaningless when <see cref="IsGuid" /> is false.</summary>
    public Guid Id => _id;

    /// <summary>The string identity. Null when <see cref="IsGuid" /> is true.</summary>
    public string Key => _key!;

    /// <summary>A Guid-identified stream.</summary>
    public static implicit operator StreamKey(Guid id) => new(id);

    /// <summary>A string-identified stream.</summary>
    public static implicit operator StreamKey(string key) => new(key);

    internal Task<StreamState?> FetchStateAsync(IQuerySession session, CancellationToken token)
        => IsGuid
            ? session.Events.FetchStreamStateAsync(_id, token)
            : session.Events.FetchStreamStateAsync(_key!, token);

    internal Task<IReadOnlyList<IEvent>> FetchEventsAsync(IQuerySession session, long version,
        DateTimeOffset? timestamp, long fromVersion, CancellationToken token)
        => IsGuid
            ? session.Events.FetchStreamAsync(_id, version, timestamp, fromVersion, token)
            : session.Events.FetchStreamAsync(_key!, version, timestamp, fromVersion, token);
}

/// <summary>
///     Writes a stream's metadata to the response, or <c>404</c> when there is no such stream.
/// </summary>
public sealed class StreamEventState : IResult, IEndpointMetadataProvider
{
    private readonly IQuerySession _session;
    private readonly StreamKey _stream;

    /// <summary>Report the metadata of one stream.</summary>
    public StreamEventState(IQuerySession session, StreamKey stream)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _stream = stream;
    }

    /// <inheritdoc cref="StreamOne{T}.OnFoundStatus" />
    public int OnFoundStatus { get; init; } = StatusCodes.Status200OK;

    /// <summary>
    ///     Whether to emit an <c>ETag</c> from the stream's version and honour <c>If-None-Match</c>.
    ///     On by default.
    /// </summary>
    /// <remarks>
    ///     Keyed on the stream version, which is exactly the right validator: it moves if and only if
    ///     an event was appended, so an unchanged stream is a <c>304</c> without reading its events.
    /// </remarks>
    public bool EmitETag { get; init; } = true;

    /// <inheritdoc />
    [UnconditionalSuppressMessage("Trimming", "IL2046",
        Justification = "IResult.ExecuteAsync is not RUC-annotated; the contract lives on this override.")]
    [UnconditionalSuppressMessage("AOT", "IL3051",
        Justification = "IResult.ExecuteAsync is not RDC-annotated; the contract lives on this override.")]
    [RequiresDynamicCode("Serializes StreamStateResponse with System.Text.Json.")]
    [RequiresUnreferencedCode("Reflects over StreamStateResponse via System.Text.Json.")]
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var state = await _stream.FetchStateAsync(_session, httpContext.RequestAborted).ConfigureAwait(false);

        if (state is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (EmitETag && await httpContext.TryWriteNotModifiedAsync(state.Version).ConfigureAwait(false))
        {
            return;
        }

        httpContext.Response.StatusCode = OnFoundStatus;

        await httpContext.Response.WriteAsJsonAsync(StreamStateResponse.From(state),
            httpContext.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Advertises <c>200: StreamStateResponse</c>, <c>304</c> and <c>404</c>.</summary>
    public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Metadata.Add(new ProducesResponseTypeMetadata(
            StatusCodes.Status200OK, typeof(StreamStateResponse), ["application/json"]));
        builder.Metadata.Add(new ProducesResponseTypeMetadata(StatusCodes.Status304NotModified, typeof(void), []));
        builder.Metadata.Add(new ProducesResponseTypeMetadata(StatusCodes.Status404NotFound, typeof(void), []));
    }
}

/// <summary>
///     Writes a stream's events to the response, or <c>404</c> when it holds none.
/// </summary>
/// <remarks>
///     Bounded exactly as <c>FetchStreamAsync</c> is — by version, by a version floor, or by
///     timestamp — because a stream long enough to be worth an endpoint is long enough to want a
///     bound.
/// </remarks>
public sealed class StreamEvents : IResult, IEndpointMetadataProvider
{
    private readonly IQuerySession _session;
    private readonly StreamKey _stream;

    /// <summary>Report one stream's events.</summary>
    public StreamEvents(IQuerySession session, StreamKey stream)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _stream = stream;
    }

    /// <summary>Read up to and including this version. Zero reads the whole stream.</summary>
    public long Version { get; init; }

    /// <summary>Read from this version onwards. Zero starts at the beginning.</summary>
    public long FromVersion { get; init; }

    /// <summary>Read up to this instant.</summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <inheritdoc cref="StreamOne{T}.OnFoundStatus" />
    public int OnFoundStatus { get; init; } = StatusCodes.Status200OK;

    /// <summary>Status written when the stream holds no matching events. <c>404</c> by default.</summary>
    public int OnEmptyStatus { get; init; } = StatusCodes.Status404NotFound;

    /// <inheritdoc />
    [UnconditionalSuppressMessage("Trimming", "IL2046",
        Justification = "IResult.ExecuteAsync is not RUC-annotated; the contract lives on this override.")]
    [UnconditionalSuppressMessage("AOT", "IL3051",
        Justification = "IResult.ExecuteAsync is not RDC-annotated; the contract lives on this override.")]
    [RequiresDynamicCode("Serializes each event's Data payload with System.Text.Json.")]
    [RequiresUnreferencedCode("Reflects over each event's Data payload via System.Text.Json.")]
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var events = await _stream
            .FetchEventsAsync(_session, Version, Timestamp, FromVersion, httpContext.RequestAborted)
            .ConfigureAwait(false);

        if (events.Count == 0)
        {
            httpContext.Response.StatusCode = OnEmptyStatus;
            return;
        }

        httpContext.Response.StatusCode = OnFoundStatus;

        await httpContext.Response.WriteAsJsonAsync(EventResponse.From(events),
            httpContext.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Advertises <c>200: EventResponse[]</c> and <c>404</c>.</summary>
    public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Metadata.Add(new ProducesResponseTypeMetadata(
            StatusCodes.Status200OK, typeof(EventResponse[]), ["application/json"]));
        builder.Metadata.Add(new ProducesResponseTypeMetadata(StatusCodes.Status404NotFound, typeof(void), []));
    }
}

/// <summary>
///     Writes a stream's aggregate, projected live, to the response — or <c>404</c> when there is no
///     such stream.
/// </summary>
/// <remarks>
///     <para>
///         <b>The ETag is read before the aggregate is built, which is the whole point.</b> A stream's
///         version moves if and only if an event was appended, so a matching <c>If-None-Match</c>
///         returns <c>304</c> having read one row of <c>fi_streams</c> and having folded nothing at
///         all. For a long stream that is the difference between an endpoint that is cheap when
///         nothing changed and one that is not.
///     </para>
///     <para>
///         Unlike <see cref="StreamOne{T}" /> the body is serialized rather than copied: a live
///         aggregate is built in memory and has no stored JSON to stream. Use <c>StreamOne</c> against
///         a snapshot when the aggregate is projected rather than folded.
///     </para>
/// </remarks>
public sealed class StreamAggregate<T> : IResult, IEndpointMetadataProvider where T : class
{
    private readonly IQuerySession _session;
    private readonly StreamKey _stream;

    /// <summary>Project one stream live and write the result.</summary>
    public StreamAggregate(IQuerySession session, StreamKey stream)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _stream = stream;
    }

    /// <inheritdoc cref="StreamOne{T}.OnFoundStatus" />
    public int OnFoundStatus { get; init; } = StatusCodes.Status200OK;

    /// <inheritdoc cref="StreamEventState.EmitETag" />
    public bool EmitETag { get; init; } = true;

    /// <inheritdoc />
    [UnconditionalSuppressMessage("Trimming", "IL2046",
        Justification = "IResult.ExecuteAsync is not RUC-annotated; the contract lives on this override.")]
    [UnconditionalSuppressMessage("AOT", "IL3051",
        Justification = "IResult.ExecuteAsync is not RDC-annotated; the contract lives on this override.")]
    [RequiresDynamicCode("Serializes the projected aggregate with System.Text.Json, and folds it through the generated dispatcher.")]
    [RequiresUnreferencedCode("Reflects over T via System.Text.Json and through JasperFx's aggregation.")]
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var state = await _stream.FetchStateAsync(_session, httpContext.RequestAborted).ConfigureAwait(false);

        if (state is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Before the fold, not after — a 304 should cost one row read and no aggregation.
        if (EmitETag && await httpContext.TryWriteNotModifiedAsync(state.Version).ConfigureAwait(false))
        {
            return;
        }

        var aggregate = _stream.IsGuid
            ? await _session.Events.AggregateStreamAsync<T>(_stream.Id, token: httpContext.RequestAborted)
                .ConfigureAwait(false)
            : await _session.Events.AggregateStreamAsync<T>(_stream.Key, token: httpContext.RequestAborted)
                .ConfigureAwait(false);

        if (aggregate is null)
        {
            // The stream exists but folds to nothing — a stream deleted by its own ShouldDelete, or one
            // holding only events this aggregate ignores. Not the same as "no such stream", but the
            // same answer to a caller asking for the aggregate.
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        httpContext.Response.StatusCode = OnFoundStatus;

        await httpContext.Response.WriteAsJsonAsync(aggregate, httpContext.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>Advertises <c>200: T</c>, <c>304</c> and <c>404</c>.</summary>
    public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Metadata.Add(new ProducesResponseTypeMetadata(
            StatusCodes.Status200OK, typeof(T), ["application/json"]));
        builder.Metadata.Add(new ProducesResponseTypeMetadata(StatusCodes.Status304NotModified, typeof(void), []));
        builder.Metadata.Add(new ProducesResponseTypeMetadata(StatusCodes.Status404NotFound, typeof(void), []));
    }
}

/// <summary>
///     The ETag half of the event-stream results, shared so the three cannot disagree about what a
///     <c>304</c> looks like.
/// </summary>
internal static class ConditionalResponses
{
    internal static Task<bool> TryWriteNotModifiedAsync(this HttpContext context, long version)
    {
        var etag = ETagHelpers.Format(version);

        if (!ETagHelpers.IfNoneMatchMatches(context, etag))
        {
            // Emitted on the way out too, or a client has nothing to send back next time.
            context.Response.Headers.ETag = etag;
            return Task.FromResult(false);
        }

        context.Response.StatusCode = StatusCodes.Status304NotModified;
        context.Response.Headers.ETag = etag;
        context.Response.ContentLength = 0;

        return Task.FromResult(true);
    }
}
