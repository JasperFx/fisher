using System.Diagnostics.CodeAnalysis;
using System.Text;
using Fisher.Events.Internal;
using Fisher.Events.Schema;
using Fisher.Storage;
using JasperFx.Events;
using Microsoft.Data.Sqlite;
using Weasel.Storage;

namespace Fisher.Events.Rewriting;

/// <summary>
///     Runs one <see cref="EventRewriteRun" /> against one Fisher database (jasperfx#1215, fisher#643): the part of a
///     stored-event rewrite a store implements for the Wolverine job that drives it. Ported from Polecat's
///     <c>StoredEventRewriter</c>.
/// </summary>
/// <remarks>
///     <para>
///         Rows are hydrated by the daemon's own row reader, so upcasting, binary payloads and the serializer are
///         what a projection sees. Each one goes through the named transforms and is written back the way an append
///         would store it today: the current type alias and .NET type, <c>data</c> through the store serializer, or
///         the <c>{}</c> placeholder with the payload in <c>data_binary</c> for a binary event. A row that would come
///         out the same is left alone.
///     </para>
///     <para>
///         <b>Each page is read and written in one <c>BEGIN IMMEDIATE</c> transaction, with its cursor.</b> That is
///         where this differs from Polecat. SQL Server has to guard each update with a hash of the row as it was
///         read, because another writer can change the row in between. SQLite has one writer per file, and the
///         page's transaction holds the write lock from before the read, so nothing can change a row between the
///         read and the write and <see cref="EventRewriteProgress.Skipped" /> is always 0. The cost is that
///         appends wait while a page is transformed, which is what <see cref="EventRewriteDefinition.BatchSize" />
///         and <see cref="EventRewriteDefinition.PauseBetweenBatches" /> are for.
///     </para>
///     <para>
///         Archived events are rewritten too: a rewrite changes what is stored, and an archived event is stored.
///     </para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "Class-level: reads events through the daemon's row reader and writes them through ISerializer, as appends do. Event types are preserved by EventGraph registration per the AOT publishing guide.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Class-level: ISerializer and event deserialization are annotated RDC. AOT consumers supply a source-generator-backed ISerializer.")]
internal sealed class StoredEventRewriter
{
    private readonly StoreOptions _options;
    private readonly EventGraph _events;
    private readonly EventRewriteDefinition _definition;
    private readonly IReadOnlyList<EventRewriteTransform> _transforms;
    private readonly IReadOnlyList<string>? _typeNames;

    public StoredEventRewriter(StoreOptions options, EventRewriteDefinition definition,
        IReadOnlyList<EventRewriteTransform> transforms)
    {
        if (definition.BatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(definition), "The rewrite's BatchSize must be greater than 0.");
        }

