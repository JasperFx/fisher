using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Fisher.Internal;
using Fisher.Linq;
using Fisher.Linq.SoftDeletes;
using Fisher.Linq.SqlGeneration;
using Fisher.Storage;
using JasperFx;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Documents;
using JasperFx.Events;
using JasperFx.Linq;
using Microsoft.Data.Sqlite;

namespace Fisher;

/// <summary>
///     The document-browsing surface a monitoring console uses (fisher#44), grown to jasperfx#870's
///     contract (fisher#364) — list the mapped types, page their rows as raw JSON with metadata, load one
///     by id and tenant, and (through <see cref="IDocumentStoreDiagnosticsWriter" />) save or delete one.
/// </summary>
/// <remarks>
///     <para>
///         Implemented <b>explicitly</b>, like every other tooling surface on this store, so it does
///         not crowd <see cref="IDocumentStore" />.
///     </para>
///     <para>
///         <b>Without criteria this is a hand-built read, and that makes it a fourth caller of the three
///         implicit filters</b> — the shape fisher#51 warns about. The console names a type as a string
///         and filters on <em>columns</em> (correlation id, causation id, last-modified-by) that are not
///         document members, and it wants the stored row back — byte-exact <c>data</c> plus every
///         metadata column — rather than a materialized document. The mitigation is that each filter is
///         composed from the single place that owns it — <see cref="SoftDelete.NotDeletedSql" />,
///         <see cref="DocumentHierarchy.FilterSqlFor" />, the tenant column — rather than re-spelled
///         here, and <c>diagnostics_reads_carry_the_implicit_filters</c> pins all three.
///     </para>
///     <para>
///         <b>With <see cref="DocumentQueryOptions.Where" /> or <see cref="DocumentQueryOptions.OrderBy" />
///         (jasperfx#869) the LINQ provider builds the statement and this read only chooses its
///         columns.</b> The text is Dynamic LINQ composed onto <c>Query&lt;T&gt;()</c> for the requested
///         type by <see cref="DocumentQueryCriteria.ApplyCriteriaTo{T}" /> — never spliced into SQL —
///         so member casing, enum storage, decimal binding and dates are translated exactly as an
///         application's own query is, and a sub-class name narrows through the provider's own
///         hierarchy pass. The provider hands back its <c>Statement</c> unexecuted
///         (<c>FisherQueryProvider.DiagnosticStatement</c>); the read then
///     </para>
///     <list type="bullet">
///         <item>replaces the select list with the same stored-row columns the hand-built read selects
///         (<see cref="StoredRowLayout" />), so the JSON is still byte-exact and the metadata is
///         still there;</item>
///         <item>adds <see cref="DocumentQueryOptions.IdEquals" /> and the three metadata filters as
///         parameterised terms from the same <see cref="DiagnosticFilter" /> the hand-built read renders,
///         so there is one list of "what the console asked for" and two renderings of it;</item>
///         <item>breaks ties in a given ordering by the identity, and orders by <c>id</c> when the text
///         gave no ordering, so a page is a page either way;</item>
///         <item>counts with the same statement, select list replaced by <c>count(*)</c>, so the total
///         reflects the criteria.</item>
///     </list>
///     <para>
///         <b>Why not "LINQ for the ids, then the row read for those ids"</b>, the other obvious shape:
///         the metadata filters would then apply <em>after</em> paging, so a page could come back short
///         and the total would count rows the filters exclude. One statement carrying every predicate is
///         the only way the page, the order and the total agree. And why not the provider's own operators
///         for everything: soft deletes are (<c>MaybeDeleted()</c>), but there is no LINQ operator for the
///         correlation, causation or last-modified-by columns, and adding three public ones to serve one
///         tooling read would be the wrong trade.
///     </para>
///     <para>
///         The three implicit filters on this path are the provider's statement-level passes rather than
///         this file's — the same single owners, reached the way every <c>Query&lt;T&gt;()</c> reaches
///         them — and <c>criteria_reads_carry_the_implicit_filters</c> pins them beside the hand-built
///         read's test. A shape the provider renders <em>silently wrong</em> is refused up front by
///         <see cref="CriteriaPolicy" />; a shape it cannot translate throws when the statement is built,
///         and that becomes the contract's <see cref="DocumentCriteriaNotSupportedException" />.
///     </para>
/// </remarks>
public partial class DocumentStore : IDocumentStoreDiagnostics, IDocumentStoreDiagnosticsWriter
{
    /// <remarks>
    ///     jasperfx#870 §2: the same value <see cref="IDocumentStoreUsageSource.Subject" /> answers with,
    ///     so a console can pair the two without a lookup table. That is the STORE's identity since
    ///     fisher#353, which is what separates two stores sharing one file.
    /// </remarks>
    Uri IDocumentStoreDiagnostics.Subject => ((IDocumentStoreUsageSource)this).Subject;

    /// <inheritdoc cref="IDocumentStoreDiagnostics.Subject" />
    Uri IDocumentStoreDiagnosticsWriter.Subject => ((IDocumentStoreUsageSource)this).Subject;

    /// <remarks>
    ///     Every mapped type, whether or not its table exists — a document table is created on demand
    ///     at first write, so a registered type with no rows yet is still one the console should offer
    ///     in its picker.
    ///     <para>
    ///         <b>A registered sub-class is listed too, right after its root and naming it</b>
    ///         (jasperfx#932). It has no mapping of its own — fisher#17 — so it would otherwise be
    ///         invisible to a picker, while <see cref="ResolveForDiagnostics" /> already accepts its name
    ///         and narrows to its rows. Its alias is the <c>doc_type</c> discriminator rather than a
    ///         table alias, which is Polecat's answer too: the table is the root's, named on the root's
    ///         entry.
    ///     </para>
    ///     <para>
    ///         <b><see cref="DocumentTypeRef.IsSoftDeleted" /> is the mapping's own delete style</b>
    ///         (jasperfx#1070) — the same <see cref="DocumentMapping.IsSoftDeleted" /> that
    ///         <c>DeleteDocumentAsync</c> and <c>UndeleteDocumentAsync</c> act on, so the type list can
    ///         never promise a delete style the writer then does not deliver.
    ///     </para>
    /// </remarks>
    Task<IReadOnlyList<DocumentTypeRef>> IDocumentStoreDiagnostics.DocumentTypesAsync(CancellationToken token)
    {
        var refs = new List<DocumentTypeRef>();

        foreach (var mapping in MaterializeMappings().OrderBy(x => x.DocumentType.Name, StringComparer.Ordinal))
        {
            var rootTypeName = mapping.DocumentType.FullNameInCode();
            refs.Add(new DocumentTypeRef(rootTypeName, mapping.Alias, Options.DatabaseSchemaName)
            {
                IsSoftDeleted = mapping.IsSoftDeleted,

                // jasperfx#1082: the projection AggregateDocumentJsonAsync folds through. A sub-class is
                // never a single-stream projection's aggregate, so it is set on roots only.
                SingleStreamProjection = SingleStreamProjectionNameFor(mapping.DocumentType)
            });

            // A sub-class reports its ROOT's delete style (jasperfx#1070): its rows are the root's
            // table's rows, and DeleteDocumentAsync deletes them through the root's storage.
            refs.AddRange(mapping.SubClasses
                .OrderBy(x => x.DocumentType.Name, StringComparer.Ordinal)
                .Select(x => new DocumentTypeRef(
                    x.DocumentType.FullNameInCode(), x.Alias, Options.DatabaseSchemaName)
                {
                    RootTypeName = rootTypeName,
                    IsSoftDeleted = mapping.IsSoftDeleted
                }));
        }

        return Task.FromResult<IReadOnlyList<DocumentTypeRef>>(refs);
    }

