using System.Text;
using Fisher.Serialization;
using Fisher.Storage.Metadata;
using JasperFx;
using Weasel.Core.Identity;
using Weasel.Storage;

namespace Fisher.Storage;

/// <summary>
///     Builds the closed-shape <see cref="DocumentStorageDescriptor{TDoc,TId}" /> — the binder arrays
///     and the four SQL statements — for one document type.
/// </summary>
/// <remarks>
///     <para>
///         <b>Column order and <c>?</c> order are one contract, not two.</b> The shared closed-shape
///         write operations bind by position, so the SQL emitted here has to place its parameter marks
///         in exactly the order those operations bind them. Two different orders are in play:
///     </para>
///     <list type="bullet">
///         <item>
///             upsert / insert / overwrite — <c>[tenant,] id, data, client-side binders</c>, then the
///             optional trailing concurrency guard (<c>ClosedShapeUpsertOperation.BindPreOnConflictParameters</c>).
///         </item>
///         <item>
///             update — <c>data, client-side binders, id, [tenant]</c>, then the guard
///             (<c>ClosedShapeUpdateOperation.BindPreConcurrencyParameters</c>). Note that the id moves
///             from the front to the back, because it is a <c>WHERE</c> term here rather than a value.
///         </item>
///     </list>
///     <para>
///         A server-side binder contributes a column and an expression but no parameter mark, which is
///         what lets <c>last_modified</c> sit in the middle of the column list without shifting any
///         slot. That only holds while its <c>ValueSql</c> contains no <c>?</c> — true of
///         <see cref="SqliteTimestamp.NowExpression" />.
///     </para>
///     <para>
///         SQLite reaches the same places as Marten with nearly the same syntax — <c>INSERT … ON
///         CONFLICT … DO UPDATE SET … RETURNING</c> — rather than SQL Server's <c>MERGE</c>. The upsert
///         is therefore closer to Marten's than to Polecat's.
///     </para>
/// </remarks>
internal static class SqliteDocumentStorageDescriptorBuilder
{
    public static DocumentStorageDescriptor<TDoc, TId> Build<TDoc, TId>(
        DocumentMapping mapping,
        IIdentification<TDoc, TId> identification,
        StoreOptions options)
        where TDoc : notnull
        where TId : notnull
    {
        var dialect = SqliteStorageDialect<TId>.Instance;
        var metadata = mapping.Metadata;
        var writeBinders = new List<IDocumentMetadataBinder<TDoc>>();
        var readBinders = new List<IDocumentMetadataBinder<TDoc>>();

        DocumentVersionBinder<TDoc>? versionBinder = null;
        DocumentRevisionBinder<TDoc>? revisionBinder = null;
        var versionReadIndex = -1;
        var docTypeReadIndex = -1;
        Func<string, Type>? resolveDocumentType = null;

        mapping.AssertConcurrencyIsCoherent();

        // The hierarchy discriminator, written on every save and read so a selector can materialise the
        // row as its own sub-class. Added FIRST among the metadata binders because the hierarchical
        // selectors dispatch on it before reading anything else.
        if (mapping.IsHierarchy)
        {
            resolveDocumentType = mapping.TypeFor;

            var docTypeBinder = new DocumentDocTypeBinder<TDoc>(
                DocumentHierarchy.DocTypeColumn, dialect, mapping.AliasFor);

            writeBinders.Add(docTypeBinder);
            docTypeReadIndex = readBinders.Count;
            readBinders.Add(docTypeBinder);
        }

        // guid_version carries optimistic concurrency in its own column so a version check never has
        // to parse the JSON body. Read back as well as written: the session's version tracker needs
        // the value it loaded in order to guard the next write with it.
        if (mapping.UseOptimisticConcurrency)
        {
            versionBinder = new DocumentVersionBinder<TDoc>("guid_version", dialect, metadata.Version.Member);
            writeBinders.Add(versionBinder);
            versionReadIndex = readBinders.Count;
            readBinders.Add(versionBinder);
        }
        else if (mapping.UseNumericRevisions)
        {
            // The numeric alternative rides its own INTEGER column. Always read back, mapped member or
            // not: unlike the Guid version, the revision the caller will guard the *next* write with is
            // the one the database just computed, so a load that did not return it would leave every
            // explicit Store(doc, revision) guessing. StorageColumnType.Int because IRevisioned's member
            // is an int; a long-backed member would want Long, which is the width Weasel defaults to.
            revisionBinder = new DocumentRevisionBinder<TDoc>(
                NumericRevision.Column, dialect, metadata.Revision.Member, StorageColumnType.Int);

            writeBinders.Add(revisionBinder);
            versionReadIndex = readBinders.Count;
            readBinders.Add(revisionBinder);
        }

        // Written on every save, never selected. It is what a hierarchy discriminator would build on,
        // and the one metadata column that cannot be mapped onto a member: DocumentDotNetTypeBinder
        // takes none, so DocumentMetadata does not offer it (fisher#11).
        writeBinders.Add(new DocumentDotNetTypeBinder<TDoc>("dotnet_type", dialect));

        // Server-side: contributes the timestamp expression rather than a parameter.
        var lastModifiedBinder = new DocumentLastModifiedBinder<TDoc>(
            "last_modified", metadata.LastModified.Member, SqliteTimestamp.NowExpression);

        writeBinders.Add(lastModifiedBinder);
        AddIfMapped(readBinders, lastModifiedBinder, metadata.LastModified);

        // Soft delete's two columns are written by every save. Both binders write the *live* value —
        // false and null — which is what makes storing a soft-deleted document undelete it, in the
        // insert branch and (through excluded.*) in the update branch alike.
        if (mapping.IsSoftDeleted)
        {
            var isDeletedBinder = new DocumentSoftDeletedBinder<TDoc>(
                SoftDelete.IsDeletedColumn, dialect, metadata.IsSoftDeleted.Member);

            var deletedAtBinder = new DocumentSoftDeletedAtBinder<TDoc>(
                SoftDelete.DeletedAtColumn, dialect, metadata.DeletedAt.Member);

            writeBinders.Add(isDeletedBinder);
            writeBinders.Add(deletedAtBinder);

            AddIfMapped(readBinders, isDeletedBinder, metadata.IsSoftDeleted);
            AddIfMapped(readBinders, deletedAtBinder, metadata.DeletedAt);
        }

        // The session-sourced columns (fisher#29). Each contributes one client-side slot, appended
        // after every existing one, so nothing above it shifts — the whole reason they go last rather
        // than in the order DocumentMetadata lists them.
        //
        // The values come off the session, which is what makes a document and an event written in the
        // same unit of work carry the same correlation id: FisherSession seeds both from
        // Activity.Current, and AppendPlanner.ApplySessionMetadata reads the same fields.
        if (metadata.CorrelationId.Enabled)
        {
            var binder = new DocumentCorrelationIdBinder<TDoc>(
                metadata.CorrelationId.Name, dialect, metadata.CorrelationId.Member);

            writeBinders.Add(binder);
            AddIfMapped(readBinders, binder, metadata.CorrelationId);
        }

        if (metadata.CausationId.Enabled)
        {
            var binder = new DocumentCausationIdBinder<TDoc>(
                metadata.CausationId.Name, dialect, metadata.CausationId.Member);

            writeBinders.Add(binder);
            AddIfMapped(readBinders, binder, metadata.CausationId);
        }

        if (metadata.LastModifiedBy.Enabled)
        {
            var binder = new DocumentLastModifiedByBinder<TDoc>(
                metadata.LastModifiedBy.Name, dialect, metadata.LastModifiedBy.Member);

            writeBinders.Add(binder);
            AddIfMapped(readBinders, binder, metadata.LastModifiedBy);
        }

        if (metadata.Headers.Enabled)
        {
            var binder = new DocumentHeadersBinder<TDoc>(
                metadata.Headers.Name, dialect, metadata.Headers.Member);

            writeBinders.Add(binder);
            AddIfMapped(readBinders, binder, metadata.Headers);
        }

        // The two read-only columns, in readBinders and never in writeBinders — which is not tidiness
        // in either case but the mechanism.
        //
        // created_at is filled by its column DEFAULT, and staying out of the write list is exactly what
        // stops the upsert's `do update set … = excluded.*` from moving it forward on every save. So
        // the "assign every column from excluded" rule needs no carve-out, which is the shape the
        // obvious implementation would have needed.
        //
        // tenant_id is bound inline by the storage operations ahead of the binder loop, being part of
        // the primary key, so a write binder would be a second writer of a value that already has one.
        if (metadata.CreatedAt.Enabled)
        {
            AddIfMapped(readBinders,
                new DocumentCreatedAtBinder<TDoc>(metadata.CreatedAt.Name, metadata.CreatedAt.Member,
                    SqliteTimestamp.NowExpression),
                metadata.CreatedAt);
        }

        if (mapping.IsConjoined)
        {
            AddIfMapped(readBinders,
                new DocumentTenantIdBinder<TDoc>(metadata.TenantId.Name, metadata.TenantId.Member),
                metadata.TenantId);
        }

        var writeArray = writeBinders.ToArray();
        var readArray = readBinders.ToArray();
        var clientSide = writeArray.Where(x => !x.IsServerSide).ToArray();

        // QueryOnly never writes, so it has no use for a version it cannot act on — unless the version
        // is mapped onto a member, in which case it is being read for the document rather than for the
        // session's version tracker, and dropping it would make a query-only load disagree with a
        // lightweight one about what the document holds.
        // A numeric revision always stays, mapped or not — see the binder above for why a caller needs
        // it back — so only the Guid version is ever dropped here.
        var queryOnlyReadArray = versionReadIndex >= 0 && metadata.Version.Member is null
                                 && revisionBinder is null
            ? readArray.Where((_, i) => i != versionReadIndex).ToArray()
            : readArray;

        var mode = mapping.ConcurrencyMode;
        var guarded = mode == ConcurrencyMode.Optimistic;

        return new DocumentStorageDescriptor<TDoc, TId>(
            identification,
            serializer: StorageSerializerAdapter.For(options.Serializer),
            dialect: dialect,
            clientSideWriteBinders: clientSide,
            writeBinders: writeArray,
            readBinders: readArray,
            queryOnlyReadBinders: queryOnlyReadArray,
            upsertSql: BuildUpsertSql(mapping, writeArray, revisionBinder, guarded),
            insertSql: BuildInsertSql(mapping, writeArray, revisionBinder),
            updateSql: BuildUpdateSql(mapping, writeArray, revisionBinder, guarded),
            overwriteSql: BuildUpsertSql(mapping, writeArray, revisionBinder, guarded: false,
                isOverwrite: true),
            isConjoined: mapping.IsConjoined,
            concurrencyMode: mode,
            versionBinder: versionBinder,
            revisionBinder: revisionBinder,
            versionReadIndex: versionReadIndex,
            resolveDocumentType: resolveDocumentType,
            docTypeReadIndex: docTypeReadIndex,
            tableName: mapping.TableName.QualifiedName);
    }

