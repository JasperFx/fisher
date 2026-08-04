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

    public Task DeleteAllEventDataAsync(CancellationToken token = default)
    {
        var events = _store.Options.EventGraph;

        // Named rather than discovered by prefix: the event tables are a fixed, known set, and
        // matching on the prefix alone would sweep up every document table too.
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            events.EventsTableName, events.StreamsTableName, events.ProgressionTableName
        };

        return ExecuteAgainstTablesAsync(tables.Contains, table => $"delete from \"{table}\"", token);
    }

    public async Task CompletelyRemoveAllAsync(CancellationToken token = default)
    {
        await ExecuteAgainstTablesAsync(name => name.StartsWith(Prefix, StringComparison.Ordinal),
            table => $"drop table if exists \"{table}\"", token).ConfigureAwait(false);

        // The tables are gone, so the database's "already created this one" bookkeeping is now a lie —
        // the next Store would otherwise skip the migration and write to a table that no longer exists.
        _store.Database.ForgetEnsuredTables();
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
