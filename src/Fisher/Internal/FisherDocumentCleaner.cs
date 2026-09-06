using Fisher.Storage;
using Microsoft.Data.Sqlite;

namespace Fisher.Internal;

/// <summary>
///     The <see cref="IDocumentCleaner" /> implementation, scoped by table prefix.
/// </summary>
/// <remarks>
///     <para>
///         Tables are discovered from <c>sqlite_master</c> rather than from the store's registered
///         mappings, because a table can outlive the configuration that created it — a document type
///         removed from the code still has rows on disk, and a clean that skipped them would leave a
///         test suite mysteriously non-empty.
///     </para>
///     <para>
///         Filtering happens in C# rather than through a <c>LIKE</c> predicate on purpose: <c>_</c> is
///         a single-character wildcard in SQL's LIKE and every Fisher prefix contains one, so
///         <c>like 'fi_%'</c> would also match a table called <c>fixtures</c>.
///     </para>
/// </remarks>
internal sealed class FisherDocumentCleaner : IDocumentCleaner
{
    private readonly DocumentStore _store;

    internal FisherDocumentCleaner(DocumentStore store)
    {
        _store = store;
    }

    /// <summary>
    ///     What every table of this store's is named with — the whole of its isolation from another
    ///     logical store in the same file. See <see cref="FisherTableNaming" />.
    /// </summary>
    private string Prefix => FisherTableNaming.PrefixFor(_store.Options.DatabaseSchemaName);

    private string DocumentPrefix => Prefix + "doc_";

    /// <remarks>
    ///     <b>Ordered by foreign key, referencing tables first</b> — the lesson
    ///     <see cref="DeleteAllEventDataAsync" /> learned in fisher#6, one layer over. Weasel's default
    ///     pragma profile enforces foreign keys on every connection Fisher opens, so once one document
    ///     table references another (fisher#38) an unordered sweep fails with
    ///     <c>FOREIGN KEY constraint failed</c> roughly half the time — whichever order
    ///     <c>sqlite_master</c> happens to return.
    /// </remarks>
    public Task DeleteAllDocumentsAsync(CancellationToken token = default)
        => ExecuteAgainstTablesAsync(name => name.StartsWith(DocumentPrefix, StringComparison.Ordinal),
            table => $"delete from \"{table}\"", OrderByForeignKeys, token);

    public Task CleanAsync<T>(CancellationToken token = default) where T : notnull
        => CleanAsync(typeof(T), token);

    /// <remarks>
    ///     Matched against the tables that actually exist rather than issued blind, because a document
    ///     table is created on demand at first write — SQLite resolves a table name when it *prepares*
    ///     a statement, so a delete against a table that was never created fails before any guard in
    ///     the SQL could run. The same reason rebuild teardown reads <c>sqlite_master</c> first.
    /// </remarks>
    public Task CleanAsync(Type documentType, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(documentType);

        var table = _store.Options.Schema.MappingFor(documentType).TableName.Name;

        return ExecuteAgainstTablesAsync(name => name == table,
            name => $"delete from \"{name}\"", token);
    }

    /// <summary>
    ///     Delete every row of event data — events, streams, progression, tags and dead letters.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Named rather than discovered by prefix: the event tables are a fixed, known set, and
    ///         matching on the prefix alone would sweep up every document table too.
    ///     </para>
    ///     <para>
    ///         <strong>Order matters, which is why this does not go through the unordered pass.</strong>
    ///         Each <c>fi_event_tag_*</c> table has a real foreign key to <c>fi_events(seq_id)</c> and
    ///         Weasel's default profile sets <c>PRAGMA foreign_keys = ON</c>, so clearing the events
    ///         first fails with <c>FOREIGN KEY constraint failed</c> as soon as any tagged event exists
    ///         (fisher#6). Tag rows are meaningless without their events, so there is nothing to
    ///         preserve by deleting them — the dead letter table is the deliberate opposite, carrying no
    ///         foreign key precisely so it outlives what it describes.
    ///     </para>
    /// </remarks>
    public Task DeleteAllEventDataAsync(CancellationToken token = default)
    {
        var events = _store.Options.EventGraph;

        var ordered = new List<string>();
        ordered.AddRange(events.TagTypes.Select(events.TagTableName));

        // The natural key lookups (fisher#40). They carry no foreign key, so their position is free —
        // but leaving them behind is not: a cleaned store would still refuse the next stream to claim
        // a key it had already seen, which is the duplicate guard firing on data that is gone.
        ordered.AddRange(_store.Options.Projections.NaturalKeys
            .Select(x => events.QuotedNaturalKeyTableName(x.AggregateType)));

        ordered.Add(events.EventsTableName);
        ordered.Add(events.StreamsTableName);
        ordered.Add(events.ProgressionTableName);
        ordered.Add(events.DeadLetterTableName);

        return ExecuteAgainstOrderedTablesAsync(ordered, table => $"delete from \"{table}\"", token);
    }

