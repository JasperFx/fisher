using JasperFx.Events.Protected;
using Weasel.Core.Sequences;

namespace Fisher;

/// <summary>
///     The store's escape hatch: cleaning, resetting, and the Hi-Lo knobs — the things an application
///     reaches for outside the session API. Mirrors Marten's and Polecat's <c>AdvancedOperations</c>.
/// </summary>
public class AdvancedOperations
{
    private readonly DocumentStore _store;
    private IDocumentCleaner? _cleaner;

    internal AdvancedOperations(DocumentStore store)
    {
        _store = store;
    }

    /// <summary>
    ///     The Hi-Lo settings applied to any document type with a numeric identity and no override of
    ///     its own.
    /// </summary>
    public HiloSettings HiloSequenceDefaults => _store.Options.HiloSequenceDefaults;

    /// <inheritdoc cref="IDocumentCleaner" />
    public IDocumentCleaner Clean => _cleaner ??= new Internal.FisherDocumentCleaner(_store);

    /// <summary>
    ///     Delete every document and every event belonging to this store, keeping the schema.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A running async daemon is paused around the wipe and resumed afterwards</b>
    ///         (fisher#138), and without that the wipe strands it. The delete takes
    ///         <c>fi_event_progression</c> out from under agents that hold their positions in memory,
    ///         so they carry on from where they were, record no progress against an event store that
    ///         now starts at zero, and every later <c>WaitForNonStaleData</c> times out with a message
    ///         about shards that have recorded nothing. Silent until something waits.
    ///     </para>
    ///     <para>
    ///         <b>This is a divergence from Marten, which leaves its daemon alone here.</b> The reason
    ///         to take it is that the alternative was unreachable rather than merely manual: until
    ///         fisher#138 there was no way to get at the running daemon from application code at all,
    ///         so a spec fixture resetting between scenarios — the caller this method overwhelmingly
    ///         has — could not have paused it by hand.
    ///     </para>
    ///     <para>
    ///         Only a daemon <em>this process is hosting</em> is paused, since that is the only one the
    ///         store knows about. <c>DaemonMode.ExternallyManaged</c>, a store built by
    ///         <see cref="DocumentStore.For(System.Action{StoreOptions})" />, or a daemon in another
    ///         process are all unaffected and keep the hazard above — which is the honest outcome, as
    ///         nothing here can reach them.
    ///     </para>
    /// </remarks>
    public async Task ResetAllDataAsync(CancellationToken token = default)
    {
        var daemons = _store.RunningDaemons;

        if (daemons is not null)
        {
            await daemons.PauseAsync().ConfigureAwait(false);
        }

        try
        {
            await Clean.DeleteAllDocumentsAsync(token).ConfigureAwait(false);
            await Clean.DeleteAllEventDataAsync(token).ConfigureAwait(false);
        }
        finally
        {
            // In a finally, because a half-done wipe with the daemon left paused is the worse of the
            // two failures: the caller sees the exception either way, and a paused daemon that never
            // resumes turns one failed reset into every subsequent projection silently not running.
            if (daemons is not null)
            {
                await daemons.ResumeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     Rewrite protected information out of events that are already stored, applying the masking
    ///     rules registered on the store's event options.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The whole batch commits in one transaction, so an erasure is either done or not done.
    ///         An event is rewritten only when a rule actually matches it.
    ///     </para>
    ///     <para>
    ///         <b>This does not reach anything derived from those events.</b> The daemon's high-water
    ///         mark is a sequence and masking does not move it, so a projection that has already folded
    ///         the unmasked body keeps what it derived — any snapshot, document or flat table holding
    ///         the protected information still holds it until that projection is rebuilt. Marten
    ///         behaves the same way. Masking is a data-at-rest operation.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     The batch names no stream and no event filter. Masking every event in the store is not
    ///     something this API can be asked for by accident.
    /// </exception>
    public Task ApplyEventDataMaskingAsync(Action<IEventDataMasking> configure,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var batch = new Events.Protected.EventDataMasking(_store);
        configure(batch);

        return batch.ApplyAsync(token);
    }

    /// <summary>
    ///     Advance a document type's Hi-Lo sequence so that every subsequently assigned id is greater
    ///     than <paramref name="floor" />.
    /// </summary>
    /// <remarks>
    ///     The floor rounds up to a whole allocation, so the next id is the start of the first page
    ///     past it rather than <paramref name="floor" /> + 1. That matches Marten and Polecat, and is
    ///     the price of the client-side batching Hi-Lo exists for.
    /// </remarks>
    public Task ResetHiloSequenceFloorAsync<T>(long floor) where T : notnull
        => _store.Database.SequenceFor(typeof(T)).SetFloor(floor);

    /// <summary>
    ///     Write many documents in as few transactions as possible.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>There is no <c>SqlBulkCopy</c> to reach for, and none is needed.</b> On SQLite the
    ///         cost of an insert is dominated by the transaction rather than by the statement, so a
    ///         prepared statement re-executed with rebound parameters inside one transaction is already
    ///         the fast path — the same order as a bulk-copy protocol, with none of the protocol.
    ///     </para>
    ///     <para>
    ///         <paramref name="batchSize" /> is a ceiling on how long the write lock is held, not a
    ///         throughput knob. SQLite permits one writer per file, so a single transaction over a very
    ///         large set blocks every other writer for its whole duration; committing periodically
    ///         gives them a chance. The trade is that a failure part way leaves earlier batches
    ///         committed — bulk insert is not atomic across batches and does not pretend to be.
    ///     </para>
    ///     <para>
    ///         The statements are the ones <c>SqliteDocumentStorageDescriptorBuilder</c> already
    ///         builds, reached through the session, rather than bulk-specific SQL. That is deliberate:
    ///         a second set of write SQL is exactly where the positional <c>?</c> contract documented
    ///         in CLAUDE.md would drift apart unnoticed.
    ///     </para>
    /// </remarks>
    public async Task BulkInsertAsync<T>(IReadOnlyCollection<T> documents,
        BulkInsertMode mode = BulkInsertMode.InsertsOnly, int batchSize = 1000,
        string? tenantId = null, CancellationToken token = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        if (documents.Count == 0)
        {
            return;
        }

        var storage = mode == BulkInsertMode.IgnoreDuplicates
            ? _store.Database.Providers.StorageFor<T>().Lightweight
            : null;

        foreach (var batch in documents.Chunk(batchSize))
        {
            await using var session = _store.LightweightSession(tenantId);

            var alreadyStored = storage is null
                ? null
                : await AlreadyStoredAsync(storage, batch, session.TenantId, token).ConfigureAwait(false);

            foreach (var document in batch)
            {
                if (alreadyStored is not null)
                {
                    if (!alreadyStored.Contains(Compare(storage!.RawIdentityValue(storage.IdentityFor(document)))))
                    {
                        session.Insert(document);
                    }
                }
                else if (mode == BulkInsertMode.InsertsOnly)
                {
                    session.Insert(document);
                }
                else
                {
                    session.Store(document);
                }
            }

            await session.SaveChangesAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Which of a batch's ids already occupy a row — the read behind
    ///     <see cref="BulkInsertMode.IgnoreDuplicates" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Hand-built rather than routed through <c>LoadManyAsync</c> or <c>Query&lt;T&gt;()</c>,
    ///         which is the opposite of what this codebase usually does and is the point: those two
    ///         answer "which of these can I read", and both apply the soft-delete and hierarchy filters
    ///         to do it. The question here is "which of these would collide", and a soft-deleted row or
    ///         a sub-class row still holds the primary key. It also reads one column instead of
    ///         materializing every document it finds.
    ///     </para>
    ///     <para>
    ///         Ids are compared as their <em>raw</em> SQL values on both sides —
    ///         <c>RawIdentityValue</c> going in and the reader's own value coming back — so a Guid is
    ///         the lowercase canonical text the row holds rather than the uppercase form a bare
    ///         <see cref="Guid" /> binds as. That is the recurring trap, and here it would present as
    ///         every document looking new and the batch failing on the constraint it exists to avoid.
    ///     </para>
    ///     <para>
    ///         A document whose identity is still unassigned needs no special case, which is worth
    ///         saying because it looks like it should. A Guid or numeric identity is assigned before the
    ///         write, so no row can hold <c>Guid.Empty</c> or <c>0</c> for the probe to match; a string
    ///         identity is externally assigned, so an empty one is a real key and finding it is the
    ///         right answer. Only a null identity is dropped, and only because
    ///         <c>RawIdentityValue</c> has nothing to convert.
    ///     </para>
    ///     <para>
    ///         Both sides are compared as invariant strings, which is not decoration:
    ///         Microsoft.Data.Sqlite hands an INTEGER column back as <see cref="long" /> while an
    ///         <see cref="int" /> identity's raw value is an <see cref="int" />, and boxed to
    ///         <see cref="object" /> those two never compare equal — so an int-keyed type would find
    ///         nothing and fail on the constraint this mode exists to avoid. Within one id type the
    ///         rendering is injective, so nothing else can collide.
    ///     </para>
    /// </remarks>
    private async Task<HashSet<string>> AlreadyStoredAsync<T>(Weasel.Storage.IDocumentStorage<T> storage,
        IReadOnlyList<T> batch, string tenantId, CancellationToken token) where T : notnull
    {
        var probe = batch
            .Select(x => storage.IdentityFor(x))
            .Where(x => x is not null)
            .Select(storage.RawIdentityValue)
            .Distinct()
            .ToArray();

        var found = new HashSet<string>();

        if (probe.Length == 0)
        {
            return found;
        }

        var conjoined = storage.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined;

        return await _store.Options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await _store.Database.OpenConnectionAsync(ct).ConfigureAwait(false);

            var builder = new Weasel.Sqlite.CommandBuilder();
            builder.Append($"select id from {storage.TableName.QualifiedName} ");
            builder.Append("where id in (select value from json_each(");
            builder.AppendParameter(System.Text.Json.JsonSerializer.Serialize(probe));
            builder.Append("))");

            if (conjoined)
            {
                builder.Append(" and tenant_id = ");
                builder.AppendParameter(tenantId);
            }

            await using var command = builder.Compile();
            command.Connection = connection;

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                found.Add(Compare(reader.GetValue(0)));
            }

            return found;
        }, token).ConfigureAwait(false);
    }

    /// <inheritdoc cref="AlreadyStoredAsync{T}" />
    private static string Compare(object? rawId)
        => Convert.ToString(rawId, System.Globalization.CultureInfo.InvariantCulture) ?? "";

    /// <summary>
    ///     How much is in the event store — event count, stream count, and the current sequence.
    /// </summary>
    /// <remarks>
    ///     Three scalars on one connection inside the resilience pipeline, so a <c>SQLITE_BUSY</c>
    ///     retries the set rather than leaving the three readings taken at different moments.
    /// </remarks>
    public async Task<Events.EventStoreStatistics> FetchEventStoreStatisticsAsync(
        CancellationToken token = default)
    {
        var events = _store.Options.EventGraph;

        return await _store.Options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await _store.Database.OpenConnectionAsync(ct).ConfigureAwait(false);

            return new Events.EventStoreStatistics
            {
                EventCount = await ScalarAsync(connection,
                    $"select count(*) from \"{events.EventsTableName}\"", ct).ConfigureAwait(false),
                StreamCount = await ScalarAsync(connection,
                    $"select count(*) from \"{events.StreamsTableName}\"", ct).ConfigureAwait(false),

                // coalesce, not just a null check: sqlite_sequence has no row for a table nothing has
                // been inserted into, and the table itself does not exist until the first AUTOINCREMENT
                // insert anywhere in the database.
                EventSequenceNumber = await ScalarAsync(connection,
                    "select coalesce((select seq from sqlite_sequence where name = "
                    + $"'{events.EventsTableName}'), 0)", ct).ConfigureAwait(false)
            };
        }, token).ConfigureAwait(false);
    }