    /// <summary>
    ///     Add a binder to the read set only when its column is projected onto a member.
    /// </summary>
    /// <remarks>
    ///     An unmapped binder returns before touching the reader, so adding one would cost a column in
    ///     every SELECT to accomplish nothing. It would also be free to be wrong: the read ordinals are
    ///     <c>FirstMetadataColumn + index</c> into this array and the SELECT's column list is built from
    ///     the same array in the same order, so the two only stay aligned while both are derived from
    ///     it — which is the reason nothing here appends to one without the other.
    /// </remarks>
    private static void AddIfMapped<TDoc>(List<IDocumentMetadataBinder<TDoc>> readBinders,
        IDocumentMetadataBinder<TDoc> binder, MetadataColumn column) where TDoc : notnull
    {
        if (column.Member is not null)
        {
            readBinders.Add(binder);
        }
    }

    /// <summary>
    ///     <c>insert … on conflict do update</c>. The conflict target is the table's primary key, which
    ///     is the tenant/id pair under conjoined tenancy.
    /// </summary>
    private static string BuildUpsertSql<TDoc>(DocumentMapping mapping,
        IDocumentMetadataBinder<TDoc>[] writeBinders, IDocumentMetadataBinder<TDoc>? revisionBinder,
        bool guarded, bool isOverwrite = false) where TDoc : notnull
    {
        var sql = new StringBuilder(BuildInsertBody(mapping, writeBinders, revisionBinder));

        sql.Append(" on conflict (");
        sql.Append(mapping.IsConjoined ? $"{StorageConstants.TenantIdColumn}, id" : "id");
        sql.Append(") do update set data = excluded.data");

        foreach (var binder in writeBinders)
        {
            if (ReferenceEquals(binder, revisionBinder))
            {
                // Not excluded.revision: that is the value the insert branch computed, and this branch
                // is an increment of what is already stored. Two more slots, which the shared numeric
                // operations bind immediately after the client-side ones.
                sql.Append(", ").Append(NumericRevision.UpdateAssignmentSql(mapping.QuotedTableName));
                continue;
            }

            // excluded.* rather than repeating each binder's ValueSql: for a server-side expression
            // excluded already holds the value it computed for this statement, so one form covers
            // client-side and server-side columns alike and cannot drift from the insert branch.
            sql.Append(", ").Append(binder.ColumnName).Append(" = excluded.").Append(binder.ColumnName);
        }

        if (revisionBinder is not null)
        {
            // **The guard is not optional under Numeric, and this is the trap.** The shared
            // NumericClosedShapeUpsertOperation binds four trailing slots unconditionally — two for the
            // SET case, two for the guard — while NumericClosedShapeOverwriteOperation binds only the
            // two SET slots. So which statement is being built decides the shape, and the `guarded`
            // flag does not: it is false for Numeric (it means "Optimistic" to the caller), and
            // reading it here produced a two-slot statement for a four-slot binder. That is an
            // IndexOutOfRangeException from inside Weasel rather than anything Fisher could
            // attribute, which is why the distinction is spelled out rather than inferred.
            if (!isOverwrite)
            {
                sql.Append(" where ").Append(NumericRevision.GuardSql(mapping.QuotedTableName));
            }

            return sql.Append(" returning ").Append(NumericRevision.Column).ToString();
        }

        if (guarded)
        {
            // The trailing slot the Optimistic upsert operation binds. On a conflict where the stored
            // version differs, the update matches nothing and the row is left untouched, which is what
            // the operation's postprocessing reads as a concurrency failure.
            sql.Append(" where ").Append(mapping.QuotedTableName).Append(".guid_version = ?");
        }

        return sql.Append(" returning id").ToString();
    }

