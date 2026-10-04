using System.Data;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Fisher.Internal;
using Fisher.Storage;
using JasperFx;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Documents;
using JasperFx.Events;
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
///         <b>This is a hand-built read, and that makes it a fourth caller of the three implicit
///         filters</b> — the shape fisher#51 warns about. It cannot go through <c>Query&lt;T&gt;()</c>:
///         the console names a type as a string and filters on <em>columns</em> (correlation id,
///         causation id, last-modified-by) that are not document members, so there is no expression
///         tree to build. The mitigation is that each filter is composed from the single place that
///         owns it — <see cref="SoftDelete.NotDeletedSql" />,
///         <see cref="DocumentHierarchy.FilterSqlFor" />, the tenant column — rather than re-spelled
///         here, and <c>diagnostics_reads_carry_the_implicit_filters</c> pins all three.
///     </para>
///     <para>
///         ⚠️ <b>That is also why <see cref="DocumentQueryOptions.Where" /> and
///         <see cref="DocumentQueryOptions.OrderBy" /> are refused</b> rather than applied. They are
///         Dynamic LINQ text meant for the store's own <c>IQueryable&lt;T&gt;</c> (jasperfx#869, still
///         open), and splicing them into this SQL would be a fifth filter path with none of the member
///         resolution, enum storage or decimal handling <c>Query&lt;T&gt;()</c> has. Returning the
///         unfiltered page instead is the one answer the contract forbids: a console cannot tell it
///         from a filter that matched every row.
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
    /// </remarks>
    Task<IReadOnlyList<DocumentTypeRef>> IDocumentStoreDiagnostics.DocumentTypesAsync(CancellationToken token)
    {
        var refs = new List<DocumentTypeRef>();

        foreach (var mapping in MaterializeMappings().OrderBy(x => x.DocumentType.Name, StringComparer.Ordinal))
        {
            var rootTypeName = mapping.DocumentType.FullNameInCode();
            refs.Add(new DocumentTypeRef(rootTypeName, mapping.Alias, Options.DatabaseSchemaName));

            refs.AddRange(mapping.SubClasses
                .OrderBy(x => x.DocumentType.Name, StringComparer.Ordinal)
                .Select(x => new DocumentTypeRef(
                    x.DocumentType.FullNameInCode(), x.Alias, Options.DatabaseSchemaName)
                {
                    RootTypeName = rootTypeName
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
    ///         The three metadata filters are honoured only where the type persists the column, and
    ///         ignored otherwise. A filter on a disabled column is <c>no such column</c>, not an empty
    ///         result — the same gating <c>QueryEventsAsync</c> applies on the event side, for the same
    ///         reason.
    ///     </para>
    ///     <para>
    ///         The criteria are refused <em>first</em>, before the type is resolved, so an unknown type
    ///         with a predicate is still a refusal rather than an empty page that looks like an answer.
    ///         <see cref="DocumentQueryOptions.AllTenants" /> with a named tenant goes before even that:
    ///         a read cannot be scoped to one tenant and to all of them, and the store must not pick.
    ///     </para>
    /// </remarks>
    async Task<DocumentQueryResult> IDocumentStoreDiagnostics.QueryDocumentsAsync(
        string documentTypeName, DocumentQueryOptions options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AssertValidTenantScope();
        RefuseUnsupportedCriteria(options);

        var pageNumber = Math.Max(1, options.PageNumber);
        var pageSize = Math.Max(1, options.PageSize);
        var empty = new DocumentQueryResult(Array.Empty<StoredDocument>(), 0, pageNumber, pageSize);

        var (mapping, queriedType) = ResolveForDiagnostics(documentTypeName);

        if (mapping is null || queriedType is null)
        {
            return empty;
        }

        if (options.AllTenants)
        {
            return await QueryEveryTenantAsync(mapping, queriedType, options, pageNumber, pageSize, token)
                .ConfigureAwait(false);
        }

        var tenantId = DocumentQueryOptions.NormalizeTenantId(options.TenantId);
        var database = DatabaseForDiagnostics(tenantId);

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
        // the primary key, so this costs nothing.
        var documents = await ReadStoredDocumentsAsync(connection, null, mapping, tenantId, where, bind,
            " order by id limit $take offset $skip", command =>
            {
                command.Parameters.AddWithValue("$take", pageSize);
                command.Parameters.AddWithValue("$skip", (pageNumber - 1) * pageSize);
            }, token).ConfigureAwait(false);

        return new DocumentQueryResult(documents, total, pageNumber, pageSize);
    }

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
                " order by id limit $take offset $skip", command =>
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

        var (mapping, queriedType) = ResolveForWrite(request.DocumentTypeName, request.Id);

        object document;
        try
        {
            document = Deserialize(queriedType, request.Json);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException)
        {
            throw new ArgumentException(
                $"The JSON for '{request.DocumentTypeName}' '{request.Id}' could not be read as {queriedType.FullNameInCode()}: {e.Message}",
                nameof(request), e);
        }

        return await RunDiagnosticWriteAsync(mapping, queriedType, request.Id, request.TenantId,
            request.ExpectedVersion, includeSoftDeletedCurrent: true,
            async (session, current, ct) =>
            {
                var identity = (string)InvokeClosed(IdentityTextFor, queriedType, session, [document])!;

                if (!string.Equals(identity, FisherSession.InvariantText(ConvertIdentity(mapping, request.Id)),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"The id in the JSON ('{identity}') does not agree with the requested id '{request.Id}'. "
                        + "Refusing rather than guessing which one was meant.", nameof(request));
                }

                InvokeClosed(StoreForDiagnostics, queriedType, session, [document, GuidVersionOf(mapping, current)]);

                await session.SaveChangesAsync(ct).ConfigureAwait(false);

                return DocumentWriteStatus.Saved;
            }, token).ConfigureAwait(false);
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
            request.ExpectedVersion, includeSoftDeletedCurrent: false,
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
        string id, string? tenantId, string? expectedVersion, bool includeSoftDeletedCurrent,
        Func<FisherSession, StoredDocument?, CancellationToken, Task<DocumentWriteStatus>> write,
        CancellationToken token)
    {
        tenantId = DocumentQueryOptions.NormalizeTenantId(tenantId);
        var database = DatabaseForDiagnostics(tenantId);

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
                id, includeSoftDeletedCurrent, ct).ConfigureAwait(false);

            // A soft-deleted row is still the row a save replaces — storing a soft-deleted document
            // undeletes it, as it does for an application — but it is not a live one to delete.
            if (!includeSoftDeletedCurrent && current is null)
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

            var written = status == DocumentWriteStatus.Saved
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

    private static void RefuseUnsupportedCriteria(DocumentQueryOptions options)
    {
        const string reason =
            "Fisher's IDocumentStoreDiagnostics does not apply Dynamic LINQ criteria yet — they need the "
            + "string-to-IQueryable translation tracked as jasperfx#869. Page without it, or narrow with "
            + "IdEquals and the metadata filters.";

        if (!string.IsNullOrWhiteSpace(options.Where))
        {
            throw new DocumentCriteriaNotSupportedException(nameof(DocumentQueryOptions.Where), reason);
        }

        if (!string.IsNullOrWhiteSpace(options.OrderBy))
        {
            throw new DocumentCriteriaNotSupportedException(nameof(DocumentQueryOptions.OrderBy), reason);
        }
    }

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
        var metadata = mapping.Metadata;
        var columns = new List<string> { "id", "data", metadata.LastModified.Name };

        int Add(bool present, string column)
        {
            if (!present) return -1;
            columns.Add(column);
            return columns.Count - 1;
        }

        var versionOrdinal = Add(mapping.UseOptimisticConcurrency, metadata.Version.Name);
        var revisionOrdinal = Add(mapping.UseNumericRevisions, metadata.Revision.Name);
        var createdOrdinal = Add(metadata.CreatedAt.Enabled, metadata.CreatedAt.Name);
        var tenantOrdinal = Add(mapping.IsConjoined, StorageConstants.TenantIdColumn);
        var deletedOrdinal = Add(mapping.IsSoftDeleted, SoftDelete.IsDeletedColumn);
        var deletedAtOrdinal = Add(mapping.IsSoftDeleted, SoftDelete.DeletedAtColumn);
        var docTypeOrdinal = Add(mapping.IsHierarchy, DocumentHierarchy.DocTypeColumn);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"select {string.Join(", ", columns)} from {mapping.QuotedTableName}{where}{tail}";
        bindWhere(command);
        bindTail(command);

        // A single-tenanted type reports the tenant the read was for — the file's own tenant under
        // database-per-tenant — or the default, which is what the contract says a single-tenanted row
        // belongs to.
        var readTenant = tenantId ?? StorageConstants.DefaultTenantId;

        var documents = new List<StoredDocument>();

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            // Byte-exact, as fisher#28's JSON reads are and for the same reason: data holds precisely
            // what the serializer wrote, so a console shows the document rather than a re-rendering.
            var json = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var lastModified = reader.IsDBNull(2) ? null : reader.GetString(2);

            var docType = docTypeOrdinal >= 0 && !reader.IsDBNull(docTypeOrdinal)
                ? reader.GetString(docTypeOrdinal)
                : null;

            documents.Add(new StoredDocument(
                FisherSession.InvariantText(reader.GetValue(0)), json)
            {
                // guid_version is rendered through Guid rather than returned as stored: Weasel's version
                // binder writes the raw Guid, which Microsoft.Data.Sqlite spells UPPERCASE, while every
                // other Guid a console sees from Fisher is lowercase canonical. An opaque token is still
                // one a console will display and paste back.
                Version = versionOrdinal >= 0 && !reader.IsDBNull(versionOrdinal)
                    ? CanonicalVersion(reader.GetString(versionOrdinal))
                    : revisionOrdinal >= 0 && !reader.IsDBNull(revisionOrdinal)
                        ? reader.GetInt64(revisionOrdinal).ToString(CultureInfo.InvariantCulture)
                        : ContentVersion(lastModified, json),
                LastModified = lastModified is null ? null : SqliteTimestamp.FromDatabaseValue(lastModified),
                Created = TimestampAt(reader, createdOrdinal),
                TenantId = tenantOrdinal >= 0 && !reader.IsDBNull(tenantOrdinal)
                    ? reader.GetString(tenantOrdinal)
                    : readTenant,
                IsDeleted = deletedOrdinal >= 0 && !reader.IsDBNull(deletedOrdinal) && reader.GetInt64(deletedOrdinal) != 0,
                DeletedAt = TimestampAt(reader, deletedAtOrdinal),
                DocumentType = (TypeForAlias(mapping, docType) ?? mapping.DocumentType).FullNameInCode()
            });
        }

        return documents;
    }

    private static string CanonicalVersion(string stored)
        => Guid.TryParse(stored, out var version) ? version.ToString("D") : stored;

    private static DateTimeOffset? TimestampAt(SqliteDataReader reader, int ordinal)
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
    /// </remarks>
    private static string ContentVersion(string? lastModified, string json)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{lastModified}\n{json}")))[..32];

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

            if (IdEquals is { } id)
            {
                // Converted through the mapping's identity type rather than bound as the raw string. A
                // console hands an id over as text, and comparing that against the id column directly is
                // the uppercase-Guid trap — fi_doc_*.id holds the lowercase canonical form and SQLite's
                // default collation is case-sensitive, so the raw string would match nothing.
                terms.Add("id = $id");
                binders.Add(command => command.Parameters.AddWithValue("$id", ConvertIdentity(mapping, id)));
            }

            AddMetadataFilter(mapping.Metadata.CorrelationId, CorrelationId, "$correlation");
            AddMetadataFilter(mapping.Metadata.CausationId, CausationId, "$causation");
            AddMetadataFilter(mapping.Metadata.LastModifiedBy, LastModifiedBy, "$user");

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
