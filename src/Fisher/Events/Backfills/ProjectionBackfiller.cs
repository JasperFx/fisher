using System.Diagnostics.CodeAnalysis;
using System.Text;
using Fisher.Events.Internal;
using Fisher.Internal;
using Fisher.Projections;
using Fisher.Storage;
using JasperFx.Events;
using Microsoft.Data.Sqlite;
using Weasel.Storage;

namespace Fisher.Events.Backfills;

/// <summary>
///     Runs one <see cref="BackfillRun" /> against one Fisher database (jasperfx#1222, fisher#643): the part of a
///     projection backfill a store implements for the Wolverine job that drives it.
/// </summary>
/// <remarks>
///     <para>
///         Reads only the events the backfill handles, from each tenant's cursor up to its ceiling, outside the
///         daemon. Archived events are left out, as the daemon leaves them out. An event whose document is missing or
///         deleted is visited and not applied.
///     </para>
///     <para>
///         <b>Each page is read, applied and written in one <c>BEGIN IMMEDIATE</c> transaction, with its cursor.</b>
///         The documents are loaded inside that transaction too, so no other writer can change one between the load
///         and the write. The page starts from the stored cursor when it is ahead of the run's own position, so a
///         second run of the same backfill, or a cursor an operator moved to the ceiling, is never applied twice.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "Class-level: reads events through the daemon's row reader, as the stored-event rewrite does. Event types are preserved by EventGraph registration per the AOT publishing guide.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Class-level: event deserialization is annotated RDC. AOT consumers supply a source-generator-backed ISerializer.")]
internal sealed class ProjectionBackfiller
{
    private readonly StoreOptions _options;
    private readonly EventGraph _events;
    private readonly IFisherBackfillRunner _runner;
    private readonly IReadOnlyList<string> _typeNames;

    private ProjectionBackfiller(StoreOptions options, IFisherBackfillRunner runner, IReadOnlyList<string> typeNames)
    {
        _options = options;
        _events = options.EventGraph;
        _runner = runner;
        _typeNames = typeNames;
    }

    /// <summary>
    ///     The registered projection named <paramref name="projectionName" /> and its backfill
    ///     <paramref name="backfillName" />, or an <see cref="InvalidOperationException" /> naming what is missing.
    /// </summary>
    public static (IFisherBackfillSource Source, IFisherBackfill Backfill) Find(StoreOptions options,
        string projectionName, string backfillName)
    {
        var source = options.Projections.All.OfType<IFisherBackfillSource>()
                         .FirstOrDefault(x => x.Name == projectionName)
                     ?? throw new InvalidOperationException(
                         $"No single-stream projection named '{projectionName}' that declares a backfill is registered on this store.");

        var backfill = source.Backfills.FirstOrDefault(x => x.Name == backfillName)
                       ?? throw new InvalidOperationException(
                           $"Projection {projectionName} declares no backfill named '{backfillName}'.");

        return (source, backfill);
    }

    public static ProjectionBackfiller For(StoreOptions options, IFisherBackfillSource source, IFisherBackfill backfill)
    {
        var events = options.EventGraph;
        var known = events.AllKnownEventTypes().Select(x => x.EventType).ToList();

        // A handler declared for an interface or a base type reaches every registered event type that implements it
        var types = backfill.EventTypes
            .SelectMany(handled => known.Where(handled.IsAssignableFrom)
                .Concat(handled is { IsInterface: false, IsAbstract: false } ? [handled] : []))
            .Distinct()
            .ToList();

        var names = types.Count == 0
            ? []
            : EventTypeAllowList.For(events, types).AliasesIncludingUpcastSources.ToList();

        if (names.Count == 0)
        {
            throw new InvalidOperationException(
                $"Backfill '{backfill.Name}' on projection {source.Name} handles no event type this store knows. "
                + "Declare it with On<TEvent>(...) for a registered event type.");
        }

        return new ProjectionBackfiller(options, source.RunnerFor(backfill), names);
    }

    /// <summary>
    ///     Each tenant in turn, from its cursor up to and including its ceiling, one page at a time.
    /// </summary>
    public async Task<IReadOnlyList<BackfillProgress>> ExecuteRunAsync(FisherDatabase database, BackfillRun run,
        CancellationToken token)
    {
        if (run.BatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(run), "The backfill's BatchSize must be greater than 0.");
        }

        await database.EnsureEventStorageAsync(token).ConfigureAwait(false);

        // Before any page's transaction: creating the table is a migration on its own connection
        await database.EnsureDocumentTableAsync(_runner.DocumentType, token).ConfigureAwait(false);

        var totals = new List<BackfillProgress>(run.Cursors.Count);
        foreach (var cursor in run.Cursors)
        {
            // A tenant's own rows when events are conjoined. Otherwise the tenant's database is the selection.
            var tenantFilter = _events.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined
                ? cursor.TenantId
                : null;

            var total = new BackfillProgress(cursor.TenantId, cursor.ResumeAfter, 0, 0, cursor.IsComplete);
            var position = cursor.ResumeAfter;

            while (!total.Completed)
            {
                token.ThrowIfCancellationRequested();

                var from = position;
                var page = await _options.ResiliencePipeline.ExecuteAsync(
                        async ct => await backfillPageAsync(database, run, cursor, tenantFilter, from, ct)
                            .ConfigureAwait(false), token)
                    .ConfigureAwait(false);

                position = page.Sequence;

                total = total with
                {
                    Sequence = page.Sequence,
                    Visited = total.Visited + page.Visited,
                    Applied = total.Applied + page.Applied,
                    Completed = page.Completed
                };

                if (run.OnPage != null)
                {
                    await run.OnPage(page, token).ConfigureAwait(false);
                }
            }

            totals.Add(total);
        }

