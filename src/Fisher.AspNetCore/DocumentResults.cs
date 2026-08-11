using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using Fisher.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

namespace Fisher.AspNetCore;

/// <summary>
///     Streams the first matching document to the response as JSON — <c>404</c> when the query
///     produces none (fisher#49).
/// </summary>
/// <remarks>
///     <para>
///         <b>These results are worth more on Fisher than on either sibling, and the reason is
///         structural.</b> They exist to skip a deserialize-then-reserialize round trip. On Marten and
///         Polecat that saves CPU for data that has already crossed a network from a database server,
///         so it is a fraction of the cost. <b>Fisher's database is the web process</b>, so the round
///         trip <em>is</em> the cost: an endpoint reading a document and returning it goes from
///         "parse JSON, build an object, serialize an object" to "copy bytes", in-process, with no
///         intermediate representation at all.
///     </para>
///     <para>
///         <b>And the bytes are exactly what was stored.</b> <c>data</c> is TEXT holding precisely
///         what System.Text.Json wrote, so streaming it transforms nothing — a guarantee neither
///         sibling can make, because <c>jsonb</c> normalises whitespace and key order and
///         <c>nvarchar</c> needs an encoding decision. What the endpoint returns is what was persisted,
///         byte for byte.
///     </para>
///     <para>
///         <b>Use <see cref="StreamOne{T}" /> for documents and <see cref="StreamAggregate{T}" /> for
///         event-sourced aggregates projected live from their stream.</b>
///     </para>
/// </remarks>
/// <typeparam name="T">The document type.</typeparam>
public sealed class StreamOne<T> : IResult, IEndpointMetadataProvider where T : notnull
{
    private readonly IQueryable<T> _queryable;

    /// <summary>Stream the first document the query matches.</summary>
    public StreamOne(IQueryable<T> queryable)
        => _queryable = queryable ?? throw new ArgumentNullException(nameof(queryable));

    /// <summary>Status written on a hit. 200 by default.</summary>
    public int OnFoundStatus { get; init; } = StatusCodes.Status200OK;

    /// <summary>Response content type. <c>application/json</c> by default.</summary>
    public string ContentType { get; init; } = "application/json";

    /// <summary>
    ///     Whether to emit an <c>ETag</c> from the document's version and honour
    ///     <c>If-None-Match</c> with a <c>304</c>. On by default.
    /// </summary>
    /// <remarks>
    ///     <b>Requires the type to carry <c>guid_version</c></b> — that is,
    ///     <c>UseOptimisticConcurrency()</c> or <c>IVersioned</c>. A query-only read normally drops
    ///     that column, so <c>ToJsonFirstWithVersionAsync</c> asks for it explicitly and refuses by
    ///     name for a type that has none. Set this false for a type without one.
    /// </remarks>
    public bool EmitETag { get; init; } = true;

    /// <inheritdoc />
    [UnconditionalSuppressMessage("Trimming", "IL2046",
        Justification = "IResult.ExecuteAsync is not RUC-annotated; the contract lives on this override.")]
    [UnconditionalSuppressMessage("AOT", "IL3051",
        Justification = "IResult.ExecuteAsync is not RDC-annotated; the contract lives on this override.")]
    [RequiresDynamicCode("Routes through Fisher's LINQ provider, which closes generic types over T.")]
    [RequiresUnreferencedCode("Routes through Fisher's LINQ provider, which reflects over T.")]
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (!EmitETag)
        {
            await WriteWithoutETagAsync(httpContext).ConfigureAwait(false);
            return;
        }

        // The JSON and the version in one read, so a 304 needs no second query and a 200 streams the
        // stored bytes.
        //
        // fisher#62 (the marten#5120 class): a numeric-revisioned document is served from its
        // `revision` rather than refused. The two concurrency styles are alternatives, and a revision
        // validates a cached representation exactly as well as a Guid version does — refusing one of
        // them left the whole revisioned half of a store unable to emit an ETag at all.
        var (json, etag) = _queryable.VersionSourceFor() switch
        {
            DocumentVersionSource.NumericRevision => Unpack(
                await _queryable.ToJsonFirstWithRevisionAsync(httpContext.RequestAborted).ConfigureAwait(false)),
            _ => Unpack(
                await _queryable.ToJsonFirstWithVersionAsync(httpContext.RequestAborted).ConfigureAwait(false))
        };

