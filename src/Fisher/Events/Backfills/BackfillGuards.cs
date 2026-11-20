using Fisher.Projections;
using Fisher.Storage;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;
using Weasel.Sqlite;

namespace Fisher.Events.Backfills;

/// <summary>
///     fisher#643: the three places Fisher refuses a backfill rather than letting it patch nothing or apply an event
///     twice. Each refusal names what to do instead.
/// </summary>
internal static class BackfillGuards
{
    /// <summary>
    ///     At startup: a backfill runs as a Wolverine job up to an async projection's prior-version mark, so it needs
    ///     a job host and an asynchronous projection registered on its own.
    /// </summary>
    public static void AssertDeclarationsCanRun(StoreOptions options)
    {
        foreach (var projection in options.Projections.All)
        {
            if (projection is IFisherBackfillSource { Backfills.Count: > 0 } source)
            {
                if (!options.EventGraph.HasJobHost)
                {
                    throw new InvalidOperationException(
                        $"Projection {source.Name} declares a backfill, which runs as a Wolverine long-running job, "
                        + "and no job host manages this store. Integrate the store with Wolverine, or remove the "
                        + "Backfill(...) declaration.");
                }

                if (projection is ProjectionBase { Lifecycle: not ProjectionLifecycle.Async } registered)
                {
                    throw new InvalidOperationException(
                        $"Projection {source.Name} declares a backfill but is registered as {registered.Lifecycle}. "
                        + "A backfill runs up to the mark an asynchronous projection's prior version reached, so "
                        + "register the projection as Async.");
                }
            }

            if (projection is JasperFx.Events.Projections.Composite.CompositeProjection<IDocumentSession, IQuerySession> composite
                && composite.AllProjections().OfType<IFisherBackfillSource>().FirstOrDefault(x => x.Backfills.Count > 0)
                    is { } member)
            {
                throw new InvalidOperationException(
                    $"Projection {member.Name} declares a backfill inside the composite projection {composite.Name}. "
                    + "A backfill is found by its projection's name, so declare it on a projection registered on its own.");
            }
        }
    }

    /// <summary>
    ///     The backfill source whose ceiling is stored under <paramref name="key" />, if any.
    /// </summary>
    public static IFisherBackfillSource? SourceForCeilingKey(StoreOptions options, string key)
    {
        if (!key.StartsWith("backfill:", StringComparison.Ordinal)) return null;

        return options.Projections.All.OfType<IFisherBackfillSource>()
            .FirstOrDefault(source => source.Backfills.Any(b =>
                EventRangeMarks.BackfillKey(source.Name, b.Name) == key));
    }

    /// <summary>
    ///     When a backfill's ceiling is first captured, by the live version's start or by the backfill job: refuse
    ///     when the prior version whose mark it captures wrote its documents to another table (fisher#638), where
    ///     neither the live version nor the backfill would see them.
    /// </summary>
    /// <remarks>
    ///     Version 2 and above of an aggregation projection writes a table of its own (<c>fi_doc_quest_2</c>), so a
    ///     version that starts from version 1's mark would build its documents from the events after the mark alone,
    ///     and the backfill would find nothing to patch. Fisher does not copy documents between versions' tables.
    ///     The refusal applies when the prior version's table holds documents, or when this version has already
    ///     run without the mark.
    /// </remarks>
    public static async Task AssertPriorVersionDocumentsAreReachableAsync(StoreOptions options,
        IFisherBackfillSource source, SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken token)
    {
        if (source.Version <= 1) return;

        var events = options.EventGraph;

        uint? priorVersion = null;
        long priorMark = 0;
        var priorShards = new List<string>();
        var ownProgress = 0L;

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"select name, last_seq_id from {events.ProgressionTableName}";

            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var identity = reader.GetString(0);
                if (!ShardName.TryParse(identity, out var parsed) || parsed == null) continue;
                if (!parsed.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase)) continue;