    /// <remarks>
    ///     <para>
    ///         <b>A table that does not exist reports an empty page rather than failing.</b> SQLite
    ///         resolves a table name when it <em>prepares</em> a statement, so a query against a type
    ///         whose table has never been created fails before any guard in the SQL could run — the
    ///         same lesson rebuild teardown and <c>CleanAsync&lt;T&gt;</c> both learned. A console
    ///         browsing a freshly-migrated store meets this on its first click.
    ///     </para>
    ///     <para>
    ///         The three metadata filters are refused with <see cref="DocumentCriteriaNotSupportedException" />
    ///         on a type that does not persist the column (jasperfx#1077) — they used to be ignored, which
    ///         returned the unfiltered page as though it were the filtered one.
    ///     </para>
    ///     <para>
    ///         <see cref="DocumentQueryOptions.AllTenants" /> with a named tenant is refused before
    ///         anything else: a read cannot be scoped to one tenant and to all of them, and the store
    ///         must not pick. An unknown type is an empty page with or without criteria — there is no
    ///         type to parse the text against, and the reference implementation answers the same.
    ///     </para>
    ///     <para>
    ///         Criteria go down their own path (<see cref="QueryWithCriteriaAsync" />); the path below is
    ///         the hand-built read and is unchanged by them.
    ///     </para>
    /// </remarks>
    async Task<DocumentQueryResult> IDocumentStoreDiagnostics.QueryDocumentsAsync(
        string documentTypeName, DocumentQueryOptions options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AssertValidTenantScope();

        var pageNumber = Math.Max(1, options.PageNumber);
        var pageSize = Math.Max(1, options.PageSize);
        var empty = new DocumentQueryResult(Array.Empty<StoredDocument>(), 0, pageNumber, pageSize);

        var (mapping, queriedType) = ResolveForDiagnostics(documentTypeName);

        if (mapping is null || queriedType is null)
        {
            return empty;
        }

        // Refused, if it is going to be, before anything is read: whether a column can be filtered or
        // ordered by is a fact about the type, not about whether it has rows yet.
        AssertMetadataFiltersAreCaptured(mapping, queriedType, options);
        var metadataOrder = MetadataOrderFor(mapping, options);

        if (options.HasCriteria())
        {
            return await QueryWithCriteriaAsync(mapping, queriedType, options, pageNumber, pageSize, token)
                .ConfigureAwait(false);
        }

        if (options.AllTenants)
        {
            return await QueryEveryTenantAsync(mapping, queriedType, options, pageNumber, pageSize, token)
                .ConfigureAwait(false);
        }

        var tenantId = DocumentQueryOptions.NormalizeTenantId(options.TenantId);
        var database = DatabaseForDiagnostics(tenantId);
        tenantId = tenantId is null ? null : Tenancy.TenantIdFor(tenantId); // fisher#393

        await using var connection = await database.OpenConnectionAsync(token).ConfigureAwait(false);

        if (!await TableExistsAsync(connection, mapping, token).ConfigureAwait(false))
        {
            return empty;
        }

        var (where, bind) = FilterFor(mapping, queriedType, tenantId, options).Build();

        long total;

        await using (var counting = connection.CreateCommand())
        {
            counting.CommandText = $"select count(*) from {mapping.QuotedTableName}{where}";
            bind(counting);

            total = Convert.ToInt64(await counting.ExecuteScalarAsync(token).ConfigureAwait(false));
        }

        // Ordered by id so paging is stable — a page of an unordered SELECT is not a page. The id is
        // the primary key, so this costs nothing. A metadata ordering goes in front of it (jasperfx#1058).
        var documents = await ReadStoredDocumentsAsync(connection, null, mapping, tenantId, where, bind,
            PageTail(metadataOrder), command =>
            {
                command.Parameters.AddWithValue("$take", pageSize);
                command.Parameters.AddWithValue("$skip", (pageNumber - 1) * pageSize);
            }, token).ConfigureAwait(false);

        return new DocumentQueryResult(documents, total, pageNumber, pageSize);
    }

    /// <summary>
    ///     The column <see cref="DocumentQueryOptions.OrderByMetadata" /> orders by, or null when none was
    ///     asked for (jasperfx#1058, fisher#471).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>last_modified</c> is on every table and is <c>SqliteTimestamp</c>'s fixed-width UTC text,
    ///         so ordering it as text is ordering it by instant. <c>created_at</c> exists only where the
    ///         type tracks it, and a version orders only as a numeric <c>revision</c>.
    ///     </para>
    ///     <para>
    ///         <b>Refused rather than ignored</b> for the two it cannot answer, naming the criterion as the
    ///         contract asks: a <c>guid_version</c> is random and the content hash a type with no version
    ///         column gets (<see cref="ContentVersion" />) is not stored at all, so neither has an order to
    ///         give, and a type that does not track <c>created_at</c> has no column to read. Answering in
    ///         id order would look like an answer.
    ///     </para>
    /// </remarks>
    private static (string Locator, bool Descending)? MetadataOrderFor(DocumentMapping mapping,
        DocumentQueryOptions options)
    {
        if (options.OrderByMetadata is not { } order)
        {
            return null;
        }

        var metadata = mapping.Metadata;

        var column = order.Field switch
        {
            DocumentMetadataField.LastModified => metadata.LastModified.Name,
            DocumentMetadataField.Created when metadata.CreatedAt.Enabled => metadata.CreatedAt.Name,
            DocumentMetadataField.Created => throw new DocumentCriteriaNotSupportedException(
                nameof(DocumentQueryOptions.OrderByMetadata),
                $"'{mapping.DocumentType.FullNameInCode()}' does not track a created timestamp, so there is no "
                + "created_at column to order by. Enable it with Schema.For<T>().Metadata(m => m.CreatedAt.Enabled = true) "
                + "or order by DocumentMetadataField.LastModified."),
            DocumentMetadataField.Version when mapping.UseNumericRevisions => metadata.Revision.Name,
            DocumentMetadataField.Version => throw new DocumentCriteriaNotSupportedException(
                nameof(DocumentQueryOptions.OrderByMetadata),
                mapping.UseOptimisticConcurrency
                    ? $"'{mapping.DocumentType.FullNameInCode()}' versions with a Guid, which has no meaningful "
                      + "order. Only a numeric revision (UseNumericRevisions()) can be ordered by."
                    : $"'{mapping.DocumentType.FullNameInCode()}' stores no version column; its version is a "
                      + "content hash derived when the row is read, which has no order. Only a numeric revision "
                      + "(UseNumericRevisions()) can be ordered by."),
            _ => throw new DocumentCriteriaNotSupportedException(nameof(DocumentQueryOptions.OrderByMetadata),
                $"Fisher does not know the metadata field '{order.Field}'.")
        };

        return (column, order.Descending);
    }

    /// <summary>
    ///     jasperfx#1077: a correlation, causation or last-modified-by filter on a type whose table does not
    ///     carry the column is refused, through the shared wording every store uses.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         It used to be skipped — <see cref="DiagnosticFilter.RequestedTerms" /> gated each term on the
    ///         column being enabled, because a term on a missing column is <c>no such column</c> — and the
    ///         page and total that came back were the unfiltered ones, which a console renders as a filter
    ///         that matched everything.
    ///     </para>
    ///     <para>
    ///         The flags are <paramref name="mapping" />'s, which is the hierarchy ROOT's for a sub-class:
    ///         a sub-class has no table of its own, so its columns are its root's.
    ///     </para>
    /// </remarks>
    private static void AssertMetadataFiltersAreCaptured(DocumentMapping mapping, Type queriedType,
        DocumentQueryOptions options)
        => options.AssertMetadataFiltersAreCaptured(queriedType.FullNameInCode(),
            mapping.Metadata.CorrelationId.Enabled,
            mapping.Metadata.CausationId.Enabled,
            mapping.Metadata.LastModifiedBy.Enabled);

    /// <summary>The hand-built reads' <c>order by</c>: the metadata key if any, then the identity.</summary>
    private static string OrderByClause((string Locator, bool Descending)? metadataOrder)
        => metadataOrder is { } order
            ? $"{order.Locator}{(order.Descending ? " desc" : "")}, id"
            : "id";

    private static DiagnosticFilter FilterFor(DocumentMapping mapping, Type queriedType, string? tenantId,
        DocumentQueryOptions options, bool everyTenant = false)
        => new(mapping, queriedType, tenantId)
        {
            EveryTenant = everyTenant,
            IdEquals = options.IdEquals,
            IncludeSoftDeleted = options.IncludeSoftDeleted,
            CorrelationId = options.CorrelationId,
            CausationId = options.CausationId,
            LastModifiedBy = options.LastModifiedBy
        };

