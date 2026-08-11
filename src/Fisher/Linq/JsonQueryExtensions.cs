using System.Globalization;
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

        if (provider.VersionSourceFor<T>() != DocumentVersionSource.GuidVersion)
        {
            throw new InvalidOperationException(
                $"'{typeof(T).Name}' has no guid_version column, so there is no version to report. "
                + "Register it with Schema.For<T>().UseOptimisticConcurrency() — or implement "
                + "JasperFx.Metadata.IVersioned, which turns it on — if it should have one. A type "
                + "using numeric revisions is read with ToJsonFirstWithRevisionAsync instead.");
        }

        var rows = await provider
            .JsonRowsAsync<T>(queryable.Expression, "data, guid_version", limit: 1, token)
            .ConfigureAwait(false);

        return rows.Count == 0 ? null : new DocumentJsonWithVersion(rows[0], Guid.Parse(rows[1]));
    }

    /// <summary>
    ///     The first matching document's JSON and its numeric revision, for an ETag response.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         fisher#62, the marten#5120 class. The counterpart of
    ///         <see cref="ToJsonFirstWithVersionAsync{T}" /> for a type registered with
    ///         <c>UseNumericRevisions()</c> — the alternative concurrency style, not a lesser one, and a
    ///         revision is exactly as good a cache validator as a Guid version.
    ///     </para>
    ///     <para>
    ///         <b>Two methods where Marten widened one</b>, because the two flavors are two physical
    ///         columns here — <c>guid_version</c> and <c>revision</c>, of different types — rather than
    ///         one <c>mt_version</c> read at either width. A type carries one or the other and never
    ///         both; <c>AssertConcurrencyIsCoherent</c> refuses that pair at configuration time, so
    ///         there is no ambiguity for a caller to resolve. Use
    ///         <see cref="QueryableVersionSourceExtensions.VersionSourceFor{T}" /> to ask which applies.
    ///     </para>
    /// </remarks>
    public static async Task<DocumentJsonWithRevision?> ToJsonFirstWithRevisionAsync<T>(
        this IQueryable<T> queryable, CancellationToken token = default) where T : notnull
    {
        var provider = ProviderFor(queryable);

        if (provider.VersionSourceFor<T>() != DocumentVersionSource.NumericRevision)
        {
            throw new InvalidOperationException(
                $"'{typeof(T).Name}' has no revision column, so there is no revision to report. "
                + "Register it with Schema.For<T>().UseNumericRevisions() — or implement "
                + "JasperFx.IRevisioned, which turns it on — if it should have one. A type using "
                + "optimistic concurrency is read with ToJsonFirstWithVersionAsync instead.");
        }

        var rows = await provider
            .JsonRowsAsync<T>(queryable.Expression, $"data, {Storage.NumericRevision.Column}", limit: 1, token)
            .ConfigureAwait(false);

        return rows.Count == 0
            ? null
            : new DocumentJsonWithRevision(rows[0], int.Parse(rows[1], CultureInfo.InvariantCulture));
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

/// <summary>
///     A document's stored JSON and the numeric revision it was read at (fisher#62).
/// </summary>
public sealed record DocumentJsonWithRevision(string Json, int Revision);

/// <summary>
///     Which concurrency column, if either, a document type carries.
/// </summary>
/// <remarks>
///     The two are alternatives — a type has <c>guid_version</c> or <c>revision</c> or neither, never
///     both. A caller that wants an ETag without knowing which style a type was registered with asks
///     this first; see <see cref="QueryableVersionSourceExtensions.VersionSourceFor{T}" />.
/// </remarks>
public enum DocumentVersionSource
{
    /// <summary>Neither column: there is no version to report.</summary>
    None,

    /// <summary>A <c>guid_version</c> column, from <c>UseOptimisticConcurrency()</c>.</summary>
    GuidVersion,

    /// <summary>A <c>revision</c> column, from <c>UseNumericRevisions()</c>.</summary>
    NumericRevision
}

/// <summary>
///     Asking a query which version column its document type carries.
/// </summary>
public static class QueryableVersionSourceExtensions
{
    /// <summary>
    ///     Which concurrency column the queried document type carries, so a caller can choose between
    ///     <see cref="JsonQueryExtensions.ToJsonFirstWithVersionAsync{T}" /> and
    ///     <see cref="JsonQueryExtensions.ToJsonFirstWithRevisionAsync{T}" /> without knowing how the
    ///     type was registered.
    /// </summary>
    public static DocumentVersionSource VersionSourceFor<T>(this IQueryable<T> queryable) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(queryable);

        return queryable.Provider is FisherQueryProvider provider
            ? provider.VersionSourceFor<T>()
            : throw new InvalidOperationException(
                "This operator only works on a query created by Fisher's session.Query<T>().");
    }
}
