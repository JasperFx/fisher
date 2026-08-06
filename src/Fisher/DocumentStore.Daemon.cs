using Fisher.Events.Daemon;
using Fisher.Storage;
using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fisher;

/// <summary>
///     The storage seam the async projection daemon runs on — <see cref="IEventStore{TOperations,TQuerySession}" />
///     closed over Fisher's session pair.
/// </summary>
/// <remarks>
///     <para>
///         The daemon itself is JasperFx's: the coordinator, the subscription agents, the shard tracker,
///         the throttled and resilient loaders. What a store supplies is this interface plus the three
///         types in <c>Events/Daemon</c> — the high-water detector, the event loader and the projection
///         batch. Everything here is either a projection of configuration the store already holds or a
///         short piece of SQL against <c>fi_event_progression</c>.
///     </para>
///     <para>
///         Implemented explicitly, as the rest of the <see cref="IEventStore" /> surface is, so none of
///         it lands on the store's own public API. Application code never calls these; the daemon does.
///     </para>
///     <para>
///         Fisher has exactly one database per store, so every member that takes an
///         <see cref="IEventDatabase" /> ignores it and works against <see cref="Database" />. Marten and
///         Polecat resolve a connection string off that parameter because they can be
///         database-per-tenant; a SQLite store is one file.
///     </para>
/// </remarks>
public partial class DocumentStore : IEventStore<IDocumentSession, IQuerySession>
{
    // ---- configuration the daemon reads ----

    IEventRegistry IEventStore<IDocumentSession, IQuerySession>.Registry => EventGraph;

    string IEventStore<IDocumentSession, IQuerySession>.DefaultDatabaseName => Database.Identifier;

    ErrorHandlingOptions IEventStore<IDocumentSession, IQuerySession>.ContinuousErrors
        => Options.Projections.Errors;

    ErrorHandlingOptions IEventStore<IDocumentSession, IQuerySession>.RebuildErrors
        => Options.Projections.RebuildErrors;

    ErrorHandlingOptions IEventStore<IDocumentSession, IQuerySession>.ErrorHandlingOptions(ShardExecutionMode mode)
        => mode == ShardExecutionMode.Rebuild ? Options.Projections.RebuildErrors : Options.Projections.Errors;

    IReadOnlyList<AsyncShard<IDocumentSession, IQuerySession>>
        IEventStore<IDocumentSession, IQuerySession>.AllShards() => Options.Projections.AllShards();

    TimeProvider IEventStore<IDocumentSession, IQuerySession>.TimeProvider => EventGraph.TimeProvider;

    AutoCreate IEventStore<IDocumentSession, IQuerySession>.AutoCreateSchemaObjects
        => Options.AutoCreateSchemaObjects;

    /// <summary>
    ///     The identity type the daemon addresses a projected document by.
    /// </summary>
    /// <remarks>
    ///     The stream identity primitive rather than the aggregate's own id type — this answers "what
    ///     does a shard slice by", not "what does the generated dispatcher key on". See
    ///     <see cref="Storage.AggregateIdentity" /> for the other question, which has a different answer
    ///     for a strong-typed id.
    /// </remarks>
    Type IEventStore<IDocumentSession, IQuerySession>.IdentityTypeForProjectedType(Type aggregateType)
        => EventGraph.StreamIdentity == StreamIdentity.AsGuid ? typeof(Guid) : typeof(string);

    /// <summary>
    ///     Expose the one database this store owns, so store-agnostic tooling can reach the
    ///     <see cref="IEventDatabase" /> reads without knowing Fisher's types.
    /// </summary>
    ValueTask<IReadOnlyList<IEventDatabase>> IEventStore.AllDatabases()
        => ValueTask.FromResult<IReadOnlyList<IEventDatabase>>([Database]);

    // ---- sessions and loaders ----

    IDocumentSession IEventStore<IDocumentSession, IQuerySession>.OpenSession(IEventDatabase database)
        => LightweightSession();

    IDocumentSession IEventStore<IDocumentSession, IQuerySession>.OpenSession(IEventDatabase database,
        string tenantId) => LightweightSession(tenantId);

