using System.Text;
using Fisher.Serialization;
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
        var writeBinders = new List<IDocumentMetadataBinder<TDoc>>();
        var readBinders = new List<IDocumentMetadataBinder<TDoc>>();

        DocumentVersionBinder<TDoc>? versionBinder = null;
        var versionReadIndex = -1;

        // guid_version carries optimistic concurrency in its own column so a version check never has
        // to parse the JSON body. Read back as well as written: the session's version tracker needs
        // the value it loaded in order to guard the next write with it.
        if (mapping.UseOptimisticConcurrency)
        {
            versionBinder = new DocumentVersionBinder<TDoc>("guid_version", dialect, versionMember: null);
            writeBinders.Add(versionBinder);
            versionReadIndex = readBinders.Count;
            readBinders.Add(versionBinder);
        }

        // Written on every save, never selected. It is what a hierarchy discriminator would build on.
        writeBinders.Add(new DocumentDotNetTypeBinder<TDoc>("dotnet_type", dialect));

        // Server-side: contributes the timestamp expression rather than a parameter.
        writeBinders.Add(new DocumentLastModifiedBinder<TDoc>(
            "last_modified", lastModifiedMember: null, SqliteTimestamp.NowExpression));

        // Soft delete's two columns are written by every save and never selected. Both binders write
        // the *live* value — false and null — which is what makes storing a soft-deleted document
        // undelete it, in the insert branch and (through excluded.*) in the update branch alike.
        //
        // Neither is given a member to project onto, matching guid_version and last_modified above:
        // Fisher has no document metadata member mapping at all, so a document implementing
        // ISoftDeleted is opted into the behaviour without having its Deleted/DeletedAt populated on
        // read. Tracked as fisher#11.
        if (mapping.IsSoftDeleted)
        {
            writeBinders.Add(new DocumentSoftDeletedBinder<TDoc>(
                SoftDelete.IsDeletedColumn, dialect, member: null));

            writeBinders.Add(new DocumentSoftDeletedAtBinder<TDoc>(
                SoftDelete.DeletedAtColumn, dialect, member: null));
        }

        var writeArray = writeBinders.ToArray();
        var readArray = readBinders.ToArray();
        var clientSide = writeArray.Where(x => !x.IsServerSide).ToArray();

        // QueryOnly never writes, so it has no use for a version it cannot act on — and no member to
        // project it onto, since Fisher has no metadata member mapping yet.
        var queryOnlyReadArray = versionReadIndex >= 0
            ? readArray.Where((_, i) => i != versionReadIndex).ToArray()
            : readArray;

        var guarded = mapping.ConcurrencyMode == ConcurrencyMode.Optimistic;

        return new DocumentStorageDescriptor<TDoc, TId>(
            identification,
            serializer: StorageSerializerAdapter.For(options.Serializer),
            dialect: dialect,
            clientSideWriteBinders: clientSide,
            writeBinders: writeArray,
            readBinders: readArray,
            queryOnlyReadBinders: queryOnlyReadArray,
            upsertSql: BuildUpsertSql(mapping, writeArray, guarded),
            insertSql: BuildInsertSql(mapping, writeArray),
            updateSql: BuildUpdateSql(mapping, writeArray, guarded),
            overwriteSql: BuildUpsertSql(mapping, writeArray, guarded: false),
            isConjoined: mapping.IsConjoined,
            concurrencyMode: mapping.ConcurrencyMode,
            versionBinder: versionBinder,
            revisionBinder: null,
            versionReadIndex: versionReadIndex,
            resolveDocumentType: null,
            docTypeReadIndex: -1,
            tableName: mapping.TableName.QualifiedName);
    }

    /// <summary>
    ///     <c>insert … on conflict do update</c>. The conflict target is the table's primary key, which
    ///     is the tenant/id pair under conjoined tenancy.
    /// </summary>
    private static string BuildUpsertSql<TDoc>(DocumentMapping mapping,
        IDocumentMetadataBinder<TDoc>[] writeBinders, bool guarded) where TDoc : notnull
    {
        var sql = new StringBuilder(BuildInsertBody(mapping, writeBinders));

        sql.Append(" on conflict (");
        sql.Append(mapping.IsConjoined ? $"{StorageConstants.TenantIdColumn}, id" : "id");
        sql.Append(") do update set data = excluded.data");

        foreach (var binder in writeBinders)
        {
            // excluded.* rather than repeating each binder's ValueSql: for a server-side expression
            // excluded already holds the value it computed for this statement, so one form covers
            // client-side and server-side columns alike and cannot drift from the insert branch.
            sql.Append(", ").Append(binder.ColumnName).Append(" = excluded.").Append(binder.ColumnName);
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
        IDocumentMetadataBinder<TDoc>[] writeBinders) where TDoc : notnull
        => BuildInsertBody(mapping, writeBinders) + " returning id";

    /// <summary>
    ///     The shared <c>insert into … values …</c> head. Parameter marks land in the operations'
    ///     binding order: <c>[tenant,] id, data</c>, then one per client-side binder.
    /// </summary>
    private static string BuildInsertBody<TDoc>(DocumentMapping mapping,
        IDocumentMetadataBinder<TDoc>[] writeBinders) where TDoc : notnull
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
            values.Add(binder.ValueSql);
        }

        return $"insert into {mapping.QuotedTableName} ({string.Join(", ", columns)}) " +
               $"values ({string.Join(", ", values)})";
    }

    /// <summary>
    ///     <c>update … set … where id = ?</c>. The id is bound after the values here, not before them —
    ///     see the class remarks.
    /// </summary>
    private static string BuildUpdateSql<TDoc>(DocumentMapping mapping,
        IDocumentMetadataBinder<TDoc>[] writeBinders, bool guarded) where TDoc : notnull
    {
        var assignments = new List<string> { "data = ?" };
        assignments.AddRange(writeBinders.Select(x => $"{x.ColumnName} = {x.ValueSql}"));

        var sql = new StringBuilder("update ")
            .Append(mapping.QuotedTableName)
            .Append(" set ")
            .Append(string.Join(", ", assignments))
            .Append(" where id = ?");

        if (mapping.IsConjoined)
        {
            sql.Append(" and ").Append(StorageConstants.TenantIdColumn).Append(" = ?");
        }

        if (guarded)
        {
            sql.Append(" and guid_version = ?");
        }

        return sql.Append(" returning id").ToString();
    }
}