    private static async Task<long> ScalarAsync(Microsoft.Data.Sqlite.SqliteConnection connection,
        string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
    }

    /// <summary>
    ///     The full creation DDL for this store, without applying any of it.
    /// </summary>
    /// <remarks>
    ///     For reviewing a migration, and for the <c>AutoCreate.None</c> deployment story where
    ///     somebody else runs the DDL. Covers every feature the store has registered — event store,
    ///     document tables, Hi-Lo and flat tables — because it asks the same feature set
    ///     <c>ApplyAllConfiguredChangesToDatabaseAsync</c> applies. A document type that has never been
    ///     registered is absent, exactly as it is from a migration.
    /// </remarks>
    public string ToDatabaseScript() => _store.Database.ToDatabaseScript();

    /// <inheritdoc cref="ToDatabaseScript" />
    public Task WriteCreationScriptToFileAsync(string path, CancellationToken token = default)
        => _store.Database.WriteCreationScriptToFileAsync(path, token);

    /// <summary>
    ///     Run a projection scenario — a given/when/then harness for asserting projected state after a
    ///     set of events.
    /// </summary>
    /// <remarks>
    ///     The harness itself is JasperFx's; Fisher supplies only the store seam, the same way
    ///     <c>FisherProjectionDaemon</c> does for the daemon. <b>It deletes existing data first</b> —
    ///     the event store, and the document types the registered projections own, but nothing else,
    ///     so a scenario can seed unrelated documents beforehand and keep them.
    /// </remarks>
    public Task EventProjectionScenarioAsync(Action<Events.TestSupport.ProjectionScenario> configure,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var scenario = new Events.TestSupport.ProjectionScenario(_store);
        configure(scenario);

        return scenario.ExecuteAsync(token);
    }
}