    private static string BuildInsertSql<TDoc>(DocumentMapping mapping,
        IDocumentMetadataBinder<TDoc>[] writeBinders, IDocumentMetadataBinder<TDoc>? revisionBinder)
        where TDoc : notnull
        => BuildInsertBody(mapping, writeBinders, revisionBinder)
           + (revisionBinder is null ? " returning id" : $" returning {NumericRevision.Column}");

    /// <summary>
    ///     The shared <c>insert into … values …</c> head. Parameter marks land in the operations'
    ///     binding order: <c>[tenant,] id, data</c>, then one per client-side binder.
    /// </summary>
    private static string BuildInsertBody<TDoc>(DocumentMapping mapping,
        IDocumentMetadataBinder<TDoc>[] writeBinders, IDocumentMetadataBinder<TDoc>? revisionBinder)
        where TDoc : notnull
    {
        var columns = new List<string>();
        var values = new List<string>();

        if (mapping.IsConjoined)
        {
            columns.Add(StorageConstants.TenantIdColumn);
            values.Add("?");
        }

        columns.Add("id");
        values.Add("?");

        columns.Add("data");
        values.Add("?");

        foreach (var binder in writeBinders)
        {
            columns.Add(binder.ColumnName);

            // The revision binder occupies two slots rather than one, which is the count every shared
            // numeric operation binds for it.
            values.Add(ReferenceEquals(binder, revisionBinder)
                ? NumericRevision.InsertValueSql
                : binder.ValueSql);
        }

        return $"insert into {mapping.QuotedTableName} ({string.Join(", ", columns)}) " +
               $"values ({string.Join(", ", values)})";
    }

