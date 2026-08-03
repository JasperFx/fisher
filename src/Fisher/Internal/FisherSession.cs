using System.Data.Common;
using Fisher.Events;
using Fisher.Serialization;
using Fisher.Storage;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using Microsoft.Data.Sqlite;
using Weasel.Sqlite;
using Weasel.Storage;

namespace Fisher.Internal;

/// <summary>
///     Fisher's session: a connection, a unit of work, and the dialect-neutral
///     <see cref="IStorageSession" /> seam the shared closed-shape storage runtime executes against.
/// </summary>
/// <remarks>
///     <para>
///         Queued operations are flushed by <see cref="SaveChangesAsync" /> inside a single
///         transaction. Unlike Polecat, which runs its unit of work with parallelism and therefore
///         aggregates failures, Fisher executes strictly sequentially on one connection — SQLite
///         permits only one writer at a time, so concurrency here would produce SQLITE_BUSY
///         contention against itself rather than throughput.
///     </para>
/// </remarks>
internal class FisherSession : IDocumentSession, IStorageSession, IAsyncDisposable
{
    private readonly List<Weasel.Storage.IStorageOperation> _operations = new();
    private List<IChangeTracker>? _changeTrackers;
    private SqliteConnection? _connection;
    private Dictionary<Type, object>? _itemMap;
    private IStorageSerializer? _storageSerializer;
    private int _tempTableNumber;
    private FisherVersionTracker? _versionTracker;

    public FisherSession(StoreOptions options, FisherDatabase database, string tenantId)
    {
        Options = options;
        FisherDatabase = database;
        TenantId = tenantId;
        Events = new EventOperations(this);
    }

    internal StoreOptions Options { get; }

    internal FisherDatabase FisherDatabase { get; }

    internal EventGraph EventGraph => Options.EventGraph;

    internal Serialization.ISerializer FisherSerializer => Options.Serializer;

    /// <summary>
    ///     Every operation queued for the next <see cref="SaveChangesAsync" />.
    /// </summary>
    internal IReadOnlyList<Weasel.Storage.IStorageOperation> PendingOperations => _operations;

    /// <summary>
    ///     The event store operations for this session.
    /// </summary>
    public EventOperations Events { get; }

    internal void QueueOperation(Weasel.Storage.IStorageOperation operation) => _operations.Add(operation);

    /// <summary>
    ///     Open (or return) this session's connection. One connection per session for its whole
    ///     lifetime, so that reads inside a unit of work see the session's own uncommitted writes.
    /// </summary>
    internal async ValueTask<SqliteConnection> ConnectionAsync(CancellationToken token = default)
        => _connection ??= await FisherDatabase.OpenConnectionAsync(token).ConfigureAwait(false);

    /// <summary>
    ///     Flush every queued operation inside one transaction.
    /// </summary>
    public async Task SaveChangesAsync(CancellationToken token = default)
    {
        var streams = Events.PendingStreams.ToArray();

        if (_operations.Count == 0 && streams.Length == 0)
        {
            return;
        }

        var queued = _operations.ToArray();
        _operations.Clear();
        Events.ClearPendingStreams();

        var connection = await ConnectionAsync(token).ConfigureAwait(false);

        await Options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            // BEGIN IMMEDIATE, not the default deferred transaction. The append planner reads each
            // stream's current version and then writes version+1; under a deferred transaction
            // SQLite would not take the write lock until that write, leaving a window where two
            // sessions both read version N. IMMEDIATE takes the lock up front, which is Fisher's
            // stand-in for Marten's advisory lock and Polecat's UPDLOCK/HOLDLOCK read.
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);

            var operations = new List<Weasel.Storage.IStorageOperation>(queued);

            if (streams.Length > 0)
            {
                var planned = await new AppendPlanner(this)
                    .PlanAsync(streams, connection, transaction, ct).ConfigureAwait(false);

                operations.AddRange(planned);
            }