        if (definition.PauseBetweenBatches < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(definition),
                "The rewrite's PauseBetweenBatches cannot be negative.");
        }

        _options = options;
        _events = options.EventGraph;
        _definition = definition;
        _transforms = transforms;

        if (!definition.AllEventTypes)
        {
            _typeNames = typeNamesFor(_events, definition, transforms);

            if (_typeNames.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Event rewrite '{definition.Name}' selects no events. Name the event types to rewrite, a "
                    + "transform, or set AllEventTypes.");
            }
        }
    }

    /// <summary>
    ///     The stored type names a definition selects: the names it lists, each event type a transform applies to,
    ///     and every stored name an upcaster reads into one of those types.
    /// </summary>
    /// <remarks>
    ///     A listed name the store knows as an event type is resolved to that type, so the names upcast into it come
    ///     along. A listed name it does not know is kept as it is: it may be a stored name with no type of its own.
    /// </remarks>
    private static IReadOnlyList<string> typeNamesFor(EventGraph events, EventRewriteDefinition definition,
        IReadOnlyList<EventRewriteTransform> transforms)
    {
        var known = events.AllKnownEventTypes();
        var types = new List<Type>();
        var storedNames = new List<string>();

        foreach (var name in definition.EventTypes.Distinct(StringComparer.Ordinal))
        {
            if (known.FirstOrDefault(x => x.EventTypeName == name) is { } mapping)
            {
                types.Add(mapping.EventType);
            }
            else
            {
                storedNames.Add(name);
            }
        }

        foreach (var transform in transforms)
        {
            if (transform.EventType is { IsInterface: false, IsAbstract: false })
            {
                types.Add(transform.EventType);
            }

            types.AddRange(known.Select(x => x.EventType)
                .Where(x => x != transform.EventType && transform.EventType.IsAssignableFrom(x)));
        }

        var names = types.Count == 0
            ? new List<string>()
            : EventTypeAllowList.For(events, types.Distinct().ToList()).AliasesIncludingUpcastSources.ToList();

        foreach (var name in storedNames.Where(x => !names.Contains(x, StringComparer.Ordinal)))
        {
            names.Add(name);
        }

        return names;
    }

    /// <summary>
    ///     Each tenant in turn, from its cursor up to and including its ceiling, one page at a time. A dry run writes
    ///     nothing, not even the cursor.
    /// </summary>
    public async Task<IReadOnlyList<EventRewriteProgress>> ExecuteRunAsync(FisherDatabase database,
        EventRewriteRun run, CancellationToken token)
    {
        await database.EnsureEventStorageAsync(token).ConfigureAwait(false);

        var totals = new List<EventRewriteProgress>(run.Cursors.Count);
        foreach (var cursor in run.Cursors)
        {
            // A tenant's own rows when events are conjoined. Otherwise the tenant's database is the selection.
            var tenantFilter = _events.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined ? cursor.TenantId : null;

            var total = new EventRewriteProgress(cursor.TenantId, cursor.ResumeAfter, 0, 0, 0, 0, 0, 0,
                cursor.IsComplete);
            var position = cursor.ResumeAfter;

            while (!total.Completed)
            {
                token.ThrowIfCancellationRequested();

                var from = position;
                var page = await _options.ResiliencePipeline.ExecuteAsync(
                        async ct => await rewritePageAsync(database, run, cursor, tenantFilter, from, ct)
                            .ConfigureAwait(false), token)
                    .ConfigureAwait(false);

                position = page.Sequence;

                total = total with
                {
                    Sequence = page.Sequence,
                    Visited = total.Visited + page.Visited,
                    Rewritten = total.Rewritten + page.Rewritten,
                    BytesBefore = total.BytesBefore + page.BytesBefore,
                    BytesAfter = total.BytesAfter + page.BytesAfter,
                    UnknownTypes = total.UnknownTypes + page.UnknownTypes,
                    Completed = page.Completed
                };

                if (run.OnPage != null)
                {
                    await run.OnPage(page, token).ConfigureAwait(false);
                }

                if (!page.Completed && _definition.PauseBetweenBatches > TimeSpan.Zero)
                {
                    await Task.Delay(_definition.PauseBetweenBatches, token).ConfigureAwait(false);
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
    private async Task<EventRewriteProgress> rewritePageAsync(FisherDatabase database, EventRewriteRun run,
        EventRangeCursor cursor, string? tenantFilter, long position, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token).ConfigureAwait(false);

        var waitedFrom = _options.OpenTelemetry.StartWriteLockWait();
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token).ConfigureAwait(false);
        _options.OpenTelemetry.RecordWriteLockWait(waitedFrom, Services.OpenTelemetryOptions.RewriteHolder);

        var rows = await readPageAsync(connection, transaction, tenantFilter, position, cursor.StopAfter, token)
            .ConfigureAwait(false);

        long rewritten = 0, unknown = 0, bytesBefore = 0, bytesAfter = 0;
        foreach (var row in rows)
        {
            if (row.Event is null)
            {
                unknown++;
                continue;
            }

            var stored = written(row.Event.Data);
            if (stored.Type == row.Type && stored.DotNetType == row.DotNetType && stored.Data == row.Data
                && sameBytes(stored.Binary, row.Binary))
            {
                continue;
            }

            rewritten++;
            bytesBefore += Encoding.UTF8.GetByteCount(row.Data) + (row.Binary?.Length ?? 0);
            bytesAfter += Encoding.UTF8.GetByteCount(stored.Data) + (stored.Binary?.Length ?? 0);

            if (!_definition.DryRun)
            {
                await updateAsync(connection, transaction, row.Sequence, stored, token).ConfigureAwait(false);
            }
        }

        var completed = rows.Count < _definition.BatchSize;

        // The ceiling on a tenant's last page, so the stored cursor says the tenant is done
        var sequence = completed ? cursor.StopAfter : rows[^1].Sequence;

        if (!_definition.DryRun)
        {
            await using var mark = connection.CreateCommand();
            mark.Transaction = transaction;
            database.ConfigureSetEventRangeMark(mark, run.CursorKey, cursor.TenantId, sequence);
            await mark.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        await transaction.CommitAsync(token).ConfigureAwait(false);

        return new EventRewriteProgress(cursor.TenantId, sequence, rows.Count, rewritten, 0, bytesBefore,
            bytesAfter, unknown, completed);
    }

    private sealed record Row(long Sequence, string Type, string? DotNetType, string Data, byte[]? Binary, IEvent? Event);

    private sealed record Stored(string Type, string DotNetType, string Data, byte[]? Binary);

    private async Task<List<Row>> readPageAsync(SqliteConnection connection, SqliteTransaction transaction,
        string? tenantId, long after, long stopAfter, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = _options.CommandTimeout;

        var sql = new StringBuilder("select ")
            .Append(FisherEventsRowReader.ComposeSelectColumns(_options.Events))
            .Append(" from ").Append(_events.EventsTableName)
            .Append(" where seq_id > @after and seq_id <= @stop_after");
        command.Parameters.AddWithValue("@after", after);
        command.Parameters.AddWithValue("@stop_after", stopAfter);

        if (_typeNames != null)
        {
            sql.Append(" and type in (select value from json_each(@types))");
            command.Parameters.AddWithValue("@types", SqliteJsonArrays.Write(_typeNames));
        }

        if (tenantId != null)
        {
            sql.Append(" and tenant_id = @tenant_id");
            command.Parameters.AddWithValue("@tenant_id", tenantId);
        }

        if (_definition.OnlyEventsStoredAsJson)
        {
            sql.Append(" and data_binary is null");
        }

        sql.Append(" order by seq_id limit @batch_size");
        command.Parameters.AddWithValue("@batch_size", _definition.BatchSize);
        command.CommandText = sql.ToString();

        var context = new EventHydrationContext(_events, _options.Serializer, string.Empty,
            JasperFx.StorageConstants.DefaultTenantId);
        var slots = MetadataSlots.For(_options.Events);
        var isGuid = _events.StreamIdentity == JasperFx.Events.StreamIdentity.AsGuid;

        var rows = new List<Row>(_definition.BatchSize);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var binaryOrdinal = reader.FieldCount - 1;

        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            // Read before the event, so the cursor moves past a row whose type this application no longer knows.
            // That row is only reachable with AllEventTypes, and it stays as it is: an event the application
            // cannot read cannot be written back the way it would be stored today.
            var @event = await FisherEventsRowReader.ReadEventAcrossStreams(reader, context, slots, isGuid, token)
                .ConfigureAwait(false);

            rows.Add(new Row(
                reader.GetInt64(0),
                reader.GetString(5),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(4),
                reader.IsDBNull(binaryOrdinal) ? null : (byte[])reader.GetValue(binaryOrdinal),
                @event));
        }

        return rows;
    }

    /// <summary>The event after the transforms, as an append would store it today.</summary>
    private Stored written(object data)
    {
        foreach (var transform in _transforms)
        {
            if (transform.AppliesTo(data))
            {
                data = transform.Apply(data);
            }
        }

        var type = data.GetType();
        var mapping = _events.EventMappingFor(type);
        var binary = _events.BinaryBodyFor(type, data);

        return new Stored(mapping.EventTypeName, mapping.DotNetTypeName,
            binary is null ? _options.Serializer.ToJson(data) : EventsTable.JsonPlaceholder, binary);
    }

    private static bool sameBytes(byte[]? left, byte[]? right)
        => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    private async Task updateAsync(SqliteConnection connection, SqliteTransaction transaction, long sequence,
        Stored stored, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = _options.CommandTimeout;
        command.CommandText = $"update {_events.EventsTableName} set type = @type, dotnet_type = @dotnet_type, "
                              + "data = @data, data_binary = @data_binary where seq_id = @seq_id";
        command.Parameters.AddWithValue("@type", stored.Type);
        command.Parameters.AddWithValue("@dotnet_type", stored.DotNetType);
        command.Parameters.AddWithValue("@data", stored.Data);
        command.Parameters.Add("@data_binary", SqliteType.Blob).Value = (object?)stored.Binary ?? DBNull.Value;
        command.Parameters.AddWithValue("@seq_id", sequence);

        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