        return totals;
    }

    /// <summary>
    ///     One page and its cursor, in one transaction. Inside the resilience pipeline as a whole, so a busy
    ///     database retries the page from its read: nothing of it was committed.
    /// </summary>
    private async Task<BackfillProgress> backfillPageAsync(FisherDatabase database, BackfillRun run,
        EventRangeCursor cursor, string? tenantFilter, long position, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token).ConfigureAwait(false);

        var waitedFrom = _options.OpenTelemetry.StartWriteLockWait();
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token).ConfigureAwait(false);
        _options.OpenTelemetry.RecordWriteLockWait(waitedFrom, Services.OpenTelemetryOptions.BackfillHolder);

        // The stored cursor wins when it is ahead: another run of this backfill went further, or an operator
        // marked it complete
        var stored = await readMarkAsync(connection, transaction, run.CursorKey, cursor.TenantId, token)
            .ConfigureAwait(false);
        if (stored is { } storedCursor && storedCursor > position) position = storedCursor;

        if (position >= cursor.StopAfter)
        {
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new BackfillProgress(cursor.TenantId, cursor.StopAfter, 0, 0, true);
        }

        var (events, rows, lastSequence) = await readPageAsync(connection, transaction, tenantFilter, position,
            cursor.StopAfter, run.BatchSize, token).ConfigureAwait(false);

        long applied = 0;
        foreach (var group in events.GroupBy(x => x.TenantId))
        {
            var session = new FisherSession(_options, database, new SessionOptions
            {
                TenantId = database.TenantId ?? group.Key ?? JasperFx.StorageConstants.DefaultTenantId,
                Transaction = transaction
            });

            await using (session.ConfigureAwait(false))
            {
                applied += await _runner.ApplyAsync(session, group.ToList(), token).ConfigureAwait(false);
                await session.ExecuteOperationsAsync(connection, transaction, session.TakePendingOperations(), token)
                    .ConfigureAwait(false);
            }
        }

        var completed = rows < run.BatchSize;

        // The ceiling on a tenant's last page, so the stored cursor says the tenant is done
        var sequence = completed ? cursor.StopAfter : lastSequence;

        await using (var mark = connection.CreateCommand())
        {
            mark.Transaction = transaction;
            database.ConfigureSetEventRangeMark(mark, run.CursorKey, cursor.TenantId, sequence);
            await mark.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        await transaction.CommitAsync(token).ConfigureAwait(false);

        return new BackfillProgress(cursor.TenantId, sequence, rows, applied, completed);
    }

    private async Task<long?> readMarkAsync(SqliteConnection connection, SqliteTransaction transaction, string key,
        string? tenantId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"select mark from {_events.EventRangeMarksTableName} where mark_key = @key and tenant_id = @tenant";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@tenant", tenantId ?? Schema.EventRangeMarksTable.StoreGlobalTenant);

        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is long mark ? mark : null;
    }

    private async Task<(List<IEvent> Events, int Rows, long LastSequence)> readPageAsync(SqliteConnection connection, SqliteTransaction transaction,
        string? tenantId, long after, long stopAfter, int batchSize, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = _options.CommandTimeout;

        var sql = new StringBuilder("select ")
            .Append(FisherEventsRowReader.ComposeSelectColumns(_options.Events))
            .Append(" from ").Append(_events.EventsTableName)
            .Append(" where seq_id > @after and seq_id <= @stop_after and is_archived = 0")
            .Append(" and type in (select value from json_each(@types))");
        command.Parameters.AddWithValue("@after", after);
        command.Parameters.AddWithValue("@stop_after", stopAfter);
        command.Parameters.AddWithValue("@types", SqliteJsonArrays.Write(_typeNames));

        if (tenantId != null)
        {
            sql.Append(" and tenant_id = @tenant_id");
            command.Parameters.AddWithValue("@tenant_id", tenantId);
        }

        sql.Append(" order by seq_id limit @batch_size");
        command.Parameters.AddWithValue("@batch_size", batchSize);
        command.CommandText = sql.ToString();

        var context = new EventHydrationContext(_events, _options.Serializer, string.Empty,
            JasperFx.StorageConstants.DefaultTenantId);
        var slots = MetadataSlots.For(_options.Events);
        var isGuid = _events.StreamIdentity == StreamIdentity.AsGuid;

        var events = new List<IEvent>(batchSize);
        var rows = 0;
        long lastSequence = after;

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            // Read before the event, so the cursor moves past a row that does not read back as one
            rows++;
            lastSequence = reader.GetInt64(0);

            var @event = await FisherEventsRowReader.ReadEventAcrossStreams(reader, context, slots, isGuid, token)
                .ConfigureAwait(false);
            if (@event != null) events.Add(@event);
        }

        return (events, rows, lastSequence);
    }
}