            await ExecuteBatchAsync(connection, transaction, operations, ct).ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);

            NotifyAppendObserver(streams);
        }, token).ConfigureAwait(false);
    }

    /// <summary>
    ///     Best-effort notification of the configured append observer with the events committed in
    ///     this unit of work.
    /// </summary>
    private void NotifyAppendObserver(IReadOnlyList<StreamAction> streams)
    {
        var observer = Options.Events.AppendObserver;

        if (observer is null || streams.Count == 0)
        {
            return;
        }

        var appended = streams.SelectMany(x => x.Events).ToList();

        if (appended.Count > 0)
        {
            observer(appended);
        }
    }

    /// <summary>
    ///     Execute the queued operations and hand each its result set back for postprocessing.
    /// </summary>
    /// <remarks>
    ///     Each operation is compiled and executed as its own command rather than being concatenated
    ///     into one. Operations bind parameters by position through the shared command builder, and
    ///     Fisher's append operation ends in a SELECT — batching them into a single SQLite command
    ///     would interleave their parameter numbering and force every consumer to walk result sets
    ///     with NextResult in lockstep. Sharing the transaction preserves the atomicity that matters
    ///     without that fragility.
    /// </remarks>
    private async Task ExecuteBatchAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<Weasel.Storage.IStorageOperation> operations, CancellationToken token)
    {
        var exceptions = new List<Exception>();

        foreach (var operation in operations)
        {
            var builder = new FisherCommandBuilder();
            operation.ConfigureCommand(builder, this);

            var command = builder.Compile();
            command.Connection = connection;
            command.Transaction = transaction;
            command.CommandTimeout = Options.CommandTimeout;

            try
            {
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                await operation.PostprocessAsync(reader, exceptions, token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                throw TransformOperationException(operation, e);
            }
        }

        if (exceptions.Count == 1)
        {
            throw exceptions[0];
        }

        if (exceptions.Count > 1)
        {
            throw new AggregateException(exceptions);
        }
    }

    /// <summary>
    ///     Give an operation the chance to map a provider exception into a domain one — how a
    ///     duplicate stream id becomes an <c>ExistingStreamIdCollisionException</c> rather than a raw
    ///     constraint violation.
    /// </summary>
    private static Exception TransformOperationException(Weasel.Storage.IStorageOperation operation, Exception e)
    {
        if (operation is JasperFx.Core.Exceptions.IExceptionTransform transform &&
            transform.TryTransform(e, out var transformed) && transformed is not null)
        {
            return transformed;
        }

        return e;
    }

    // ---- IStorageSession ----

    IStorageSerializer IStorageSession.Serializer
        => _storageSerializer ??= StorageSerializerAdapter.For(FisherSerializer);

    IStorageDatabase IStorageSession.Database => FisherDatabase;

    IVersionTracker IStorageSession.Versions => _versionTracker ??= new FisherVersionTracker();

    /// <summary>
    ///     Fisher has no dirty tracking by design, mirroring Polecat, so no change trackers are ever
    ///     registered and the shared runtime iterates an empty list.
    /// </summary>
    IList<IChangeTracker> IStorageSession.ChangeTrackers => _changeTrackers ??= new List<IChangeTracker>();

    Dictionary<Type, object> IStorageSession.ItemMap => _itemMap ??= new Dictionary<Type, object>();

    ConcurrencyChecks IStorageSession.Concurrency => ConcurrencyChecks.Enabled;

    IDocumentStorage IStorageSession.StorageFor(Type documentType)
        => throw new NotImplementedException(
            "Fisher document storage is not implemented yet; this session supports event store operations only.");

    IDocumentStorage<T> IStorageSession.StorageFor<T>()
        => throw new NotImplementedException(
            "Fisher document storage is not implemented yet; this session supports event store operations only.");

    // ---- JasperFx.Events.IStorageOperations ----
    //
    // The projection write path. Fisher implements it only as far as live aggregation needs, which is
    // to say the type constraint and nothing else: JasperFx's aggregation generics require the write
    // session to be an IStorageOperations, but folding a stream in memory never calls through any of
    // these. They come alive with document storage and the projection graph.

    public bool EnableSideEffectsOnInlineProjections => EventGraph.EnableSideEffectsOnInlineProjections;

    Task<IProjectionStorage<TDoc, TId>> JasperFx.Events.IStorageOperations.FetchProjectionStorageAsync<TDoc, TId>(
        string tenantId, CancellationToken cancellationToken)
        => throw new NotImplementedException(
            "Fisher cannot persist projections yet; there is no document storage to write a snapshot to. " +
            "Live aggregation through Events.AggregateStreamAsync is supported.");

    public ValueTask<IMessageSink> GetOrStartMessageSink()
        => throw new NotImplementedException(
            "Fisher has no message outbox yet, so projection side effects cannot be published.");

    public virtual void MarkAsAddedForStorage(object id, object document)
    {
    }

    public virtual void MarkAsDocumentLoaded(object id, object document)
    {
    }

    public async Task<DbDataReader> ExecuteReaderAsync(DbCommand command, CancellationToken token = default)
    {
        var connection = await ConnectionAsync(token).ConfigureAwait(false);
        command.Connection = connection;
        command.CommandTimeout = Options.CommandTimeout;

        return await command.ExecuteReaderAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    ///     SQLite temporary tables are per-connection and live in the <c>temp</c> schema, so a plain
    ///     unique-per-session name suffices — there is no <c>#</c> prefix convention to honour as in
    ///     SQL Server.
    /// </summary>
    public string NextTempTableName() => $"fi_temp_{++_tempTableNumber}";

    // ---- IMetadataContext ----

    public string TenantId { get; internal set; }
    public string? CausationId { get; set; }
    public string? CorrelationId { get; set; }
    public string? CurrentUserName { get; set; }
    public Dictionary<string, object>? Headers { get; private set; }

    public bool CorrelationIdEnabled => Options.Events.EnableCorrelationId;
    public bool CausationIdEnabled => Options.Events.EnableCausationId;
    public bool HeadersEnabled => Options.Events.EnableHeaders;
    public bool UserNameEnabled => Options.Events.EnableUserName;

    /// <summary>
    ///     Set a header value carried on every event appended in this unit of work.
    /// </summary>
    public void SetHeader(string key, object value)
    {
        Headers ??= new Dictionary<string, object>();
        Headers[key] = value;
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }
}