        if (json is null || etag is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (ETagHelpers.IfNoneMatchMatches(httpContext, etag))
        {
            httpContext.Response.StatusCode = StatusCodes.Status304NotModified;
            httpContext.Response.Headers.ETag = etag;
            httpContext.Response.ContentLength = 0;
            return;
        }

        httpContext.Response.Headers.ETag = etag;

        await WriteAsync(httpContext, json).ConfigureAwait(false);
    }

    /// <summary>The stored JSON and the ETag its version reads as, or two nulls for no match.</summary>
    private static (string? Json, string? ETag) Unpack(DocumentJsonWithVersion? result)
        => result is null ? (null, null) : (result.Json, ETagHelpers.Format(result.Version));

    /// <inheritdoc cref="Unpack(DocumentJsonWithVersion?)" />
    private static (string? Json, string? ETag) Unpack(DocumentJsonWithRevision? result)
        => result is null ? (null, null) : (result.Json, ETagHelpers.Format(result.Revision));

    [RequiresDynamicCode("Routes through Fisher's LINQ provider, which closes generic types over T.")]
    [RequiresUnreferencedCode("Routes through Fisher's LINQ provider, which reflects over T.")]
    private async Task WriteWithoutETagAsync(HttpContext httpContext)
    {
        // Take(1) rather than a first-row terminal, because the JSON array read is the one that does
        // not deserialize — the point of the whole type.
        var json = await _queryable.Take(1).ToJsonArrayAsync(httpContext.RequestAborted)
            .ConfigureAwait(false);

        if (json == "[]")
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await WriteAsync(httpContext, json[1..^1]).ConfigureAwait(false);
    }

    private async Task WriteAsync(HttpContext httpContext, string json)
    {
        httpContext.Response.StatusCode = OnFoundStatus;
        httpContext.Response.ContentType = ContentType;

        var bytes = Encoding.UTF8.GetBytes(json);
        httpContext.Response.ContentLength = bytes.Length;

        await httpContext.Response.Body.WriteAsync(bytes, httpContext.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Advertises <c>200: T</c>, <c>304</c> and <c>404</c> to OpenAPI.</summary>
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
///     Streams every matching document to the response as a JSON array.
/// </summary>
/// <remarks>
///     <b>A genuine divergence from Polecat's, and the reason is fisher#28.</b> Polecat's
///     <c>StreamMany</c> materializes objects and calls
///     <c>JsonSerializer.SerializeToUtf8Bytes</c> — which throws away the saving the type exists for,
///     because it deserializes every row and then serializes it back. Fisher's
///     <c>ToJsonArrayAsync</c> concatenates the stored <c>data</c> columns in .NET, so nothing is
///     parsed and nothing is re-rendered. That is also why it does <em>not</em> use
///     <c>json_group_array</c>: that function re-parses and re-renders every document, and reorders
///     object keys on the way.
/// </remarks>
public sealed class StreamMany<T> : IResult, IEndpointMetadataProvider where T : notnull
{
    private readonly IQueryable<T> _queryable;

    /// <summary>Stream every document the query matches.</summary>
    public StreamMany(IQueryable<T> queryable)
        => _queryable = queryable ?? throw new ArgumentNullException(nameof(queryable));

    /// <inheritdoc cref="StreamOne{T}.OnFoundStatus" />
    public int OnFoundStatus { get; init; } = StatusCodes.Status200OK;

    /// <inheritdoc cref="StreamOne{T}.ContentType" />
    public string ContentType { get; init; } = "application/json";

    /// <inheritdoc />
    /// <remarks>
    ///     <b>Materialized before anything is written to the response body, deliberately.</b> A
    ///     retried <c>SQLITE_BUSY</c> re-executes the whole read, so streaming a live reader straight
    ///     to the response would resume against a disposed reader <em>and</em> a half-written body.
    ///     This is the one place the retry semantics and the streaming goal genuinely conflict;
    ///     buffering is the resolution, because the saving being chased is the serializer round trip
    ///     rather than the buffer. Same decision, and the same words, as
    ///     <c>StreamJsonArrayAsync</c>'s.
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2046",
        Justification = "IResult.ExecuteAsync is not RUC-annotated; the contract lives on this override.")]
    [UnconditionalSuppressMessage("AOT", "IL3051",
        Justification = "IResult.ExecuteAsync is not RDC-annotated; the contract lives on this override.")]
    [RequiresDynamicCode("Routes through Fisher's LINQ provider, which closes generic types over T.")]
    [RequiresUnreferencedCode("Routes through Fisher's LINQ provider, which reflects over T.")]
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var json = await _queryable.ToJsonArrayAsync(httpContext.RequestAborted).ConfigureAwait(false);

        httpContext.Response.StatusCode = OnFoundStatus;
        httpContext.Response.ContentType = ContentType;

        var bytes = Encoding.UTF8.GetBytes(json);
        httpContext.Response.ContentLength = bytes.Length;

        await httpContext.Response.Body.WriteAsync(bytes, httpContext.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Advertises <c>200: IReadOnlyList&lt;T&gt;</c> to OpenAPI.</summary>
    public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Metadata.Add(new ProducesResponseTypeMetadata(
            StatusCodes.Status200OK, typeof(IReadOnlyList<T>), ["application/json"]));
    }
}

/// <summary>
///     Streams one offset page of documents, with the total count in a header.
/// </summary>
/// <remarks>
///     The total is what an offset pager needs and a cursor pager cannot give — see
///     <see cref="StreamPagedByCursor{T}" /> for the other trade. It rides a response header rather
///     than an envelope so the body stays the stored bytes.
/// </remarks>
public sealed class StreamPaged<T> : IResult, IEndpointMetadataProvider where T : notnull
{
    /// <summary>The header carrying the total number of matching documents.</summary>
    public const string TotalCountHeader = "x-total-count";

