using Weasel.Core;
using Weasel.Sqlite;
using Weasel.Sqlite.Triggers;
using Weasel.Sqlite.Views;

namespace Fisher.Storage.FullText;

/// <summary>
///     The schema objects behind one document type's full-text index: a content view, the FTS5
///     virtual table, and the three triggers that keep it in step with the document table.
/// </summary>
/// <remarks>
///     <para>
///         <b>Trigger-maintained external content, not a write-path index, and the tradeoff is the
///         whole decision.</b> A contentless index maintained by Fisher's own upsert would be less
///         machinery — no view, no triggers, nothing new in the migration — and it would be silently
///         wrong the moment anything wrote the table Fisher did not write: a `QueueSqlCommand`, an
///         `ITransactionParticipant`'s own statement, an EF Core context sharing the file, a
///         `sqlite3` shell, a restored backup edited in place. A stale full-text index does not
///         error; it returns fewer rows than it should, which is indistinguishable from a search that
///         legitimately matched nothing. That is exactly the failure the house rule exists to refuse.
///     </para>
///     <para>
///         A trigger is a database-level object, so it fires for <em>every</em> writer on the
///         connection, Fisher's or not. What it costs is three schema objects per indexed type and an
///         extra write per document write; what it buys is that the only way to desync the index is to
///         drop the triggers.
///     </para>
///     <para>
///         <b>It was also the cheaper option here, which is worth saying because it usually is not.</b>
///         <c>Weasel.Sqlite</c> already ships <c>Trigger</c> and <c>View</c> as first-class schema
///         objects with sqlite_master-based delta detection, so all three of these go through the
///         ordinary migration path with no bespoke plumbing. The write-path alternative would have had
///         to reach into the positional <c>?</c> contract in
///         <see cref="SqliteDocumentStorageDescriptorBuilder" /> — the one place in Fisher where an
///         off-by-one is silent.
///     </para>
///     <para>
///         <b>Why external content over a <em>view</em>.</b> FTS5's <c>content=</c> option names a
///         table whose column names match the index's, and a document table has <c>id</c> and
///         <c>data</c> and nothing called <c>title</c>. The view supplies exactly those names over the
///         <c>json_extract</c> expressions, which is what makes the built-in <c>'rebuild'</c> and
///         <c>'integrity-check'</c> commands work — and <c>'rebuild'</c> is
///         <see cref="Fts5Table" />'s answer to populating an index declared on a store that already
///         holds documents.
///     </para>
///     <para>
///         <b>The index stores terms, not text.</b> External content means FTS5 keeps no copy of the
///         indexed strings, so the cost is the inverted index alone — which matters more here than on
///         either sibling, where the store is not usually the application's own disk footprint.
///     </para>
/// </remarks>
internal static class FullTextSchema
{
    /// <summary>
    ///     The family prefix for everything a full-text index owns — <c>fi_fts_article</c>, not
    ///     <c>fi_doc_article_fts</c>.
    /// </summary>
    /// <remarks>
    ///     <b>Deliberately outside the <c>fi_doc_</c> namespace, and this is a correctness rule rather
    ///     than a preference.</b> An FTS5 virtual table brings four shadow tables of its own
    ///     (<c>_data</c>, <c>_idx</c>, <c>_docsize</c>, <c>_config</c>), all real tables in
    ///     <c>sqlite_master</c>. <c>IDocumentCleaner.DeleteAllDocumentsAsync</c> discovers its targets
    ///     by the <c>fi_doc_</c> prefix rather than from the store's configuration — on purpose, so a
    ///     table outliving the mapping that made it is still cleaned — so under the obvious name it
    ///     would issue <c>delete from</c> against the virtual table and every shadow of it.
    ///     <para>
    ///         Verified against SQLite 3.50.4: a <c>DELETE</c> on an external-content FTS5 table does
    ///         <em>not</em> error. It reports success and leaves the index in a state nothing checked,
    ///         which is the silent shape this whole feature is written to avoid. A prefix the document
    ///         sweep cannot see removes the possibility rather than adding a filter that a future
    ///         sweep could forget.
    ///     </para>
    /// </remarks>
    private static string SuffixFor(DocumentMapping mapping) => $"fts_{mapping.Alias}";

    /// <summary>The FTS5 virtual table's name for a document mapping.</summary>
    internal static SqliteObjectName TableNameFor(DocumentMapping mapping)
        => FisherTableNaming.ObjectFor(mapping.StoreOptions.DatabaseSchemaName, SuffixFor(mapping));