    /// <summary>
    ///     <c>update … set … where id = ?</c>. The id is bound after the values here, not before them —
    ///     see the class remarks.
    /// </summary>
    private static string BuildUpdateSql<TDoc>(DocumentMapping mapping,
        IDocumentMetadataBinder<TDoc>[] writeBinders, IDocumentMetadataBinder<TDoc>? revisionBinder,
        bool guarded) where TDoc : notnull
    {
        var assignments = new List<string> { "data = ?" };

        assignments.AddRange(writeBinders.Select(x => ReferenceEquals(x, revisionBinder)
            ? NumericRevision.UpdateAssignmentSql(mapping.QuotedTableName)
            : $"{x.ColumnName} = {x.ValueSql}"));

        var sql = new StringBuilder("update ")
            .Append(mapping.QuotedTableName)
            .Append(" set ")
            .Append(string.Join(", ", assignments))
            .Append(" where id = ?");

        if (mapping.IsConjoined)
        {
            sql.Append(" and ").Append(StorageConstants.TenantIdColumn).Append(" = ?");
        }

        if (revisionBinder is not null)
        {
            // Always present, guarded or not: the shared numeric Update operation binds two trailing
            // slots unconditionally. The id and tenant terms come first, matching the class remarks'
            // note that the update statement moves the id to the back.
            sql.Append(" and ").Append(NumericRevision.GuardSql(mapping.QuotedTableName));

            return sql.Append(" returning ").Append(NumericRevision.Column).ToString();
        }

        if (guarded)
        {
            sql.Append(" and guid_version = ?");
        }

        return sql.Append(" returning id").ToString();
    }
}