    private readonly IQueryable<T> _queryable;
    private readonly int _pageNumber;
    private readonly int _pageSize;

    /// <summary>Stream page <paramref name="pageNumber" /> (1-based) of the query.</summary>
    public StreamPaged(IQueryable<T> queryable, int pageNumber, int pageSize)
    {
        _queryable = queryable ?? throw new ArgumentNullException(nameof(queryable));
        _pageNumber = pageNumber;
        _pageSize = pageSize;
    }

    /// <inheritdoc cref="StreamOne{T}.OnFoundStatus" />
    public int OnFoundStatus { get; init; } = StatusCodes.Status200OK;

    /// <inheritdoc cref="StreamOne{T}.ContentType" />
    public string ContentType { get; init; } = "application/json";

    /// <inheritdoc />
    [UnconditionalSuppressMessage("Trimming", "IL2046",
        Justification = "IResult.ExecuteAsync is not RUC-annotated; the contract lives on this override.")]
    [UnconditionalSuppressMessage("AOT", "IL3051",
        Justification = "IResult.ExecuteAsync is not RDC-annotated; the contract lives on this override.")]
    [RequiresDynamicCode("Routes through Fisher's LINQ provider, which closes generic types over T.")]
    [RequiresUnreferencedCode("Routes through Fisher's LINQ provider, which reflects over T.")]
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var pageNumber = Math.Max(1, _pageNumber);
        var pageSize = Math.Max(1, _pageSize);

        // The total is a second statement rather than a window function, for the reason fisher#27
        // records: count(*) over () returns no row at all for a page past the end, which is exactly
        // when a pager most needs the real total.
        var total = await _queryable.CountAsync(httpContext.RequestAborted).ConfigureAwait(false);

        var json = await _queryable.Skip((pageNumber - 1) * pageSize).Take(pageSize)
            .ToJsonArrayAsync(httpContext.RequestAborted).ConfigureAwait(false);

        httpContext.Response.StatusCode = OnFoundStatus;
        httpContext.Response.ContentType = ContentType;
        httpContext.Response.Headers[TotalCountHeader] = total.ToString();

        var bytes = Encoding.UTF8.GetBytes(json);
        httpContext.Response.ContentLength = bytes.Length;

        await httpContext.Response.Body.WriteAsync(bytes, httpContext.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Advertises <c>200: IReadOnlyList&lt;T&gt;</c> to OpenAPI.</summary>
    public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Metadata.Add(new ProducesResponseTypeMetadata(
            StatusCodes.Status200OK, typeof(IReadOnlyList<T>), ["application/json"]));
    }
}