    /// <summary>
    ///     <see cref="DocumentQueryOptions.AllTenants" /> (jasperfx#928, fisher#368): every tenant's rows
    ///     of one type, each carrying its own <see cref="StoredDocument.TenantId" />, paged tenant first
    ///     and then by id.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Fisher does have database-per-tenant, so this fans out</b> — fisher#368 said otherwise,
    ///         and a read of the default file alone is exactly the "default tenant's rows as though they
    ///         were every tenant's" answer the contract names as the one wrong one. Under a single file the
    ///         fan-out is one database and conjoined tenancy is simply no tenant predicate; a
    ///         single-tenanted type then reads as the default, because its rows belong to it.
    ///     </para>
    ///     <para>
    ///         <b>Paged by segment rather than by one <c>order by tenant_id, id</c></b>, so one code path
    ///         serves a single file, a file per tenant and several tenants sharing a file (fisher#252).
    ///         Each database reports how many matching rows each of its tenants has; the segments are
    ///         ordered by tenant id in .NET — so the order does not depend on which file a tenant lives
    ///         in — and only the segments the requested page overlaps are read, each ordered by id. Exact
    ///         for every page, and a page never repeats a row from another tenant's.
    ///     </para>
    ///     <para>
    ///         The tenant order is ordinal, deliberately not SQLite's: a merge across files cannot lean on
    ///         one file's collation, and <c>order by id</c> within a segment is SQL's because an integer id
    ///         has to sort numerically.
    ///     </para>
    /// </remarks>
    private async Task<DocumentQueryResult> QueryEveryTenantAsync(DocumentMapping mapping, Type queriedType,
        DocumentQueryOptions options, int pageNumber, int pageSize, CancellationToken token)
    {
        IReadOnlyList<FisherDatabase> databases;
        if (Tenancy.Cardinality == DatabaseCardinality.Single)
        {
            databases = [Database];
        }
        else
        {
            // A tenant nothing has resolved yet still has rows to show.
            await RefreshTenantsAsync(token).ConfigureAwait(false);
            databases = Tenancy.AllDatabases();
        }

        var segments = new List<(FisherDatabase Database, string Tenant, long Count)>();

        foreach (var database in databases)
        {
            await using var connection = await database.OpenConnectionAsync(token).ConfigureAwait(false);

            if (!await TableExistsAsync(connection, mapping, token).ConfigureAwait(false))
            {
                continue;
            }

            var (where, bind) = FilterFor(mapping, queriedType, null, options, everyTenant: true).Build();

            await using var counting = connection.CreateCommand();
            bind(counting);

            if (mapping.IsConjoined)
            {
                counting.CommandText =
                    $"select {StorageConstants.TenantIdColumn}, count(*) from {mapping.QuotedTableName}{where} group by {StorageConstants.TenantIdColumn}";

                await using var reader = await counting.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    segments.Add((database, reader.GetString(0), reader.GetInt64(1)));
                }
            }
            else
            {
                // A single-tenanted type's rows belong to the file's tenant, or to the default tenant
                // when the file is the store's one database.
                counting.CommandText = $"select count(*) from {mapping.QuotedTableName}{where}";
                var count = Convert.ToInt64(await counting.ExecuteScalarAsync(token).ConfigureAwait(false));

                if (count > 0)
                {
                    segments.Add((database, database.TenantId ?? StorageConstants.DefaultTenantId, count));
                }
            }
        }

        segments.Sort((x, y) =>
        {
            var byTenant = string.CompareOrdinal(x.Tenant, y.Tenant);
            return byTenant != 0 ? byTenant : string.CompareOrdinal(x.Database.Identifier, y.Database.Identifier);
        });

        var total = segments.Sum(x => x.Count);
        var skip = (long)(pageNumber - 1) * pageSize;
        var remaining = pageSize;
        var documents = new List<StoredDocument>();

        foreach (var segment in segments)
        {
            if (remaining == 0)
            {
                break;
            }

            if (skip >= segment.Count)
            {
                skip -= segment.Count;
                continue;
            }

            await using var connection = await segment.Database.OpenConnectionAsync(token).ConfigureAwait(false);

            var (where, bind) = FilterFor(mapping, queriedType, segment.Tenant, options).Build();
            var take = remaining;
            var offset = skip;

            var rows = await ReadStoredDocumentsAsync(connection, null, mapping, segment.Tenant, where, bind,
                PageTail(MetadataOrderFor(mapping, options)), command =>
                {
                    command.Parameters.AddWithValue("$take", take);
                    command.Parameters.AddWithValue("$skip", offset);
                }, token).ConfigureAwait(false);

            documents.AddRange(rows);
            remaining -= rows.Count;
            skip = 0;
        }

