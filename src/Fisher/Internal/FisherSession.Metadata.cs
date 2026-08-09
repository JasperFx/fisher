using Fisher.Metadata;
using Fisher.Storage;
using JasperFx;
using Microsoft.Data.Sqlite;
using Weasel.Sqlite;

namespace Fisher.Internal;

/// <summary>
///     <c>MetadataForAsync</c> — a document's stored metadata, without loading the document (fisher#29).
/// </summary>
/// <remarks>
///     <para>
///         <b>Hand-built rather than routed through the LINQ path</b>, which is the second place in
///         Fisher where that is the right answer rather than a shortcut (the first is bulk insert's
///         duplicate probe). Two reasons, and the first is the one that matters: the LINQ path applies
///         the soft-delete filter, and a soft-deleted row's metadata is exactly what a caller asking
///         "when was this deleted" wants. The second is that this reads the metadata columns and not
///         <c>data</c>, so there is nothing for a selector to materialize.
///     </para>
///     <para>
///         The tenant term is kept, because a conjoined table keys on <c>(tenant_id, id)</c> and the
///         same id under another tenant is a different document.
///     </para>
///     <para>
///         Columns are chosen from the mapping rather than selected blindly: a document table only
///         carries the columns its type asked for, and naming one it does not have is
///         <c>no such column</c> rather than a null.
///     </para>
/// </remarks>
internal partial class FisherSession
{
    public Task<StoredDocumentMetadata?> MetadataForAsync<T>(T document, CancellationToken token = default)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(document);

        return MetadataForIdAsync<T>(StorageFor<T>().IdentityFor(document), token);
    }

    public Task<StoredDocumentMetadata?> MetadataForAsync<T>(Guid id, CancellationToken token = default)
        where T : notnull => MetadataForIdAsync<T>(id, token);

    public Task<StoredDocumentMetadata?> MetadataForAsync<T>(string id, CancellationToken token = default)
        where T : notnull => MetadataForIdAsync<T>(id, token);

    public Task<StoredDocumentMetadata?> MetadataForAsync<T>(int id, CancellationToken token = default)
        where T : notnull => MetadataForIdAsync<T>(id, token);

    public Task<StoredDocumentMetadata?> MetadataForAsync<T>(long id, CancellationToken token = default)
        where T : notnull => MetadataForIdAsync<T>(id, token);

    private async Task<StoredDocumentMetadata?> MetadataForIdAsync<T>(object id, CancellationToken token)
        where T : notnull
    {
        var mapping = Options.Schema.MappingFor(typeof(T));
        var metadata = mapping.Metadata;
        var storage = StorageFor<T>();

        // Every table has these two; the rest are asked for one at a time below.
        var columns = new List<string> { "id", "last_modified", "dotnet_type" };

        void Include(bool present, string column)
        {
            if (present)
            {
                columns.Add(column);
            }
        }

        Include(mapping.IsConjoined, StorageConstants.TenantIdColumn);
        Include(mapping.IsHierarchy, DocumentHierarchy.DocTypeColumn);
        Include(mapping.UseOptimisticConcurrency, "guid_version");
        Include(mapping.UseNumericRevisions, NumericRevision.Column);
        Include(mapping.IsSoftDeleted, SoftDelete.IsDeletedColumn);
        Include(mapping.IsSoftDeleted, SoftDelete.DeletedAtColumn);
        Include(metadata.CreatedAt.Enabled, metadata.CreatedAt.Name);
        Include(metadata.CorrelationId.Enabled, metadata.CorrelationId.Name);
        Include(metadata.CausationId.Enabled, metadata.CausationId.Name);
        Include(metadata.LastModifiedBy.Enabled, metadata.LastModifiedBy.Name);
        Include(metadata.Headers.Enabled, metadata.Headers.Name);

        var builder = new CommandBuilder();
        builder.Append($"select {string.Join(", ", columns)} from {mapping.QuotedTableName} where id = ");
        builder.AppendParameter(storage.RawIdentityValue(id));

        if (mapping.IsConjoined)
        {
            builder.Append($" and {StorageConstants.TenantIdColumn} = ");
            builder.AppendParameter(TenantId);
        }

        await using var command = (SqliteCommand)builder.Compile();
        await ConfigureCommandAsync(command, token).ConfigureAwait(false);

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

        if (!await reader.ReadAsync(token).ConfigureAwait(false))
        {
            return null;
        }

        int? Ordinal(string column)
        {
            var index = columns.IndexOf(column);
            return index < 0 || reader.IsDBNull(index) ? null : index;
        }

        DateTimeOffset? Timestamp(string column)
            => Ordinal(column) is { } i ? SqliteTimestamp.FromDatabaseValue(reader.GetString(i)) : null;

        string? Text(string column) => Ordinal(column) is { } i ? reader.GetString(i) : null;

        return new StoredDocumentMetadata(id,
            // A single-tenant table has no column, and the session's tenant is the honest answer.
            mapping.IsConjoined ? Text(StorageConstants.TenantIdColumn)! : TenantId,
            Timestamp("last_modified")!.Value)
        {
            CreatedAt = Timestamp(metadata.CreatedAt.Name),
            // Explicit conversions on the way out, as the event row readers do and for the same
            // reason: the write path converted explicitly on the way in, so reading through a
            // provider convenience method would leave the round trip depending on
            // Microsoft.Data.Sqlite's coercion rules rather than on Fisher's storage decisions.
            Version = Ordinal("guid_version") is { } v ? Guid.Parse(reader.GetString(v)) : null,
            Revision = Ordinal(NumericRevision.Column) is { } r ? (int)reader.GetInt64(r) : null,
            Deleted = Ordinal(SoftDelete.IsDeletedColumn) is { } d && reader.GetInt64(d) != 0,
            DeletedAt = Timestamp(SoftDelete.DeletedAtColumn),
            DotNetType = Text("dotnet_type"),
            DocumentType = Text(DocumentHierarchy.DocTypeColumn),
            CorrelationId = Text(metadata.CorrelationId.Name),
            CausationId = Text(metadata.CausationId.Name),
            LastModifiedBy = Text(metadata.LastModifiedBy.Name),
            Headers = Ordinal(metadata.Headers.Name) is { } h
                ? ((Weasel.Storage.IStorageSession)this).Serializer
                    .FromJson<Dictionary<string, object>>(reader, h)
                : null
        };
    }
}
