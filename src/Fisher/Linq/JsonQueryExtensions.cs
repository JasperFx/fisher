using System.Text;

namespace Fisher.Linq;

/// <summary>
///     Reads that hand back the stored JSON rather than a deserialized document (fisher#28).
/// </summary>
/// <remarks>
///     <para>
///         <b>The saving is larger here than on either sibling.</b> These exist to skip a
///         deserialize-then-reserialize round trip when the caller is going to write the document to an
///         HTTP response anyway. On Marten and Polecat that saves CPU for data that already crossed a
///         network from a database server; in Fisher the database <em>is</em> the caller's process, so
///         the round trip is the whole cost rather than a fraction of it.
///     </para>
///     <para>
///         <c>data</c> is TEXT holding exactly what System.Text.Json wrote, so the round trip is
///         byte-exact. PostgreSQL's <c>jsonb</c> normalises whitespace and key order and SQL Server's
///         <c>nvarchar</c> needs an encoding decision; neither sibling can promise this.
///     </para>
/// </remarks>
public static class JsonQueryExtensions
{
    /// <summary>
    ///     Every matching document's stored JSON, as one array.
    /// </summary>
    /// <remarks>
    ///     Concatenated in .NET rather than with SQLite's <c>json_group_array</c>, which would re-parse
    ///     and re-render every document — discarding the whole saving and, incidentally, reordering
    ///     object keys.
    /// </remarks>
    public static async Task<string> ToJsonArrayAsync<T>(this IQueryable<T> queryable,
        CancellationToken token = default) where T : notnull
    {
        var rows = await ProviderFor(queryable)
            .JsonRowsAsync<T>(queryable.Expression, "data", limit: null, token).ConfigureAwait(false);

        var json = new StringBuilder("[");

        for (var i = 0; i < rows.Count; i++)
        {
            if (i > 0)
            {
                json.Append(',');
            }

            json.Append(rows[i]);
        }

        return json.Append(']').ToString();
    }

    /// <summary>
    ///     The first matching document's JSON and its version, for an ETag response.
    /// </summary>
    /// <remarks>
    ///     Asks for <c>guid_version</c> explicitly rather than relying on it being in the read
    ///     projection — a query-only read normally drops it, since it has no version tracker to feed.
    ///     Refused for a type without optimistic concurrency, because the column does not exist and
    ///     there is no version to report.
    /// </remarks>
    public static async Task<DocumentJsonWithVersion?> ToJsonFirstWithVersionAsync<T>(
        this IQueryable<T> queryable, CancellationToken token = default) where T : notnull
    {
        var provider = ProviderFor(queryable);

        if (!provider.HasVersionColumn<T>())
        {
            throw new InvalidOperationException(
                $"'{typeof(T).Name}' has no guid_version column, so there is no version to report. "
                + "Register it with Schema.For<T>().UseOptimisticConcurrency() — or implement "
                + "JasperFx.Metadata.IVersioned, which turns it on — if it should have one.");
        }

        var rows = await provider
            .JsonRowsAsync<T>(queryable.Expression, "data, guid_version", limit: 1, token)
            .ConfigureAwait(false);

        return rows.Count == 0 ? null : new DocumentJsonWithVersion(rows[0], Guid.Parse(rows[1]));
    }

    /// <summary>
    ///     Write every matching document's JSON to a stream as an array.
    /// </summary>
    /// <remarks>
    ///     <b>Materialized before anything is written, deliberately.</b> A retried <c>SQLITE_BUSY</c>
    ///     re-executes the whole delegate, so streaming a live reader straight to the caller's stream
    ///     would resume against a disposed reader <em>and</em> a half-written response body. This is
    ///     the one place the retry semantics and the streaming goal genuinely conflict, and buffering
    ///     is the resolution: the saving being chased is the serializer round trip, not the buffer.
    /// </remarks>
    public static async Task StreamJsonArrayAsync<T>(this IQueryable<T> queryable, Stream destination,
        CancellationToken token = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(destination);

        var json = await queryable.ToJsonArrayAsync(token).ConfigureAwait(false);

        await destination.WriteAsync(Encoding.UTF8.GetBytes(json), token).ConfigureAwait(false);
    }

    /// <summary>
    ///     A keyset page whose items are the stored JSON rather than materialized documents
    ///     (fisher#27's JSON variant, which fisher#49 is the consumer for).
    /// </summary>
    /// <remarks>
    ///     Same cursor rules as <see cref="QueryableExtensions.ToCursorPageAsync{T}" /> — it shares
    ///     the preparation rather than repeating it, because the ordering validation, the decode and
    ///     the seek predicate are subtle enough that a second copy would drift, and a drift there is a
    ///     pager that silently skips or repeats rows.
    /// </remarks>
    public static Task<Pagination.CursorPage<string>> ToJsonCursorPageAsync<T>(this IQueryable<T> queryable,
        int pageSize, string? cursor = null, CancellationToken token = default) where T : notnull
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        return ProviderFor(queryable).CursorPageJsonAsync<T>(queryable.Expression, pageSize, cursor, token);
    }

    private static FisherQueryProvider ProviderFor<T>(IQueryable<T> queryable)
        => queryable.Provider as FisherQueryProvider
           ?? throw new InvalidOperationException(
               "This operator only works on a query created by Fisher's session.Query<T>().");
}

/// <summary>
///     A document's stored JSON and the version it was read at.
/// </summary>
public sealed record DocumentJsonWithVersion(string Json, Guid Version);
