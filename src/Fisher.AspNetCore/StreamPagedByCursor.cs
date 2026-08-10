using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Fisher.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

namespace Fisher.AspNetCore;

/// <summary>
///     Streams one keyset page of documents, with the cursor for the next one (fisher#49).
/// </summary>
/// <remarks>
///     <para>
///         The other half of the paging trade fisher#27 carries both sides of. Unlike
///         <see cref="StreamPaged{T}" /> this cannot jump to an arbitrary page and reports no total;
///         what it gets in exchange is stability under concurrent writes and a cost that does not
///         grow with how far in the caller is.
///     </para>
///     <para>
///         <b>The items are the stored bytes.</b> The page comes back as raw JSON through
///         <c>ToJsonCursorPageAsync</c>, which shares its cursor preparation with the typed form
///         rather than repeating it — the ordering validation and the seek predicate are subtle enough
///         that a second copy would drift, and a drift there is a pager that silently skips or repeats
///         rows.
///     </para>
///     <para>
///         <b>The cursor is emitted twice, in a header and in the envelope, and that is deliberate.</b>
///         A header lets a client follow the page without parsing the body; the envelope keeps the
///         response self-describing for one that already has. The cursor's <c>v1:</c> base64-JSON
///         format is byte-identical to Polecat's, so a cursor is portable between the stores.
///     </para>
/// </remarks>
public sealed class StreamPagedByCursor<T> : IResult, IEndpointMetadataProvider where T : notnull
{
    /// <summary>The response header carrying the cursor for the next page, when there is one.</summary>
    public const string ContinuationHeader = "x-next-cursor";

    private readonly IQueryable<T> _queryable;
    private readonly string? _cursor;
    private readonly int _pageSize;

    /// <summary>Stream the page after <paramref name="cursor" />, or the first when it is null.</summary>
    public StreamPagedByCursor(IQueryable<T> queryable, string? cursor, int pageSize)
    {
        _queryable = queryable ?? throw new ArgumentNullException(nameof(queryable));
        _cursor = cursor;
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

        var page = await _queryable
            .ToJsonCursorPageAsync(Math.Max(1, _pageSize), _cursor, httpContext.RequestAborted)
            .ConfigureAwait(false);

        httpContext.Response.StatusCode = OnFoundStatus;
        httpContext.Response.ContentType = ContentType;

        if (page.NextCursor is not null)
        {
            httpContext.Response.Headers[ContinuationHeader] = page.NextCursor;
        }

        var envelope = new StringBuilder("{\"items\":[");

        for (var i = 0; i < page.Items.Count; i++)
        {
            if (i > 0)
            {
                envelope.Append(',');
            }

            envelope.Append(page.Items[i]);
        }

        // Encoded rather than interpolated: a cursor is base64 of JSON, so it cannot contain a quote
        // today — but "cannot today" is how a JSON injection gets written, and the encoder costs a
        // string.
        envelope.Append("],\"nextCursor\":")
            .Append(page.NextCursor is null
                ? "null"
                : JsonSerializer.Serialize(page.NextCursor, StringOptions))
            .Append('}');

        var bytes = Encoding.UTF8.GetBytes(envelope.ToString());
        httpContext.Response.ContentLength = bytes.Length;

        await httpContext.Response.Body.WriteAsync(bytes, httpContext.RequestAborted).ConfigureAwait(false);
    }

    /// <remarks>
    ///     The default encoder escapes <c>+</c> and <c>/</c>, which base64 is full of — producing a
    ///     valid but unreadable cursor. Relaxed encoding is safe here because the value is written
    ///     into a JSON string, not into HTML.
    /// </remarks>
    private static readonly JsonSerializerOptions StringOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Advertises <c>200</c> to OpenAPI.</summary>
    public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Metadata.Add(new ProducesResponseTypeMetadata(
            StatusCodes.Status200OK, typeof(object), ["application/json"]));
    }
}