    /// <summary>
    ///     Build the loader one shard pages its events through.
    /// </summary>
    /// <remarks>
    ///     The bare <see cref="FisherEventLoader" /> is wrapped in JasperFx's
    ///     <see cref="ResilientEventLoader" />, which supplies both the retry around
    ///     <see cref="StoreOptions.ResiliencePipeline" /> and the load metrics the daemon reports. That
    ///     is why the loader itself does neither.
    /// </remarks>
    IEventLoader IEventStore<IDocumentSession, IQuerySession>.BuildEventLoader(IEventDatabase database,
        ILogger loggerFactory, EventFilterable filtering, AsyncOptions shardOptions)
        => new ResilientEventLoader(Options.ResiliencePipeline, new FisherEventLoader(Database, Options, filtering),
            Database);

    /// <summary>
    ///     Open one transaction's worth of projection work.
    /// </summary>
    /// <remarks>
    ///     The range's progress is recorded into the batch immediately, so it commits in the same
    ///     transaction as whatever the projection goes on to write. See
    ///     <see cref="FisherProjectionBatch" /> for why splitting those is not an option.
    /// </remarks>
    async ValueTask<IProjectionBatch<IDocumentSession, IQuerySession>>
        IEventStore<IDocumentSession, IQuerySession>.StartProjectionBatchAsync(EventRange range,
            IEventDatabase database, ShardExecutionMode mode, AsyncOptions projectionOptions,
            CancellationToken token)
    {
        var batch = new FisherProjectionBatch(this, EventGraph);
        await batch.RecordProgress(range).ConfigureAwait(false);

        return batch;
    }

    // ---- progression bookkeeping ----

    /// <summary>
    ///     Reset a subscription's progress to a floor, or clear it outright.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A null or zero floor deletes every progression row the subscription owns rather than
    ///         writing a zero, because a missing row and a row at zero mean the same thing to the daemon,
    ///         and the deletion also sweeps up shard rows the current configuration no longer names.
    ///     </para>
    ///     <para>
    ///         A subscription owns one row per shard (<c>Name:ShardKey</c>), so the delete matches on a
    ///         <c>like</c> prefix — the shape Marten and Polecat both use. Note that <c>_</c> is a
    ///         single-character wildcard to SQL's <c>LIKE</c>: a subscription named <c>tally</c> and one
    ///         named <c>tallyX</c> are distinct, but a projection name containing <c>_</c> would match
    ///         loosely. That is the same trap the document cleaner sidesteps by filtering in C#; here the
    ///         set being matched is one small table of the store's own shard names rather than every
    ///         table in the file.
    ///     </para>
    /// </remarks>
    Task IEventStore<IDocumentSession, IQuerySession>.RewindSubscriptionProgressAsync(IEventDatabase database,
        string subscriptionName, CancellationToken token, long? sequenceFloor)
        => sequenceFloor is null or 0
            ? DeleteProgressionRowsAsync(subscriptionName, token)
            : WriteProgressionRowAsync(subscriptionName, sequenceFloor.Value, token);

    Task IEventStore<IDocumentSession, IQuerySession>.RewindAgentProgressAsync(IEventDatabase database,
        string shardName, CancellationToken token, long sequenceFloor)
        => WriteProgressionRowAsync(shardName, sequenceFloor, token);

    Task IEventStore<IDocumentSession, IQuerySession>.DeleteProjectionProgressAsync(IEventDatabase database,
        string subscriptionName, CancellationToken token)
        => DeleteProgressionRowsAsync(subscriptionName, token);

