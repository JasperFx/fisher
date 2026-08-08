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

    public Task DeleteAllDocumentsAsync(CancellationToken token = default)
        => ExecuteAgainstTablesAsync(name => name.StartsWith(DocumentPrefix, StringComparison.Ordinal),
            table => $"delete from \"{table}\"", token);

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
        ordered.Add(events.EventsTableName);
        ordered.Add(events.StreamsTableName);
        ordered.Add(events.ProgressionTableName);
        ordered.Add(events.DeadLetterTableName);

        return ExecuteAgainstOrderedTablesAsync(ordered, table => $"delete from \"{table}\"", token);
    }

    public async Task CompletelyRemoveAllAsync(CancellationToken token = default)
    {
        await ExecuteAgainstTablesAsync(name => name.StartsWith(Prefix, StringComparison.Ordinal),
            table => $"drop table if exists \"{table}\"", token).ConfigureAwait(false);

        // The tables are gone, so the database's "already created this one" bookkeeping is now a lie —
        // the next Store would otherwise skip the migration and write to a table that no longer exists.
        _store.Database.ForgetEnsuredTables();
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
        await _store.Options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await _store.Database.OpenConnectionAsync(ct).ConfigureAwait(false);

            var existing = new HashSet<string>(await ReadTableNamesAsync(connection, ct).ConfigureAwait(false),
                StringComparer.OrdinalIgnoreCase);

            foreach (var table in ordered.Where(existing.Contains))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sqlFor(table);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }, token).ConfigureAwait(false);
    }

    private async Task ExecuteAgainstTablesAsync(Func<string, bool> matches, Func<string, string> sqlFor,
        CancellationToken token)
    {
        await _store.Options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await _store.Database.OpenConnectionAsync(ct).ConfigureAwait(false);

            var tables = await ReadTableNamesAsync(connection, ct).ConfigureAwait(false);

            foreach (var table in tables.Where(matches))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sqlFor(table);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }, token).ConfigureAwait(false);
    }

    private static async Task<List<string>> ReadTableNamesAsync(SqliteConnection connection,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select name from sqlite_master where type = 'table'";

        var names = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