                var sequence = reader.GetInt64(1);
                if (parsed.Version == source.Version)
                {
                    ownProgress = Math.Max(ownProgress, sequence);
                }
                else if (parsed.Version < source.Version)
                {
                    priorShards.Add(identity);
                    if (priorVersion == null || parsed.Version > priorVersion)
                    {
                        priorVersion = parsed.Version;
                        priorMark = sequence;
                    }
                    else if (parsed.Version == priorVersion)
                    {
                        priorMark = Math.Max(priorMark, sequence);
                    }
                }
            }
        }

        // No prior version on record, or one that processed nothing: there is nothing from before the mark to reach
        if (priorVersion is not { } prior || priorMark == 0) return;

        var mapping = options.Schema.MappingFor(source.DocumentType);
        var liveTable = mapping.TableName.Name;
        var priorTable = mapping.TableNameForProjectionVersion(prior).Name;
        if (liveTable.Equals(priorTable, StringComparison.OrdinalIgnoreCase)) return;

        var priorTableHasDocuments = await tableHasRowsAsync(connection, transaction, priorTable, token)
            .ConfigureAwait(false);

        if (!priorTableHasDocuments && ownProgress == 0) return;

        var reason = priorTableHasDocuments
            ? $"version {prior}'s documents are in {priorTable}"
            : $"version {source.Version} has already processed events into {liveTable} up to sequence {ownProgress} "
              + $"without the mark";

        throw new InvalidOperationException(
            $"Projection {source.Name} (version {source.Version}) would start from version {prior}'s mark "
            + $"(sequence {priorMark}), but Fisher gives version {source.Version} its own document table "
            + $"({liveTable}) and {reason}. Neither the live version nor the backfill would see those documents, and "
            + "Fisher does not copy documents between versions' tables. Declare the backfill without bumping the "
            + $"version (keep version {prior}): Fisher runs one daemon per database, so its own progress is an exact "
            + $"mark. If version {prior} is retired, delete its progression rows ({string.Join(", ", priorShards)}) "
            + "and the mark falls back to this version's own progress. Otherwise drop the Backfill(...) and "
            + $"SubscribeFromPriorVersionMark(...) declarations and rebuild version {source.Version}.");
    }

    /// <summary>
    ///     Before a rebuild of <paramref name="projectionName" /> deletes anything: refuse while a backfill it
    ///     declares has not finished in this database.
    /// </summary>
    /// <remarks>
    ///     A rebuild replays every event through the live version's <c>Apply</c> methods, which already fill the new
    ///     member, so the backfill's later pages would apply those events a second time. Not started counts as not
    ///     finished: its ceiling would be captured after the rebuild and it would patch the rebuilt documents.
    /// </remarks>
    public static async Task AssertNoUnfinishedBackfillAsync(StoreOptions options, string projectionName,
        string action, SqliteConnection connection, SqliteTransaction? transaction, CancellationToken token)
    {
        var source = options.Projections.All.OfType<IFisherBackfillSource>()
            .FirstOrDefault(x => x.Name == projectionName);
        if (source is null || source.Backfills.Count == 0) return;

        if (!(await tableExistsAsync(connection, transaction,
                FisherTableNaming.TableName(options.EventGraph.DatabaseSchemaName, Schema.EventRangeMarksTable.TableSuffix), token)
                .ConfigureAwait(false)))
        {
            throw unfinished(source, source.Backfills[0], action, "it has not started");
        }

        foreach (var backfill in source.Backfills)
        {
            var ceilingKey = EventRangeMarks.BackfillKey(source.Name, backfill.Name);
            var ceilings = await readMarksAsync(options, connection, transaction, ceilingKey, token).ConfigureAwait(false);
            if (ceilings.Count == 0)
            {
                throw unfinished(source, backfill, action, "it has not started");
            }

            var cursors = await readMarksAsync(options, connection, transaction, EventRangeMarks.CursorKey(ceilingKey),
                token).ConfigureAwait(false);

            foreach (var (tenant, ceiling) in ceilings)
            {
                var cursor = cursors.TryGetValue(tenant, out var stored) ? stored : 0;
                if (cursor < ceiling)
                {
                    var who = tenant.Length == 0 ? "" : $" for tenant '{tenant}'";
                    throw unfinished(source, backfill, action,
                        $"its cursor{who} is at sequence {cursor} of its ceiling {ceiling}");
                }
            }
        }
    }

    private static InvalidOperationException unfinished(IFisherBackfillSource source, IFisherBackfill backfill,
        string action, string state)
    {
        var ceilingKey = EventRangeMarks.BackfillKey(source.Name, backfill.Name);
        return new InvalidOperationException(
            $"Cannot {action} projection {source.Name}: its backfill '{backfill.Name}' has not finished in this "
            + $"database ({state}). A rebuild applies every event through version {source.Version}'s Apply methods, "
            + "so the backfill's remaining pages would apply those events a second time. Let the backfill job finish "
            + "first. If the rebuild is meant to fill the member instead, remove the Backfill(...) declaration, or mark "
            + $"the backfill complete by setting the event-range mark '{EventRangeMarks.CursorKey(ceilingKey)}' to the "
            + $"mark '{ceilingKey}' (setting both when neither is stored) with IEventDatabase.SetEventRangeMarkAsync.");
    }

    private static async Task<Dictionary<string, long>> readMarksAsync(StoreOptions options,
        SqliteConnection connection, SqliteTransaction? transaction, string key, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"select tenant_id, mark from {options.EventGraph.EventRangeMarksTableName} where mark_key = @key";
        command.Parameters.AddWithValue("@key", key);

        var marks = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            marks[reader.GetString(0)] = reader.GetInt64(1);
        }

        return marks;
    }

    private static async Task<bool> tableExistsAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string table, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select count(*) from sqlite_master where type = 'table' and name = @name collate nocase";
        command.Parameters.AddWithValue("@name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) > 0;
    }

    private static async Task<bool> tableHasRowsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, CancellationToken token)
    {
        if (!await tableExistsAsync(connection, transaction, table, token).ConfigureAwait(false)) return false;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"select exists (select 1 from {SchemaUtils.QuoteName(table)})";
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) > 0;
    }
}