        return new DocumentQueryResult(documents, total, pageNumber, pageSize);
    }

    // ------------------------------------------------------------------ criteria (jasperfx#869)

    /// <summary>
    ///     A page read with <see cref="DocumentQueryOptions.Where" /> / <see cref="DocumentQueryOptions.OrderBy" />
    ///     — see the class remarks for the mechanism and why it is this one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The text is composed and the statement built before anything is read</b>, so a parse
    ///         failure, an allow-list or <see cref="CriteriaPolicy" /> refusal, or a shape the provider
    ///         cannot translate is a refusal whether or not the type has a table or any rows. Neither step
    ///         touches a connection.
    ///     </para>
    ///     <para>
    ///         <b><see cref="DocumentQueryOptions.AllTenants" /> keeps the segment shape of
    ///         <see cref="QueryEveryTenantAsync" /></b> rather than one statement ordered by tenant: the
    ///         criteria read is one session, and a session is one tenant in one file. So each file's tenants
    ///         are counted with the criteria one session at a time, the segments are ordered by tenant
    ///         ordinally exactly as without criteria, and only the segments the page overlaps are read, each
    ///         in the criteria's own order. Tenant first, then the ordering, then the id — the contract's
    ///         order — falls out of that with no ordering of our own beyond the tie-break.
    ///     </para>
    /// </remarks>
    private async Task<DocumentQueryResult> QueryWithCriteriaAsync(DocumentMapping mapping, Type queriedType,
        DocumentQueryOptions options, int pageNumber, int pageSize, CancellationToken token)
    {
        var empty = new DocumentQueryResult(Array.Empty<StoredDocument>(), 0, pageNumber, pageSize);

        if (!options.AllTenants)
        {
            var tenantId = DocumentQueryOptions.NormalizeTenantId(options.TenantId);
            var database = DatabaseForDiagnostics(tenantId);
            tenantId = tenantId is null ? null : Tenancy.TenantIdFor(tenantId); // fisher#393

            await using var read = await OpenCriteriaReadAsync(database, tenantId, mapping, queriedType, options)
                .ConfigureAwait(false);

            if (!await TableExistsAsync(database, mapping, token).ConfigureAwait(false))
            {
                return empty;
            }

            var total = await read.CountAsync(token).ConfigureAwait(false);
            var documents = total == 0
                ? []
                : await read.PageAsync(pageSize, (long)(pageNumber - 1) * pageSize, token).ConfigureAwait(false);

            return new DocumentQueryResult(documents, total, pageNumber, pageSize);
        }

        // Refused, if it is going to be, before the fan-out rather than once per tenant.
        await (await OpenCriteriaReadAsync(Database, null, mapping, queriedType, options).ConfigureAwait(false))
            .DisposeAsync().ConfigureAwait(false);

        IReadOnlyList<FisherDatabase> databases;
        if (Tenancy.Cardinality == DatabaseCardinality.Single)
        {
            databases = [Database];
        }
        else
        {
            await RefreshTenantsAsync(token).ConfigureAwait(false);
            databases = Tenancy.AllDatabases();
        }

        var segments = new List<(FisherDatabase Database, string Tenant, long Count)>();

        foreach (var database in databases)
        {
            if (!await TableExistsAsync(database, mapping, token).ConfigureAwait(false))
            {
                continue;
            }

            var tenants = mapping.IsConjoined
                ? await TenantsInAsync(database, mapping, token).ConfigureAwait(false)
                : [database.TenantId ?? StorageConstants.DefaultTenantId];

            foreach (var tenant in tenants)
            {
                await using var read = await OpenCriteriaReadAsync(database, tenant, mapping, queriedType, options)
                    .ConfigureAwait(false);

                var count = await read.CountAsync(token).ConfigureAwait(false);
                if (count > 0)
                {
                    segments.Add((database, tenant, count));
                }
            }
        }

        segments.Sort((x, y) =>
        {
            var byTenant = string.CompareOrdinal(x.Tenant, y.Tenant);
            return byTenant != 0 ? byTenant : string.CompareOrdinal(x.Database.Identifier, y.Database.Identifier);
        });

        var all = segments.Sum(x => x.Count);
        var skip = (long)(pageNumber - 1) * pageSize;
        var remaining = pageSize;
        var page = new List<StoredDocument>();

        foreach (var segment in segments)
        {
            if (remaining == 0)
            {
                break;
            }

            if (skip >= segment.Count)
            {
                skip -= segment.Count;
                continue;
            }

            await using var read = await OpenCriteriaReadAsync(segment.Database, segment.Tenant, mapping,
                queriedType, options).ConfigureAwait(false);

            var rows = await read.PageAsync(remaining, skip, token).ConfigureAwait(false);

            page.AddRange(rows);
            remaining -= rows.Count;
            skip = 0;
        }

        return new DocumentQueryResult(page, all, pageNumber, pageSize);
    }

    private static async Task<bool> TableExistsAsync(FisherDatabase database, DocumentMapping mapping,
        CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token).ConfigureAwait(false);
        return await TableExistsAsync(connection, mapping, token).ConfigureAwait(false);
    }

    /// <summary>Every tenant a conjoined table in <paramref name="database" /> holds rows for.</summary>
    private static async Task<List<string>> TenantsInAsync(FisherDatabase database, DocumentMapping mapping,
        CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"select distinct {StorageConstants.TenantIdColumn} from {mapping.QuotedTableName}";

        var tenants = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            tenants.Add(reader.GetString(0));
        }

        return tenants;
    }

    /// <summary>
    ///     A session pinned to <paramref name="database" /> and scoped to <paramref name="tenantId" />, with
    ///     the criteria composed onto <c>Query&lt;T&gt;()</c> for the requested type.
    /// </summary>
    /// <remarks>
    ///     Pinned to the file rather than resolved from the tenant, as <c>OpenSessionOn</c> is for the
    ///     daemon: an all-tenants segment is a (file, tenant) pair, and under fisher#252's shared files the
    ///     tenant alone does not have to name the file the segment was counted in.
    /// </remarks>
    private async Task<CriteriaRead> OpenCriteriaReadAsync(FisherDatabase database, string? tenantId,
        DocumentMapping mapping, Type queriedType, DocumentQueryOptions options)
    {
        var session = new FisherSession(Options, database,
            new SessionOptions { TenantId = tenantId ?? StorageConstants.DefaultTenantId });

        try
        {
            var composed = (IQueryable)InvokeClosed(ComposeCriteriaMethod, queriedType, null!,
                [session, options, mapping.IsSoftDeleted && options.IncludeSoftDeleted, mapping.IdMember.Name])!;

            var read = new CriteriaRead(composed, mapping, FilterFor(mapping, queriedType, tenantId, options),
                tenantId, options, session);

            // The provider refuses a shape it cannot translate while it builds the statement, which needs
            // no connection — so that refusal, too, comes before the table is looked for.
            read.Validate();

            return read;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     The Dynamic LINQ shapes Fisher's provider would translate SILENTLY WRONG, refused before they run —
    ///     see <see cref="CriteriaShapeRules" />.
    /// </summary>
    internal static DynamicQueryPolicy CriteriaPolicy => CriteriaShapeRules.Policy;

    private static readonly MethodInfo ComposeCriteriaMethod =
        typeof(DocumentStore).GetMethod(nameof(ComposeCriteria), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    ///     <c>Query&lt;T&gt;()</c> with the criteria composed onto it — nothing executed.
    /// </summary>
    /// <remarks>
    ///     <c>Query&lt;T&gt;()</c> for the requested type, not the root, so a sub-class name narrows through
    ///     the provider's own hierarchy pass and the text can name the sub-class's own members.
    ///     <see cref="DocumentQueryOptions.IncludeSoftDeleted" /> is the provider's
    ///     <c>MaybeDeleted()</c>, which drops its own <c>is_deleted = 0</c> term.
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification =
            "Applies console text to a mapped document type through Dynamic LINQ, which resolves the type's members reflectively. Mapped types are preserved by their registration on the caller side per the AOT publishing guide. Under Native AOT ApplyCriteriaTo refuses with DocumentCriteriaNotSupportedException before it closes or emits anything.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "See the trimming justification above.")]
    private static IQueryable ComposeCriteria<T>(FisherSession session, DocumentQueryOptions options,
        bool maybeDeleted, string tieBreaker) where T : notnull
    {
        var source = session.Query<T>();

        if (maybeDeleted)
        {
            source = source.MaybeDeleted();
        }

        return options.ApplyCriteriaTo(source, tieBreaker, CriteriaPolicy);
    }

    /// <summary>
    ///     One criteria-bearing read: the composed query, the session it belongs to, and the console's
    ///     requested terms to add to whatever statement it builds.
    /// </summary>
    private sealed class CriteriaRead(IQueryable composed, DocumentMapping mapping, DiagnosticFilter filter,
        string? tenantId, DocumentQueryOptions options, FisherSession session) : IAsyncDisposable
    {
        private FisherQueryProvider Provider => (FisherQueryProvider)composed.Provider;

        /// <summary>How many rows the criteria and the console's terms select — the page's total.</summary>
        public async Task<long> CountAsync(CancellationToken token)
        {
            var statement = Build();
            statement.SelectColumns = "count(*)";
            statement.OrderBys.Clear();

            return await TranslatedAsync(() => Provider.ExecuteDiagnosticCountAsync(statement, token))
                .ConfigureAwait(false);
        }

        /// <summary>
        ///     <paramref name="take" /> stored rows after <paramref name="skip" />, in the criteria's order
        ///     tie-broken by the identity, or by the identity alone without an ordering.
        /// </summary>
        public async Task<List<StoredDocument>> PageAsync(int take, long skip, CancellationToken token)
        {
            var layout = new StoredRowLayout(mapping);
            var statement = PageStatement(layout, take, skip);

            await using var reader = await TranslatedAsync(() => Provider.ExecuteDiagnosticReaderAsync(statement, token))
                .ConfigureAwait(false);

            return await layout.ReadAllAsync(reader, tenantId, token).ConfigureAwait(false);
        }

        /// <summary>
        ///     SQLite's plan for exactly the statement <see cref="PageAsync" /> runs (jasperfx#1078) — the
        ///     same builder with <see cref="Statement.Explain" /> set, so the two cannot drift apart.
        /// </summary>
        public async Task<List<ExplainedStep>> ExplainPageAsync(int take, long skip, CancellationToken token)
        {
            var statement = PageStatement(new StoredRowLayout(mapping), take, skip);
            statement.Explain = true;

            await using var reader = await TranslatedAsync(() => Provider.ExecuteDiagnosticReaderAsync(statement, token))
                .ConfigureAwait(false);

            return await ReadExplainedStepsAsync(reader, token).ConfigureAwait(false);
        }

        private Statement PageStatement(StoredRowLayout layout, int take, long skip)
        {
            var statement = Build();
            statement.SelectColumns = layout.Columns;

            // The criteria's ordering already ends in the identity (the tie-breaker handed to
            // ApplyCriteriaTo); a Where with no ordering is ordered by it here, as the hand-built read is.
            if (statement.OrderBys.Count == 0)
            {
                statement.OrderBys.Add(("id", false));
            }

            // jasperfx#1058: the metadata key first, the criteria's ordering breaking its ties, and the
            // identity breaking whatever ties remain.
            if (MetadataOrderFor(mapping, options) is { } metadataOrder)
            {
                statement.OrderBys.Insert(0, metadataOrder);
            }

            statement.Limit = take;
            statement.Offset = checked((int)skip);

            return statement;
        }

        /// <summary>Build the statement and throw it away: the provider's own refusal, without a read.</summary>
        public void Validate() => Build();

        public ValueTask DisposeAsync() => session.DisposeAsync();

        private Statement Build()
        {
            var statement = Translated(() => Provider.DiagnosticStatement(composed.Expression));

            foreach (var (column, value) in filter.RequestedTerms())
            {
                statement.Wheres.Add(new ComparisonFilter(column, "=", value));
            }

            return statement;
        }

        /// <summary>
        ///     The provider refusing a shape — when the statement is built, or when its parameters are bound
        ///     — becomes the contract's refusal. A database failure is not one, and propagates as itself.
        /// </summary>
        private T Translated<T>(Func<T> action)
        {
            try
            {
                return action();
            }
            catch (Exception e) when (DocumentQueryCriteria.IsTranslationFailure(e))
            {
                throw options.Untranslatable(e);
            }
        }

        private async Task<T> TranslatedAsync<T>(Func<Task<T>> action)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception e) when (DocumentQueryCriteria.IsTranslationFailure(e))
            {
                throw options.Untranslatable(e);
            }
        }
    }

    /// <remarks>
    ///     <para>
    ///         A load by id is explicit, so it returns a soft-deleted row flagged with
    ///         <see cref="StoredDocument.IsDeleted" /> rather than hiding it — the one place the
    ///         soft-delete filter is deliberately left off, for the reason <c>MetadataForAsync</c> leaves
    ///         it off: a console asking about one id wants to be told it was deleted.
    ///     </para>
    ///     <para>
    ///         Its own read rather than a one-row page query, which is what <c>LoadDocumentJsonAsync</c>
    ///         used to be — that had no tenant to take, and it inherited the page's soft-delete filter,
    ///         so it could not answer either of the two questions jasperfx#870 added this member for.
    ///         <c>LoadDocumentJsonAsync</c> now forwards here through the contract's own default.
    ///     </para>
    /// </remarks>
    async Task<StoredDocument?> IDocumentStoreDiagnostics.LoadDocumentAsync(
        string documentTypeName, string id, string? tenantId, CancellationToken token)
    {
        var (mapping, queriedType) = ResolveForDiagnostics(documentTypeName);

        if (mapping is null || queriedType is null || id is null)
        {
            return null;
        }

        tenantId = DocumentQueryOptions.NormalizeTenantId(tenantId);
        var database = DatabaseForDiagnostics(tenantId);
        tenantId = tenantId is null ? null : Tenancy.TenantIdFor(tenantId); // fisher#393

        await using var connection = await database.OpenConnectionAsync(token).ConfigureAwait(false);

        if (!await TableExistsAsync(connection, mapping, token).ConfigureAwait(false))
        {
            return null;
        }

        return await LoadStoredDocumentAsync(connection, null, mapping, queriedType, tenantId, id,
            includeSoftDeleted: true, token).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ writes (jasperfx#870 §6)

    /// <remarks>
    ///     <para>
    ///         <b>Through a session, never a raw <c>UPDATE</c> of <c>data</c></b>, as the contract
    ///         requires — so the version moves, <c>last_modified</c> and the session metadata columns are
    ///         stamped, and a duplicated field follows for free (being a generated column over
    ///         <c>data</c>). The document is deserialized with the store's own serializer.
    ///     </para>
    ///     <para>
    ///         <b>The expected version is checked inside Fisher's write transaction, before anything is
    ///         written</b>, and that is why the session is an enlisted one rather than an ordinary save.
    ///         The writer opens the transaction (<c>BEGIN IMMEDIATE</c>, so it holds the file's one write
    ///         lock), reads the current row, and only then decides; no other writer can move the row in
    ///         between. It works whether or not the type opted into optimistic concurrency, as the
    ///         contract asks, because the comparison is against <see cref="StoredDocument.Version" /> —
    ///         a column where the type has one, a content hash where it does not.
    ///     </para>
    ///     <para>
    ///         The whole attempt runs inside <see cref="StoreOptions.ResiliencePipeline" />. That is safe
    ///         here where it is not for an enlisted application session, because the transaction is the
    ///         writer's own: a failed attempt rolled back everything it wrote.
    ///     </para>
    /// </remarks>
    async Task<DocumentWriteResult> IDocumentStoreDiagnosticsWriter.SaveDocumentJsonAsync(
        DocumentWriteRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (mapping, queriedType, document) = ReadWriteRequest(request);

        return await RunDiagnosticWriteAsync(mapping, queriedType, request.Id, request.TenantId,
            request.ExpectedVersion, CurrentRow.Any,
            async (session, current, ct) =>
            {
                AssertIdentityAgrees(session, mapping, queriedType, document, request);

                InvokeClosed(StoreForDiagnostics, queriedType, session, [document, GuidVersionOf(mapping, current)]);

                await session.SaveChangesAsync(ct).ConfigureAwait(false);

                return DocumentWriteStatus.Saved;
            }, token).ConfigureAwait(false);
    }

    /// <remarks>
    ///     <para>
    ///         <b>The save's own steps, minus the database</b> (jasperfx#1071): the same
    ///         <see cref="ReadWriteRequest" /> deserialize and the same <see cref="AssertIdentityAgrees" />
    ///         check <see cref="IDocumentStoreDiagnosticsWriter.SaveDocumentJsonAsync" /> runs, so the two
    ///         refuse exactly the same requests, and then the store's own serializer writes the document back
    ///         out — <c>ToJson</c>, which is what the closed-shape upsert binds into <c>data</c>. That is what
    ///         makes the answer the JSON a save would store rather than a reformatting of the operator's
    ///         text: a member the CLR type does not have is gone, and casing and enum storage are the
    ///         store's.
    ///     </para>
    ///     <para>
    ///         Nothing is read or written: no write lock, no current row, and no
    ///         <see cref="DocumentWriteRequest.ExpectedVersion" /> check, which the contract says to ignore.
    ///         The session is only there to resolve the type's identity strategy for the id check — it is
    ///         never saved, and a Fisher session opens no connection until something needs one.
    ///     </para>
    /// </remarks>
    async Task<string> IDocumentStoreDiagnosticsWriter.PreviewDocumentJsonAsync(DocumentWriteRequest request,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (mapping, queriedType, document) = ReadWriteRequest(request);

        // The database a save would write to, so an unknown tenant is refused here as it is there.
        var tenantId = DocumentQueryOptions.NormalizeTenantId(request.TenantId);
        await using (var session = (FisherSession)OpenSessionOn(DatabaseForDiagnostics(tenantId), tenantId))
        {
            AssertIdentityAgrees(session, mapping, queriedType, document, request);
        }

        return Options.Serializer.ToJson(document);
    }

    /// <summary>
    ///     The first half of a save, shared with its preview: the named type, refused by name when this
    ///     store has none, and the request's JSON read as that type by the store's own serializer.
    /// </summary>
    /// <exception cref="ArgumentException">
    ///     An unknown type name, or JSON the serializer cannot read as the type.
    /// </exception>
    private (DocumentMapping Mapping, Type QueriedType, object Document) ReadWriteRequest(DocumentWriteRequest request)
    {
        var (mapping, queriedType) = ResolveForWrite(request.DocumentTypeName, request.Id);

        try
        {
            return (mapping, queriedType, Deserialize(queriedType, request.Json));
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException)
        {
            throw new ArgumentException(
                $"The JSON for '{request.DocumentTypeName}' '{request.Id}' could not be read as {queriedType.FullNameInCode()}: {e.Message}",
                nameof(request), e);
        }
    }

    /// <summary>
    ///     Refuse a document whose own identity disagrees with the id the request names — through the
    ///     storage's identity strategy, so a strong-typed id and a Guid's casing compare as the column does.
    /// </summary>
    private static void AssertIdentityAgrees(FisherSession session, DocumentMapping mapping, Type queriedType,
        object document, DocumentWriteRequest request)
    {
        var identity = (string)InvokeClosed(IdentityTextFor, queriedType, session, [document])!;

        if (!string.Equals(identity, FisherSession.InvariantText(ConvertIdentity(mapping, request.Id)),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The id in the JSON ('{identity}') does not agree with the requested id '{request.Id}'. "
                + "Refusing rather than guessing which one was meant.", nameof(request));
        }
    }

    /// <remarks>
    ///     A delete through the session, so a soft-deleted type is soft-deleted — the row stays, flagged,
    ///     exactly as an application's <c>Delete</c> leaves it. "No live row" is
    ///     <see cref="DocumentWriteStatus.NotFound" />, which makes deleting an already soft-deleted
    ///     document a not-found rather than a second deletion that would push <c>deleted_at</c> forward.
    /// </remarks>
    async Task<DocumentWriteResult> IDocumentStoreDiagnosticsWriter.DeleteDocumentAsync(
        DocumentDeleteRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (mapping, queriedType) = ResolveForWrite(request.DocumentTypeName, request.Id);

        return await RunDiagnosticWriteAsync(mapping, queriedType, request.Id, request.TenantId,
            request.ExpectedVersion, CurrentRow.Live,
            async (session, current, ct) =>
            {
                // The document the application would have deleted, read back as its own type so a
                // hierarchy row and its identity resolve through the storage's own rules.
                var document = Deserialize(ReadTypeOf(mapping, current!) ?? queriedType, current!.Json);

                InvokeClosed(DeleteDocument, document.GetType(), session, [document]);

                await session.SaveChangesAsync(ct).ConfigureAwait(false);

                return DocumentWriteStatus.Deleted;
            }, token).ConfigureAwait(false);
    }

    /// <remarks>
    ///     <para>
    ///         <b>Through an enlisted session, under the writer's own <c>BEGIN IMMEDIATE</c></b>, exactly as
    ///         a save and a delete are, so the expected version is checked against the row the restore will
    ///         touch. The restore clears <c>is_deleted</c> and <c>deleted_at</c> and moves
    ///         <c>last_modified</c> and the version column, without rewriting <c>data</c> — see
    ///         <c>FisherDocumentStorage.RestoreForId</c>.
    ///     </para>
    ///     <para>
    ///         <b>Only a soft-deleted row is a target.</b> A live row is <see cref="DocumentWriteStatus.NotFound" />
    ///         as surely as a missing one: an undelete that did nothing must not look like one that did. A
    ///         type that hard-deletes answers <see cref="DocumentWriteStatus.NotSupported" /> before anything
    ///         is read, so a console can offer the action per type without guarding the call.
    ///     </para>
    /// </remarks>
    async Task<DocumentWriteResult> IDocumentStoreDiagnosticsWriter.UndeleteDocumentAsync(
        DocumentUndeleteRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (mapping, queriedType) = ResolveForWrite(request.DocumentTypeName, request.Id);

        if (!mapping.IsSoftDeleted)
        {
            return new DocumentWriteResult(DocumentWriteStatus.NotSupported);
        }

        return await RunDiagnosticWriteAsync(mapping, queriedType, request.Id, request.TenantId,
            request.ExpectedVersion, CurrentRow.SoftDeleted,
            async (session, current, ct) =>
            {
                // Only the identity is read off the document, through the root's storage, which owns the
                // table whichever sub-class the row is.
                var document = Deserialize(ReadTypeOf(mapping, current!) ?? queriedType, current!.Json);

                InvokeClosed(UndeleteDocument, mapping.DocumentType, session, [document]);

                await session.SaveChangesAsync(ct).ConfigureAwait(false);

                return DocumentWriteStatus.Undeleted;
            }, token).ConfigureAwait(false);
    }

    /// <summary>Which existing row a diagnostic write acts on, and so what counts as not found.</summary>
    private enum CurrentRow
    {
        /// <summary>A save: a soft-deleted row is still the row it replaces, and no row is an insert.</summary>
        Any,

        /// <summary>A delete: only a live row.</summary>
        Live,

        /// <summary>An undelete: only a soft-deleted row.</summary>
        SoftDeleted
    }

    private static readonly MethodInfo UndeleteDocument =
        typeof(FisherSession).GetMethod(nameof(FisherSession.UndeleteForDiagnostics),
            BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo StoreForDiagnostics =
        typeof(FisherSession).GetMethod(nameof(FisherSession.StoreForDiagnostics),
            BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo IdentityTextFor =
        typeof(FisherSession).GetMethod(nameof(FisherSession.IdentityTextForDiagnostics),
            BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo DeleteDocument =
        typeof(IDocumentOperations).GetMethods()
            .Single(x => x.Name == nameof(IDocumentOperations.Delete) && x.IsGenericMethodDefinition
                         && x.GetParameters() is [{ ParameterType.IsGenericParameter: true }]);

    /// <summary>
    ///     One diagnostic write: take the write lock, read the current row, check the caller's expected
    ///     version against it, and only then hand an enlisted session to <paramref name="write" />.
    /// </summary>
    private async Task<DocumentWriteResult> RunDiagnosticWriteAsync(DocumentMapping mapping, Type queriedType,
        string id, string? tenantId, string? expectedVersion, CurrentRow currentRow,
        Func<FisherSession, StoredDocument?, CancellationToken, Task<DocumentWriteStatus>> write,
        CancellationToken token)
    {
        tenantId = DocumentQueryOptions.NormalizeTenantId(tenantId);
        var database = DatabaseForDiagnostics(tenantId);
        tenantId = tenantId is null ? null : Tenancy.TenantIdFor(tenantId); // fisher#393

        // Before the transaction, because it is a migration on its own connection — the enlisted
        // session asserts rather than creating for exactly that reason, and would otherwise refuse a
        // console's first save to a type nothing has written yet. AutoCreate.None is honoured there.
        await database.EnsureDocumentTableAsync(mapping.DocumentType, token).ConfigureAwait(false);

        return await Options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await database.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);

            var current = await LoadStoredDocumentAsync(connection, transaction, mapping, queriedType, tenantId,
                id, includeSoftDeleted: currentRow != CurrentRow.Live, ct).ConfigureAwait(false);

            // A soft-deleted row is still the row a save replaces — storing a soft-deleted document
            // undeletes it, as it does for an application — but it is not a live one to delete, and a
            // live row is not one to undelete.
            var notFound = currentRow switch
            {
                CurrentRow.Live => current is null,
                CurrentRow.SoftDeleted => current is not { IsDeleted: true },
                _ => false
            };

            if (notFound)
            {
                return new DocumentWriteResult(DocumentWriteStatus.NotFound);
            }

            if (expectedVersion is not null && (current is null
                                                || !string.Equals(current.Version, expectedVersion,
                                                    StringComparison.OrdinalIgnoreCase)))
            {
                // Nothing written, so there is nothing to roll back beyond releasing the lock.
                return new DocumentWriteResult(DocumentWriteStatus.ConcurrencyConflict, current);
            }

            var options = SessionOptions.ForTransaction(transaction);
            options.TenantId = tenantId ?? StorageConstants.DefaultTenantId;

            DocumentWriteStatus status;
            await using (var session = (FisherSession)OpenSession(options))
            {
                status = await write(session, current, ct).ConfigureAwait(false);
            }

            var written = status is DocumentWriteStatus.Saved or DocumentWriteStatus.Undeleted
                ? await LoadStoredDocumentAsync(connection, transaction, mapping, queriedType, tenantId, id,
                    includeSoftDeleted: true, ct).ConfigureAwait(false)
                : null;

            await transaction.CommitAsync(ct).ConfigureAwait(false);

            return new DocumentWriteResult(status, written);
        }, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     The type a write names, refused by name when this store has no such type — where a read
    ///     answers empty. A write that did nothing must not look like one that succeeded.
    /// </summary>
    private (DocumentMapping Mapping, Type QueriedType) ResolveForWrite(string documentTypeName, string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        var (mapping, queriedType) = ResolveForDiagnostics(documentTypeName);

        if (mapping is null || queriedType is null)
        {
            throw new ArgumentException(
                $"This Fisher store has no document type named '{documentTypeName}'. Use a name "
                + "IDocumentStoreDiagnostics.DocumentTypesAsync lists.", nameof(documentTypeName));
        }

        return (mapping, queriedType);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification =
            "Deserializes a mapped document type with the store's own serializer, as every document load does. Mapped types are preserved by their registration on the caller side per the AOT publishing guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "See the trimming justification above.")]
    private object Deserialize(Type type, string json) => Options.Serializer.FromJson(type, json);

    /// <summary>
    ///     Close one of the writer's generic session members over the document type a console named,
    ///     and rethrow what it threw rather than the reflection wrapper around it — an
    ///     <see cref="ArgumentException" /> or a <c>ConcurrencyException</c> has to reach the caller as
    ///     itself.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2060:MakeGenericMethod",
        Justification =
            "Closes a session member over a mapped document type named by the console. Mapped types are preserved by their registration on the caller side per the AOT publishing guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "See the trimming justification above.")]
    private static object? InvokeClosed(MethodInfo open, Type type, object target, object?[] arguments)
    {
        try
        {
            return open.MakeGenericMethod(type).Invoke(target, arguments);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private static Guid? GuidVersionOf(DocumentMapping mapping, StoredDocument? current)
        => mapping.UseOptimisticConcurrency && current?.Version is { } text && Guid.TryParse(text, out var version)
            ? version
            : null;

    // ------------------------------------------------------------------ the shared read

    /// <summary>
    ///     The database a tenant's documents live in: the tenant's own file under database-per-tenant,
    ///     the one file otherwise.
    /// </summary>
    /// <remarks>
    ///     jasperfx#870 §3. This read used <see cref="Database" /> unconditionally, so a
    ///     database-per-tenant store answered every tenant from the default file. An unknown tenant
    ///     throws out of the tenancy rather than falling back — the rule every tenant-scoped member
    ///     follows, because falling back would answer about somebody else's data.
    /// </remarks>
    private FisherDatabase DatabaseForDiagnostics(string? tenantId)
        => tenantId is null ? Database : Tenancy.ExistingDatabaseFor(tenantId);

    private async Task<StoredDocument?> LoadStoredDocumentAsync(SqliteConnection connection,
        SqliteTransaction? transaction, DocumentMapping mapping, Type queriedType, string? tenantId, string id,
        bool includeSoftDeleted, CancellationToken token)
    {
        var (where, bind) = new DiagnosticFilter(mapping, queriedType, tenantId)
        {
            IdEquals = id,
            IncludeSoftDeleted = includeSoftDeleted
        }.Build();

        var rows = await ReadStoredDocumentsAsync(connection, transaction, mapping, tenantId, where, bind,
            " limit 1", _ => { }, token).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    ///     Rows of <paramref name="mapping" />'s table as <see cref="StoredDocument" />s, with every
    ///     metadata column the table carries.
    /// </summary>
    /// <remarks>
    ///     Columns are chosen from the mapping rather than selected blindly, as
    ///     <c>MetadataForAsync</c>'s are: a document table carries only the columns its type asked
    ///     for, and naming one it lacks is <c>no such column</c> rather than a null.
    /// </remarks>
    private async Task<List<StoredDocument>> ReadStoredDocumentsAsync(SqliteConnection connection,
        SqliteTransaction? transaction, DocumentMapping mapping, string? tenantId, string where,
        Action<SqliteCommand> bindWhere, string tail, Action<SqliteCommand> bindTail, CancellationToken token)
    {
        var layout = new StoredRowLayout(mapping);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = StoredRowsSql(layout, mapping, where, tail);
        bindWhere(command);
        bindTail(command);

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await layout.ReadAllAsync(reader, tenantId, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     The hand-built read's statement — one spelling, so <c>ExplainDocumentQueryAsync</c> explains
    ///     exactly what <c>QueryDocumentsAsync</c> runs (jasperfx#1078).
    /// </summary>
    private static string StoredRowsSql(StoredRowLayout layout, DocumentMapping mapping, string where, string tail)
        => $"select {layout.Columns} from {mapping.QuotedTableName}{where}{tail}";

    /// <summary>The hand-built page read's tail: the ordering, then the page.</summary>
    private static string PageTail((string Locator, bool Descending)? metadataOrder)
        => $" order by {OrderByClause(metadataOrder)} limit $take offset $skip";

    /// <summary>
    ///     The columns a <see cref="StoredDocument" /> is read from, and the read — one definition for the
    ///     hand-built read and the criteria read alike, so the two cannot disagree about what a row is.
    /// </summary>
    private sealed class StoredRowLayout
    {
        private readonly DocumentMapping _mapping;
        private readonly int _versionOrdinal;
        private readonly int _revisionOrdinal;
        private readonly int _createdOrdinal;
        private readonly int _tenantOrdinal;
        private readonly int _deletedOrdinal;
        private readonly int _deletedAtOrdinal;
        private readonly int _docTypeOrdinal;

        public StoredRowLayout(DocumentMapping mapping)
        {
            _mapping = mapping;

            var metadata = mapping.Metadata;
            var columns = new List<string> { "id", "data", metadata.LastModified.Name };

            int Add(bool present, string column)
            {
                if (!present) return -1;
                columns.Add(column);
                return columns.Count - 1;
            }

            _versionOrdinal = Add(mapping.UseOptimisticConcurrency, metadata.Version.Name);
            _revisionOrdinal = Add(mapping.UseNumericRevisions, metadata.Revision.Name);
            _createdOrdinal = Add(metadata.CreatedAt.Enabled, metadata.CreatedAt.Name);
            _tenantOrdinal = Add(mapping.IsConjoined, StorageConstants.TenantIdColumn);
            _deletedOrdinal = Add(mapping.IsSoftDeleted, SoftDelete.IsDeletedColumn);
            _deletedAtOrdinal = Add(mapping.IsSoftDeleted, SoftDelete.DeletedAtColumn);
            _docTypeOrdinal = Add(mapping.IsHierarchy, DocumentHierarchy.DocTypeColumn);

            Columns = string.Join(", ", columns);
        }

        /// <summary>The select list, unqualified — a diagnostics read never joins.</summary>
        public string Columns { get; }

        /// <param name="tenantId">
        ///     The tenant the read was for. A single-tenanted type reports it — the file's own tenant
        ///     under database-per-tenant — or the default, which is what the contract says a
        ///     single-tenanted row belongs to.
        /// </param>
        public async Task<List<StoredDocument>> ReadAllAsync(DbDataReader reader, string? tenantId,
            CancellationToken token)
        {
            var readTenant = tenantId ?? StorageConstants.DefaultTenantId;
            var documents = new List<StoredDocument>();

            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                documents.Add(Read(reader, readTenant));
            }

            return documents;
        }

        private StoredDocument Read(DbDataReader reader, string readTenant)
        {
            // Byte-exact, as fisher#28's JSON reads are and for the same reason: data holds precisely
            // what the serializer wrote, so a console shows the document rather than a re-rendering.
            var json = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var lastModified = reader.IsDBNull(2) ? null : reader.GetString(2);

            var docType = _docTypeOrdinal >= 0 && !reader.IsDBNull(_docTypeOrdinal)
                ? reader.GetString(_docTypeOrdinal)
                : null;

            var deletedAt = _deletedAtOrdinal >= 0 && !reader.IsDBNull(_deletedAtOrdinal)
                ? reader.GetString(_deletedAtOrdinal)
                : null;

            return new StoredDocument(FisherSession.InvariantText(reader.GetValue(0)), json)
            {
                // guid_version is rendered through Guid rather than returned as stored: Weasel's version
                // binder writes the raw Guid, which Microsoft.Data.Sqlite spells UPPERCASE, while every
                // other Guid a console sees from Fisher is lowercase canonical. An opaque token is still
                // one a console will display and paste back.
                Version = _versionOrdinal >= 0 && !reader.IsDBNull(_versionOrdinal)
                    ? CanonicalVersion(reader.GetString(_versionOrdinal))
                    : _revisionOrdinal >= 0 && !reader.IsDBNull(_revisionOrdinal)
                        ? reader.GetInt64(_revisionOrdinal).ToString(CultureInfo.InvariantCulture)
                        : ContentVersion(lastModified, json, deletedAt),
                LastModified = lastModified is null ? null : SqliteTimestamp.FromDatabaseValue(lastModified),
                Created = TimestampAt(reader, _createdOrdinal),
                TenantId = _tenantOrdinal >= 0 && !reader.IsDBNull(_tenantOrdinal)
                    ? reader.GetString(_tenantOrdinal)
                    : readTenant,
                IsDeleted = _deletedOrdinal >= 0 && !reader.IsDBNull(_deletedOrdinal)
                                                 && reader.GetInt64(_deletedOrdinal) != 0,
                DeletedAt = TimestampAt(reader, _deletedAtOrdinal),
                DocumentType = (TypeForAlias(_mapping, docType) ?? _mapping.DocumentType).FullNameInCode()
            };
        }
    }

    private static string CanonicalVersion(string stored)
        => Guid.TryParse(stored, out var version) ? version.ToString("D") : stored;

    private static DateTimeOffset? TimestampAt(DbDataReader reader, int ordinal)
        => ordinal >= 0 && !reader.IsDBNull(ordinal) ? SqliteTimestamp.FromDatabaseValue(reader.GetString(ordinal)) : null;

    /// <summary>
    ///     The opaque version of a row whose type has no version column — neither
    ///     <c>UseOptimisticConcurrency()</c> nor numeric revisions.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The contract wants a token that changes on every write whether or not the type opted into
    ///         concurrency, so a console's guarded edit is guarded anyway. <c>last_modified</c> alone
    ///         would be the obvious one and is not enough: it is millisecond-precision, and two writes
    ///         inside one millisecond would hand a console the same version for different documents.
    ///         Hashing it with <c>data</c> closes that — two writes that agree on both are the same
    ///         document, so a shared token is honest.
    ///     </para>
    ///     <para>
    ///         Nothing is stored: the token is derived from what the row already holds, so a type gains
    ///         no column and an existing table needs no migration.
    ///     </para>
    ///     <para>
    ///         <b>A soft-deleted row folds its <c>deleted_at</c> in</b> (fisher#471). A soft delete moves
    ///         neither <c>last_modified</c> nor <c>data</c>, so without it a delete was invisible to the
    ///         token: a console holding a live row's version could save over a deletion it never saw, and an
    ///         undelete's "new version" could equal the deleted row's. A live row's token is unchanged.
    ///     </para>
    /// </remarks>
    private static string ContentVersion(string? lastModified, string json, string? deletedAt)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(deletedAt is null
            ? $"{lastModified}\n{json}"
            : $"{lastModified}\n{json}\n{deletedAt}")))[..32];

    private static Type? TypeForAlias(DocumentMapping mapping, string? alias)
        => alias is null
            ? null
            : mapping.SubClasses.FirstOrDefault(x => string.Equals(x.Alias, alias, StringComparison.Ordinal))
                  ?.DocumentType
              ?? (string.Equals(mapping.Alias, alias, StringComparison.Ordinal) ? mapping.DocumentType : null);

    private static Type? ReadTypeOf(DocumentMapping mapping, StoredDocument document)
        => document.DocumentType is { } name
            ? mapping.SubClasses.FirstOrDefault(x => x.DocumentType.FullNameInCode() == name)?.DocumentType
              ?? mapping.DocumentType
            : null;

    /// <summary>
    ///     The mapping a console's type name refers to, or null when this store has no such type.
    /// </summary>
    /// <remarks>
    ///     Matched on the fully-qualified name first, because that is what <c>DocumentTypesAsync</c>
    ///     handed out; then the simple name and the alias, because a human typing into a URL will not
    ///     use the first. <b>Never <c>MappingFor</c></b>, which would <em>register</em> a type this
    ///     store does not have and give it a table on the next migration.
    /// </remarks>
    private (DocumentMapping? Mapping, Type? QueriedType) ResolveForDiagnostics(string documentTypeName)
    {
        if (string.IsNullOrWhiteSpace(documentTypeName))
        {
            return (null, null);
        }

        var mappings = MaterializeMappings();

        var mapping = mappings.FirstOrDefault(x => Names(x.DocumentType, x.Alias).Contains(documentTypeName,
            StringComparer.OrdinalIgnoreCase));

        if (mapping is not null)
        {
            return (mapping, mapping.DocumentType);
        }

        // A registered sub-class has no mapping of its own — that is fisher#17's whole point, and it is
        // what makes a hierarchy share one table. So the name may be a sub-class of one, in which case
        // the table is the base's and the type is what the doc_type filter narrows to.
        foreach (var candidate in mappings.Where(x => x.IsHierarchy))
        {
            var subClass = candidate.SubClasses.FirstOrDefault(x
                => Names(x.DocumentType, x.Alias).Contains(documentTypeName, StringComparer.OrdinalIgnoreCase));

            if (subClass is not null)
            {
                return (candidate, subClass.DocumentType);
            }
        }

        return (null, null);
    }

    private static string[] Names(Type type, string alias) => [type.FullNameInCode(), type.Name, alias];

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, DocumentMapping mapping,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();

        command.CommandText = "select 1 from sqlite_master where type = 'table' and name = $name";
        command.Parameters.AddWithValue("$name", mapping.TableName.Name);

        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null;
    }

    /// <summary>
    ///     The <c>where</c> clause and the binder for it: the three implicit filters, then whatever the
    ///     console asked for.
    /// </summary>
    private sealed class DiagnosticFilter(DocumentMapping mapping, Type queriedType, string? tenantId)
    {
        /// <summary>No tenant predicate at all — the conjoined reading of <c>AllTenants</c>.</summary>
        public bool EveryTenant { get; init; }

        public string? IdEquals { get; init; }
        public bool IncludeSoftDeleted { get; init; }
        public string? CorrelationId { get; init; }
        public string? CausationId { get; init; }
        public string? LastModifiedBy { get; init; }

        public (string Where, Action<SqliteCommand> Bind) Build()
        {
            var terms = new List<string>();
            var binders = new List<Action<SqliteCommand>>();

            // ---- the three implicit filters, each from the place that owns it ----

            // jasperfx#870 §3: excluded unless asked for. Asking is the console's IncludeSoftDeleted,
            // or a load by id, which is explicit about the one row it wants.
            if (mapping.IsSoftDeleted && !IncludeSoftDeleted)
            {
                terms.Add(SoftDelete.NotDeletedSql);
            }

            if (mapping.IsHierarchy)
            {
                terms.Add(DocumentHierarchy.FilterSqlFor(mapping, queriedType));
            }

            if (mapping.IsConjoined && !EveryTenant)
            {
                // Normalized by the caller (DocumentQueryOptions.NormalizeTenantId), so an empty or
                // whitespace tenant is the default tenant rather than a tenant literally named "" —
                // the CritterWatch#1304 bug, and what `?? DefaultTenantId` alone let through.
                var tenant = tenantId ?? StorageConstants.DefaultTenantId;

                terms.Add($"{StorageConstants.TenantIdColumn} = $tenant");
                binders.Add(command => command.Parameters.AddWithValue("$tenant", tenant));
            }

            // ---- what the console asked for ----

            var requested = 0;
            foreach (var (column, value) in RequestedTerms())
            {
                var parameter = $"$requested{requested++}";
                terms.Add($"{column} = {parameter}");
                binders.Add(command => command.Parameters.AddWithValue(parameter, value));
            }

            var where = terms.Count == 0 ? string.Empty : " where " + string.Join(" and ", terms);

            return (where, command =>
            {
                foreach (var binder in binders)
                {
                    binder(command);
                }
            });
        }

        /// <summary>
        ///     What the console asked for beyond the implicit filters — the id and the three metadata
        ///     columns — as column/value equalities. Rendered as SQL by <see cref="Build" /> and as
        ///     statement predicates by the criteria read, so both reads narrow by exactly the same list.
        /// </summary>
        /// <remarks>
        ///     A metadata filter on a column the type does not persist never reaches here: every read
        ///     refuses it first (<see cref="AssertMetadataFiltersAreCaptured" />, jasperfx#1077). The terms
        ///     are emitted unconditionally on purpose, so a read path that forgot the refusal fails with
        ///     <c>no such column</c> rather than silently returning the unfiltered page, which is what the
        ///     old <c>column.Enabled</c> gate here did.
        /// </remarks>
        public IEnumerable<(string Column, object Value)> RequestedTerms()
        {
            if (IdEquals is { } id)
            {
                // Converted through the mapping's identity type rather than bound as the raw string. A
                // console hands an id over as text, and comparing that against the id column directly is
                // the uppercase-Guid trap — fi_doc_*.id holds the lowercase canonical form and SQLite's
                // default collation is case-sensitive, so the raw string would match nothing.
                yield return ("id", ConvertIdentity(mapping, id));
            }

            foreach (var (column, value) in new[]
                     {
                         (mapping.Metadata.CorrelationId, CorrelationId),
                         (mapping.Metadata.CausationId, CausationId),
                         (mapping.Metadata.LastModifiedBy, LastModifiedBy)
                     })
            {
                if (value is not null)
                {
                    yield return (column.Name, value);
                }
            }
        }
    }

    /// <summary>
    ///     A console's string id in the form the <c>id</c> column holds.
    /// </summary>
    private static object ConvertIdentity(DocumentMapping mapping, string id)
    {
        var stored = mapping.StoredIdType;

        if (stored == typeof(Guid))
        {
            return Guid.TryParse(id, out var parsed)
                ? SqliteStorageDialect<Guid>.ToDatabaseValue(parsed)
                : id;
        }

        if (stored == typeof(int) && int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
        if (stored == typeof(long) && long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;

        return id;
    }
}