    /// <summary>
    ///     Drop a projection's progress <em>and</em> the documents it published, which is what a rebuild
    ///     starts from.
    /// </summary>
    /// <remarks>
    ///     Both halves run in one transaction. A teardown that deleted the progression and then failed
    ///     before clearing the documents would leave a projection that replays from zero on top of rows
    ///     it already wrote — the exact double-application a rebuild exists to avoid.
    /// </remarks>
    async Task IEventStore<IDocumentSession, IQuerySession>.TeardownExistingProjectionStateAsync(
        IEventDatabase database, string subscriptionName, CancellationToken token)
    {
        var tables = PublishedTableNamesFor(subscriptionName);

        await Options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection
                .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"delete from {EventGraph.ProgressionTableName} where name like @name";
                command.Parameters.AddWithValue("@name", subscriptionName + "%");
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // A projection's document table is created on first write, so on a first-ever rebuild it may
            // not exist yet. The existence test has to happen here rather than as a predicate on the
            // delete: SQLite resolves table names when it prepares the statement, so a `where exists
            // (select ... from sqlite_master ...)` guard would still fail before it ever ran.
            var existing = await ReadExistingTableNamesAsync(connection, transaction, ct).ConfigureAwait(false);

            foreach (var table in tables.Where(existing.Contains))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"delete from {Weasel.Sqlite.SchemaUtils.QuoteName(table)}";
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            Diagnostics.DaemonTrace.Record("teardown.commit", subscriptionName, tables.Count);

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }, token).ConfigureAwait(false);

        Diagnostics.DaemonTrace.Record("teardown.done", subscriptionName);
    }

    /// <summary>
    ///     The unquoted tables a projection publishes into.
    /// </summary>
    /// <remarks>
    ///     Normally that means one document table per published type the schema has mapped. A
    ///     flat-table projection publishes no types at all — its rows are not documents — so it names
    ///     its table directly through <c>IPublishesTables</c>; without that a rebuild would replay onto
    ///     the rows the previous run left behind.
    /// </remarks>
    private IReadOnlyList<string> PublishedTableNamesFor(string subscriptionName)
    {
        if (!Options.Projections.TryFindProjection(subscriptionName, out var source))
        {
            return [];
        }

        var tables = source.PublishedTypes()
            .Where(Options.Schema.HasMappingFor)
            .Select(x => Options.Schema.MappingFor(x).TableName.Name)
            .ToList();

        if (source is Projections.Flattened.IPublishesTables publisher)
        {
            tables.AddRange(publisher.PublishedTableNames());
        }

        return tables;
    }

    private static async Task<HashSet<string>> ReadExistingTableNamesAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select name from sqlite_master where type = 'table'";

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private async Task DeleteProgressionRowsAsync(string subscriptionName, CancellationToken token)
    {
        await Options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"delete from {EventGraph.ProgressionTableName} where name like @name";
            command.Parameters.AddWithValue("@name", subscriptionName + "%");
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }

    private async Task WriteProgressionRowAsync(string shardIdentity, long sequence, CancellationToken token)
    {
        await Options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                                   insert into {EventGraph.ProgressionTableName} (name, last_seq_id, last_updated)
                                   values (@name, @seq, {SqliteTimestamp.NowExpression})
                                   on conflict (name) do update
                                     set last_seq_id = excluded.last_seq_id,
                                         last_updated = excluded.last_updated;
                                   """;
            command.Parameters.AddWithValue("@name", shardIdentity);
            command.Parameters.AddWithValue("@seq", sequence);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }

    // ---- building the daemon ----

    /// <summary>
    ///     Build a projection daemon over this store's single database.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <paramref name="tenantIdOrDatabaseIdentifier" /> is accepted and ignored: Fisher is one
    ///         file, one database, and has no database-per-tenant tenancy to resolve against.
    ///     </para>
    ///     <para>
    ///         <strong>WAL is checked here rather than assumed.</strong> The daemon reads while
    ///         application sessions write, which under SQLite's default rollback journal serializes the
    ///         two — the daemon would block behind every writer and every writer behind the daemon. WAL
    ///         is on by default through <see cref="Weasel.Sqlite.SqlitePragmaSettings.Default" />, but a
    ///         consumer replacing <see cref="StoreOptions.PragmaSettings" /> can turn it off, and the
    ///         resulting stall looks like a slow projection rather than a misconfiguration. Warning at
    ///         start is where a human is actually looking.
    ///     </para>
    /// </remarks>
    public async ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(
        string? tenantIdOrDatabaseIdentifier = null, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        WarnIfJournalModeIsNotWal(logger);

        await Database.EnsureStorageExistsAsync(typeof(IEvent), CancellationToken.None).ConfigureAwait(false);

        return new FisherProjectionDaemon(this, Database, logger,
            new FisherHighWaterDetector(Database, EventGraph));
    }

    ValueTask<IProjectionDaemon> IEventStore.BuildProjectionDaemonAsync(DatabaseId id)
        => BuildProjectionDaemonAsync();

    private void WarnIfJournalModeIsNotWal(ILogger logger)
    {
        var journalMode = Options.PragmaSettings.JournalMode;

        if (journalMode != Weasel.Sqlite.JournalMode.WAL)
        {
            logger.LogWarning(
                "The Fisher async projection daemon is starting against a database whose journal mode is "
                + "{JournalMode} rather than WAL. Without WAL, SQLite blocks readers while a writer holds the "
                + "database, so the daemon and every application session will serialize against each other. "
                + "Set StoreOptions.PragmaSettings.JournalMode to Wal.", journalMode);
        }
    }
}