    /// <remarks>
    ///     <para>
    ///         Views go too, and they are not merely tidiness: a full-text index's content view
    ///         (fisher#215) is what its virtual table names as its content source, so a removal that
    ///         left the view behind would leave a view over a table that no longer exists. It is
    ///         harmless — the next migration drops and recreates it — but "completely remove all" that
    ///         visibly does not is worse than the work of removing it.
    ///     </para>
    ///     <para>
    ///         An FTS5 table's four shadow tables carry the same prefix and are swept up with it. No
    ///         ordering is needed between them and the virtual table: verified against SQLite 3.50.4,
    ///         dropping the shadows first and the virtual table last succeeds and leaves nothing, and
    ///         so does the other order.
    ///     </para>
    /// </remarks>
    public async Task CompletelyRemoveAllAsync(CancellationToken token = default)
    {
        await ExecuteAgainstTablesAsync(name => name.StartsWith(Prefix, StringComparison.Ordinal),
            table => $"drop table if exists \"{table}\"", token).ConfigureAwait(false);

        await ExecuteAgainstViewsAsync(name => name.StartsWith(Prefix, StringComparison.Ordinal),
            view => $"drop view if exists \"{view}\"", token).ConfigureAwait(false);

        // The tables are gone, so the database's "already created this one" bookkeeping is now a lie —
        // the next Store would otherwise skip the migration and write to a table that no longer exists.
        foreach (var database in _store.Tenancy.AllDatabases())
        {
            database.ForgetEnsuredTables();
        }
    }

    /// <summary>
    ///     Run <paramref name="sqlFor" /> against the named tables in the order given, skipping any that
    ///     do not exist.
    /// </summary>
    /// <remarks>
    ///     The existence filter has to happen here rather than as a predicate in the SQL: SQLite
    ///     resolves a table name when it <em>prepares</em> a statement, so a guard inside the statement
    ///     never gets to run. Same reason the daemon's rebuild teardown reads <c>sqlite_master</c> first.
    /// </remarks>
    private async Task ExecuteAgainstOrderedTablesAsync(IReadOnlyList<string> ordered,
        Func<string, string> sqlFor, CancellationToken token)
    {
        // Every database the store spans, which is one unless it is database-per-tenant (fisher#57).
        // Cleaning only the default there would leave every other tenant's data behind while reporting
        // success, and the caller most likely to notice is a test fixture.
        foreach (var database in _store.Tenancy.AllDatabases())
        {
            await _store.Options.ResiliencePipeline.ExecuteAsync(async ct =>
            {
                await using var connection = await database.OpenConnectionAsync(ct).ConfigureAwait(false);

                var existing = new HashSet<string>(
                    await ReadTableNamesAsync(connection, ct).ConfigureAwait(false),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var table in ordered.Where(existing.Contains))
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = sqlFor(table);
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }, token).ConfigureAwait(false);
        }
    }

    private Task ExecuteAgainstTablesAsync(Func<string, bool> matches, Func<string, string> sqlFor,
        CancellationToken token)
        => ExecuteAgainstTablesAsync(matches, sqlFor, order: null, token);

    private async Task ExecuteAgainstTablesAsync(Func<string, bool> matches, Func<string, string> sqlFor,
        Func<SqliteConnection, List<string>, CancellationToken, Task<List<string>>>? order,
        CancellationToken token)
    {
        foreach (var database in _store.Tenancy.AllDatabases())
        {
            await _store.Options.ResiliencePipeline.ExecuteAsync(async ct =>
            {
                await using var connection = await database.OpenConnectionAsync(ct).ConfigureAwait(false);

                var tables = await ReadTableNamesAsync(connection, ct).ConfigureAwait(false);
                var matching = tables.Where(matches).ToList();

                if (order is not null)
                {
                    matching = await order(connection, matching, ct).ConfigureAwait(false);
                }

                foreach (var table in matching)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = sqlFor(table);
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Order the tables so that a table referencing another comes first.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Read from <c>pragma_foreign_key_list</c> rather than from the store's configuration, so
    ///         it is the database's own account of what references what. That matters: a table left
    ///         behind by an earlier configuration is still enforced, and the store no longer knows about
    ///         it.
    ///     </para>
    ///     <para>
    ///         A plain depth-first topological sort with a visiting set, so a reference cycle degrades
    ///         to "some order" rather than looping. Fisher refuses a self-reference at configuration
    ///         time and a cycle between two document types is not a shape the DSL makes easy, so this is
    ///         a backstop rather than a supported case — and a cycle would fail the delete anyway, which
    ///         is an honest answer.
    ///     </para>
    /// </remarks>
    private static async Task<List<string>> OrderByForeignKeys(SqliteConnection connection,
        List<string> tables, CancellationToken token)
    {
        var references = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"select \"table\" from pragma_foreign_key_list('{table.Replace("'", "''")}')";

            var parents = new List<string>();

            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                parents.Add(reader.GetString(0));
            }

            references[table] = parents;
        }

        var ordered = new List<string>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string table)
        {
            if (!done.Add(table) || !visiting.Add(table))
            {
                return;
            }

            // Children first: everything that references this table has to be emptied before it is.
            foreach (var child in tables.Where(x => references[x].Contains(table, StringComparer.OrdinalIgnoreCase)))
            {
                Visit(child);
            }

            visiting.Remove(table);
            ordered.Add(table);
        }

        foreach (var table in tables)
        {
            Visit(table);
        }

        return ordered;
    }

    private async Task ExecuteAgainstViewsAsync(Func<string, bool> matches, Func<string, string> sqlFor,
        CancellationToken token)
    {
        foreach (var database in _store.Tenancy.AllDatabases())
        {
            await _store.Options.ResiliencePipeline.ExecuteAsync(async ct =>
            {
                await using var connection = await database.OpenConnectionAsync(ct).ConfigureAwait(false);

                foreach (var view in (await ReadNamesAsync(connection, "view", ct).ConfigureAwait(false))
                         .Where(matches))
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = sqlFor(view);
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }, token).ConfigureAwait(false);
        }
    }

    private static Task<List<string>> ReadTableNamesAsync(SqliteConnection connection,
        CancellationToken token)
        => ReadNamesAsync(connection, "table", token);

    private static async Task<List<string>> ReadNamesAsync(SqliteConnection connection, string type,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select name from sqlite_master where type = $type";
        command.Parameters.AddWithValue("$type", type);

        var names = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
