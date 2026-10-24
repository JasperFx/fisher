using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Fisher.Internal;
using Fisher.Storage;
using JasperFx;
using JasperFx.Core.Reflection;
using JasperFx.Documents;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;

namespace Fisher;

/// <summary>
///     <see cref="IDocumentStoreDiagnostics.AggregateDocumentJsonAsync" /> (jasperfx#1082): a document of a
///     single-stream projection's type, built now by live-aggregating its stream, rendered as the JSON
///     Fisher would store for it — with nothing stored.
/// </summary>
/// <remarks>
///     <para>
///         <b>Live aggregation, through the registered projection.</b> <c>AggregateStreamAsync&lt;T&gt;</c>
///         resolves its aggregator through <c>FisherProjectionOptions.AggregatorFor&lt;T&gt;</c>, which
///         consults the registered projections before falling back to convention — so the fold is the
///         registered projection's own apply methods. It reads the stream in the tenant's own session, so
///         under conjoined events a shared stream id is folded for the tenant asked about.
///     </para>
///     <para>
///         <b>Rendered by <c>StoreOptions.Serializer</c></b>, which is what writes a document's <c>data</c>
///         column, so the result compares byte for byte with <see cref="StoredDocument.Json" /> — casing,
///         enum storage and converters alike. <c>PreviewDocumentJsonAsync</c> renders through the same
///         call for the same reason.
///     </para>
///     <para>
///         <b>Only a top-level registered single-stream projection qualifies.</b> A plain document, a
///         multi-stream projection's document and a document only a composite member produces are refused
///         naming the type: none has one source stream per document, or (inside a composite) a projection
///         whose fold this call could stand in for honestly.
///     </para>
/// </remarks>
public partial class DocumentStore
{
    async Task<string?> IDocumentStoreDiagnostics.AggregateDocumentJsonAsync(string documentTypeName, string id,
        string? tenantId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(id);

        var (mapping, queriedType) = ResolveForDiagnostics(documentTypeName);

        if (mapping is null || queriedType is null)
        {
            throw new ArgumentException(
                $"This Fisher store has no document type named '{documentTypeName}'. Use a name "
                + "IDocumentStoreDiagnostics.DocumentTypesAsync lists.", nameof(documentTypeName));
        }

        if (SingleStreamProjectionFor(queriedType) is null)
        {
            throw new InvalidOperationException(
                $"Documents of type '{queriedType.FullNameInCode()}' are not produced by a registered single-stream "
                + "projection, so there is no one stream to rebuild a document from. Only a type "
                + "DocumentTypesAsync reports with a SingleStreamProjection can be live-aggregated.");
        }

        var streamId = StreamIdFor(id);

        if (streamId is null)
        {
            // A load converts the id the same way and finds nothing for text that cannot be one.
            return null;
        }

        var normalized = DocumentQueryOptions.NormalizeTenantId(tenantId);
        var database = DatabaseForDiagnostics(normalized);
        normalized = normalized is null ? null : Tenancy.TenantIdFor(normalized); // fisher#393

        await using var session = new FisherSession(Options, database,
            new SessionOptions { TenantId = normalized ?? StorageConstants.DefaultTenantId });

        var aggregate = await ((Task<object?>)InvokeClosed(AggregateForDiagnosticsMethod, queriedType, null!,
            [session, streamId, token])!).ConfigureAwait(false);

        return aggregate is null ? null : SerializeForDiagnostics(aggregate);
    }

    /// <summary>
    ///     The registered top-level single-stream projection whose aggregate is exactly
    ///     <paramref name="documentType" />, or null (jasperfx#1082).
    /// </summary>
    internal IAggregateProjection? SingleStreamProjectionFor(Type documentType)
        => Options.Projections.All
            .OfType<IAggregateProjection>()
            .FirstOrDefault(x => x.Scope == AggregationScope.SingleStream && x.AggregateType == documentType);

    /// <summary>The name <see cref="DocumentTypeRef.SingleStreamProjection" /> reports.</summary>
    internal string? SingleStreamProjectionNameFor(Type documentType)
        => SingleStreamProjectionFor(documentType) is { } projection
            ? Options.Projections.All.First(x => ReferenceEquals(x, projection)).Name
            : null;

    /// <summary>
    ///     The stream a console's document id names, as the configured stream identity holds it — or null
    ///     when the text cannot be one.
    /// </summary>
    private object? StreamIdFor(string id)
        => Options.EventGraph.StreamIdentity == StreamIdentity.AsGuid
            ? Guid.TryParse(id, out var guid) ? guid : null
            : id;

    private static readonly MethodInfo AggregateForDiagnosticsMethod =
        typeof(DocumentStore).GetMethod(nameof(AggregateForDiagnosticsAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static async Task<object?> AggregateForDiagnosticsAsync<T>(FisherSession session, object streamId,
        CancellationToken token) where T : class
        => streamId is Guid guid
            ? await session.Events.AggregateStreamAsync<T>(guid, token: token).ConfigureAwait(false)
            : await session.Events.AggregateStreamAsync<T>((string)streamId, token: token).ConfigureAwait(false);

    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification =
            "Serializes a mapped projection document with the store's own serializer, as every document write does. Mapped types are preserved by their registration on the caller side per the AOT publishing guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "See the trimming justification above.")]
    private string SerializeForDiagnostics(object document) => Options.Serializer.ToJson(document);
}