    /// <summary>The content view's name.</summary>
    internal static SqliteObjectName ViewNameFor(DocumentMapping mapping)
        => FisherTableNaming.ObjectFor(mapping.StoreOptions.DatabaseSchemaName,
            $"{SuffixFor(mapping)}_src");

    /// <summary>
    ///     Every schema object the index needs, in creation order.
    /// </summary>
    /// <remarks>
    ///     Order is load-bearing: the view has to exist before the virtual table that names it as its
    ///     content source, and the virtual table before the triggers that write into it. Weasel
    ///     applies a feature's objects in the order they are yielded, which is what makes stating it
    ///     here sufficient.
    /// </remarks>
    internal static IEnumerable<ISchemaObject> ObjectsFor(DocumentMapping mapping)
    {
        var index = mapping.FullTextIndex
                    ?? throw new InvalidOperationException(
                        $"'{mapping.DocumentType.Name}' declares no full-text index.");

        var table = mapping.TableName.Name;
        var ftsName = TableNameFor(mapping);
        var viewName = ViewNameFor(mapping);

        var columns = index.ColumnNames;
        var expressions = index.Expressions(mapping.MembersFor(), "data");

        var projection = string.Join(", ",
            columns.Select((column, i) => $"{expressions[i]} as {SchemaUtils.QuoteName(column)}"));

        yield return new View(viewName,
            $"select rowid as rowid, {projection} from {SchemaUtils.QuoteName(table)}");

        yield return new Fts5Table(ftsName, columns, viewName.Name, index.Tokenizer);

        var fts = SchemaUtils.QuoteName(ftsName.Name);
        var columnList = string.Join(", ", columns.Select(SchemaUtils.QuoteName));

        var insertValues = string.Join(", ", expressions.Select(x => Rebind(x, "new")));
        var deleteValues = string.Join(", ", expressions.Select(x => Rebind(x, "old")));

        var insert = $"INSERT INTO {fts}(rowid, {columnList}) VALUES (new.rowid, {insertValues})";

        // FTS5's documented way to retract a row from an external-content index: hand it back the
        // values that were indexed. It cannot read them itself — the content view now reflects the
        // NEW row, or no row at all — which is why the old values are recomputed from `old.data`
        // rather than selected.
        var retract = $"INSERT INTO {fts}({fts}, rowid, {columnList}) "
                      + $"VALUES ('delete', old.rowid, {deleteValues})";

        yield return TriggerFor(mapping, "ai", table, TriggerEvents.Insert, insert);
        yield return TriggerFor(mapping, "ad", table, TriggerEvents.Delete, retract);

        // Fisher writes documents with INSERT … ON CONFLICT DO UPDATE, which fires the UPDATE
        // trigger rather than the INSERT one for an existing row — so this is the path a store
        // overwhelmingly takes, not an edge case. Verified against SQLite 3.50.4.
        yield return TriggerFor(mapping, "au", table, TriggerEvents.Update, $"{retract}; {insert}");
    }

    /// <summary>
    ///     One trigger, always <c>AFTER</c> and always on exactly one event.
    /// </summary>
    /// <remarks>
    ///     Three triggers rather than one covering all three events, because SQLite's trigger syntax
    ///     takes a single event — and because the bodies genuinely differ: an insert only adds terms,
    ///     a delete only retracts them, and an update has to do both in that order.
    /// </remarks>
    private static Trigger TriggerFor(DocumentMapping mapping, string suffix, string table,
        TriggerEvents events, string body)
    {
        var name = FisherTableNaming.TableName(mapping.StoreOptions.DatabaseSchemaName,
            $"{SuffixFor(mapping)}_{suffix}");

        return new Trigger(new SqliteObjectName(FisherTableNaming.DefaultSchemaName, name),
            new SqliteObjectName(FisherTableNaming.DefaultSchemaName, table), body)
        {
            Timing = TriggerTiming.After,
            Events = events
        };
    }

    /// <summary>
    ///     Rewrites a locator built against the document table so it reads the trigger's <c>new</c> or
    ///     <c>old</c> row instead.
    /// </summary>
    /// <remarks>
    ///     A locator is <c>json_extract(data, '$.title')</c> and a trigger body needs
    ///     <c>json_extract(new.data, '$.title')</c>. Qualifying the <c>data</c> reference rather than
    ///     the whole expression is the same rule the join alias follows — the alias belongs inside
    ///     <c>json_extract</c>, on the column, not on its result.
    /// </remarks>
    private static string Rebind(string expression, string row)
        => expression == "data" ? $"{row}.data" : expression.Replace("data,", $"{row}.data,");
}
