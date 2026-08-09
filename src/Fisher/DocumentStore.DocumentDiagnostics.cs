using Fisher.Storage;
using JasperFx;
using JasperFx.Core.Reflection;
using JasperFx.Documents;
using Microsoft.Data.Sqlite;

namespace Fisher;

/// <summary>
///     The read-only document-browsing surface a monitoring console uses (fisher#44) — list the
///     mapped types, page their rows as raw JSON, fetch one by id.
/// </summary>
/// <remarks>
///     <para>
///         Implemented <b>explicitly</b>, like every other tooling surface on this store, so it does
///         not crowd <see cref="IDocumentStore" />.
///     </para>
///     <para>
///         <b>This is a hand-built read, and that makes it a fourth caller of the three implicit
///         filters</b> — the shape fisher#51 warns about. It cannot go through <c>Query&lt;T&gt;()</c>:
///         the console names a type as a string and filters on <em>columns</em> (correlation id,
///         causation id, last-modified-by) that are not document members, so there is no expression
///         tree to build. The mitigation is that each filter is composed from the single place that
///         owns it — <see cref="SoftDelete.NotDeletedSql" />,
///         <see cref="DocumentHierarchy.FilterSqlFor" />, the tenant column — rather than re-spelled
///         here, and <c>diagnostics_reads_carry_the_implicit_filters</c> pins all three.
///     </para>
/// </remarks>
public partial class DocumentStore : IDocumentStoreDiagnostics
{
    /// <remarks>
    ///     Every mapped type, whether or not its table exists — a document table is created on demand
    ///     at first write, so a registered type with no rows yet is still one the console should offer
    ///     in its picker.
    /// </remarks>
    Task<IReadOnlyList<DocumentTypeRef>> IDocumentStoreDiagnostics.DocumentTypesAsync(CancellationToken token)
    {
        var refs = MaterializeMappings()
            .OrderBy(x => x.DocumentType.Name, StringComparer.Ordinal)
            .Select(x => new DocumentTypeRef(
                x.DocumentType.FullNameInCode(), x.Alias, Options.DatabaseSchemaName))
            .ToList();

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
    ///         The three metadata filters are honoured only where the type persists the column, and
    ///         ignored otherwise. A filter on a disabled column is <c>no such column</c>, not an empty
    ///         result — the same gating <c>QueryEventsAsync</c> applies on the event side, for the same
    ///         reason.
    ///     </para>
    /// </remarks>
    async Task<DocumentQueryResult> IDocumentStoreDiagnostics.QueryDocumentsAsync(
        string documentTypeName, DocumentQueryOptions options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(options);

        var pageNumber = Math.Max(1, options.PageNumber);
        var pageSize = Math.Max(1, options.PageSize);
        var empty = new DocumentQueryResult([], 0, pageNumber, pageSize);

        var (mapping, queriedType) = ResolveForDiagnostics(documentTypeName);

        if (mapping is null || queriedType is null)
        {
            return empty;
        }

        await using var connection = await Database.OpenConnectionAsync(token).ConfigureAwait(false);

        if (!await TableExistsAsync(connection, mapping, token).ConfigureAwait(false))
        {
            return empty;
        }

        var (where, bind) = BuildDiagnosticFilter(mapping, queriedType, options);

        long total;

        await using (var counting = connection.CreateCommand())
        {
            counting.CommandText = $"select count(*) from {mapping.QuotedTableName}{where}";
            bind(counting);

            total = Convert.ToInt64(await counting.ExecuteScalarAsync(token).ConfigureAwait(false));
        }

        // Ordered by id so paging is stable — a page of an unordered SELECT is not a page. The id is
        // the primary key, so this costs nothing.
        await using var paging = connection.CreateCommand();
        paging.CommandText =
            $"select data from {mapping.QuotedTableName}{where} order by id limit $take offset $skip";
        bind(paging);
        paging.Parameters.AddWithValue("$take", pageSize);
        paging.Parameters.AddWithValue("$skip", (pageNumber - 1) * pageSize);

        var documents = new List<string>();

        await using var reader = await paging.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            // Byte-exact, as fisher#28's JSON reads are and for the same reason: data holds precisely
            // what the serializer wrote, so a console shows the document rather than a re-rendering.
            documents.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
        }

        return new DocumentQueryResult(documents, total, pageNumber, pageSize);
    }

    /// <inheritdoc cref="IDocumentStoreDiagnostics.QueryDocumentsAsync" />
    async Task<string?> IDocumentStoreDiagnostics.LoadDocumentJsonAsync(
        string documentTypeName, string id, CancellationToken token)
    {
        var result = await ((IDocumentStoreDiagnostics)this)
            .QueryDocumentsAsync(documentTypeName, new DocumentQueryOptions(1, 1, id), token)
            .ConfigureAwait(false);

        return result.DocumentsJson.Count == 0 ? null : result.DocumentsJson[0];
    }

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
    private static (string Where, Action<SqliteCommand> Bind) BuildDiagnosticFilter(
        DocumentMapping mapping, Type queriedType, DocumentQueryOptions options)
    {
        var terms = new List<string>();
        var binders = new List<Action<SqliteCommand>>();

        // ---- the three implicit filters, each from the place that owns it ----

        if (mapping.IsSoftDeleted)
        {
            terms.Add(SoftDelete.NotDeletedSql);
        }

        if (mapping.IsHierarchy)
        {
            terms.Add(DocumentHierarchy.FilterSqlFor(mapping, queriedType));
        }

        if (mapping.IsConjoined)
        {
            var tenantId = options.TenantId ?? StorageConstants.DefaultTenantId;

            terms.Add($"{StorageConstants.TenantIdColumn} = $tenant");
            binders.Add(command => command.Parameters.AddWithValue("$tenant", tenantId));
        }

        // ---- what the console asked for ----

        if (options.IdEquals is { } id)
        {
            // Converted through the mapping's identity type rather than bound as the raw string. A
            // console hands an id over as text, and comparing that against the id column directly is
            // the uppercase-Guid trap — fi_doc_*.id holds the lowercase canonical form and SQLite's
            // default collation is case-sensitive, so the raw string would match nothing.
            terms.Add("id = $id");
            binders.Add(command => command.Parameters.AddWithValue("$id", ConvertIdentity(mapping, id)));
        }

        AddMetadataFilter(mapping.Metadata.CorrelationId, options.CorrelationId, "$correlation");
        AddMetadataFilter(mapping.Metadata.CausationId, options.CausationId, "$causation");
        AddMetadataFilter(mapping.Metadata.LastModifiedBy, options.LastModifiedBy, "$user");

        void AddMetadataFilter(Storage.Metadata.MetadataColumn column, string? value, string parameter)
        {
            if (value is null || !column.Enabled)
            {
                return;
            }

            terms.Add($"{column.Name} = {parameter}");
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

        if (stored == typeof(int) && int.TryParse(id, out var i)) return i;
        if (stored == typeof(long) && long.TryParse(id, out var l)) return l;

        return id;
    }
}
