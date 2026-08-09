using System.Data.Common;
using System.Diagnostics;
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
internal partial class FisherSession : IDocumentSession, IStorageSession, IAsyncDisposable
{
    private readonly List<Weasel.Storage.IStorageOperation> _operations = new();

    /// <summary>
    ///     Guards <see cref="_operations" />.
    /// </summary>
    /// <remarks>
    ///     <b>fisher#13.</b> A session is normally driven by one caller, but the async daemon is not
    ///     one caller: a multi-stream projection applies its slices concurrently onto the <em>same</em>
    ///     session, so two slices can queue their document writes at the same moment. <c>List&lt;T&gt;.Add</c>
    ///     is not thread-safe, and the way it fails here is silent — two concurrent adds can leave the
    ///     count incremented once, so one slice's write simply never reaches the batch. The batch then
    ///     commits the progression row for a range whose documents were only partly written, with no
    ///     error anywhere. Exactly the shape of fisher#12, one layer up.
    /// </remarks>
    private readonly System.Threading.Lock _operationsLock = new();
    private List<IChangeTracker>? _changeTrackers;
    private readonly Sessions.IConnectionLifetime _lifetime;
    private Dictionary<Type, object>? _itemMap;
    private IStorageSerializer? _storageSerializer;
    private int _tempTableNumber;
    private FisherVersionTracker? _versionTracker;

    public FisherSession(StoreOptions options, FisherDatabase database, string tenantId)
        : this(options, database, new SessionOptions { TenantId = tenantId })
    {
    }

    public FisherSession(StoreOptions options, FisherDatabase database, SessionOptions sessionOptions)
    {
        sessionOptions.AssertValid();

        Options = options;
        FisherDatabase = database;
        SessionOptions = sessionOptions;
        TenantId = sessionOptions.TenantId;

        _lifetime = sessionOptions.ExternalConnection is { } external
            ? new Sessions.ExternalConnectionLifetime(external, sessionOptions.Transaction)
            : new Sessions.OwnedConnectionLifetime(database);

        Events = new EventOperations(this);

        // Seed distributed tracing context onto the session so appended events carry it without the
        // application passing anything. Root, not Id: the correlation id identifies the whole trace,
        // while the parent identifies the operation that caused this one. Marten and Polecat read the
        // ambient activity exactly this way. A caller assigning either property afterwards wins,
        // which is the point of doing it here rather than at append time.
        CorrelationId = Activity.Current?.RootId;
        CausationId = Activity.Current?.ParentId;
    }

    internal StoreOptions Options { get; }

    internal SessionOptions SessionOptions { get; }

    /// <summary>
    ///     The caller's transaction this session is enlisted in, or null for an ordinary session.
    /// </summary>
    internal SqliteTransaction? EnlistedTransaction => _lifetime.EnlistedTransaction;

    /// <summary>
    ///     The command timeout every statement this session runs carries — the session's own, or the
    ///     store's.
    /// </summary>
    internal int CommandTimeout => SessionOptions.Timeout ?? Options.CommandTimeout;

    internal FisherDatabase FisherDatabase { get; }

    internal EventGraph EventGraph => Options.EventGraph;

    internal Serialization.ISerializer FisherSerializer => Options.Serializer;

    /// <summary>
    ///     Every operation queued for the next <see cref="SaveChangesAsync" />.
    /// </summary>
    /// <remarks>
    ///     A snapshot, not the live list — see <see cref="_operationsLock" />. Enumerating the list
    ///     itself while a projection slice is still queueing would throw or skip.
    /// </remarks>
    internal IReadOnlyList<Weasel.Storage.IStorageOperation> PendingOperations
    {
        get
        {
            lock (_operationsLock)
            {
                return _operations.ToArray();
            }
        }
    }

    /// <summary>
    ///     The event store operations for this session.
    /// </summary>
    public EventOperations Events { get; }

    internal void QueueOperation(Weasel.Storage.IStorageOperation operation)
    {
        lock (_operationsLock)
        {
            _operations.Add(operation);
        }
    }

    private List<(JasperFx.Events.Tags.EventTagQuery Query, long LastSeenSequence)>? _boundaries;

    /// <summary>
    ///     Record a DCB boundary's consistency marker for checking at commit.
    /// </summary>
    internal void TrackBoundary(JasperFx.Events.Tags.EventTagQuery query, long lastSeenSequence)
        => (_boundaries ??= []).Add((query, lastSeenSequence));

    /// <summary>
    ///     Fail the commit if any event matching a tracked boundary's query has been appended since the
    ///     boundary was read.
    /// </summary>
    /// <remarks>
    ///     Runs on the session's own connection inside the write transaction. <c>BEGIN IMMEDIATE</c>
    ///     already holds the write lock by this point, so no competing writer can slip an event in
    ///     between this check and the commit — the same property that makes the append planner's
    ///     version read safe.
    /// </remarks>
    private async Task AssertBoundariesAreStillConsistentAsync(SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (_boundaries is not { Count: > 0 })
        {
            return;
        }

        foreach (var (query, lastSeen) in _boundaries)
        {
            if (await Events.AnyMatchingEventBeyondAsync(query, lastSeen, connection, transaction, token)
                    .ConfigureAwait(false))
            {
                throw new JasperFx.Events.DcbConcurrencyException(query, lastSeen);
            }
        }

        _boundaries.Clear();
    }

    /// <summary>
    ///     Open (or return) this session's connection. One connection per session for its whole
    ///     lifetime, so that reads inside a unit of work see the session's own uncommitted writes.
    /// </summary>
    internal ValueTask<SqliteConnection> ConnectionAsync(CancellationToken token = default)
        => _lifetime.ConnectionAsync(token);

    /// <summary>
    ///     Point a command at this session's connection, transaction and timeout.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every command a session runs goes through here — the batch executor, the LINQ provider,
    ///         the raw SQL reads and the storage's own loads. That matters for enlistment: joining the
    ///         caller's transaction is one line, in one place rather than at four sites that would each
    ///         have to remember.
    ///     </para>
    ///     <para>
    ///         <b>The transaction assignment is not tidiness — without it an enlisted session does not
    ///         work at all.</b> Microsoft.Data.Sqlite refuses to execute a command whose
    ///         <c>Transaction</c> is unset while its connection has a pending local transaction:
    ///         "Execute requires the command to have a transaction object". A command from
    ///         <c>connection.CreateCommand()</c> inherits the connection's transaction and so never
    ///         meets this; Weasel's command builder compiles a detached command, which is what every
    ///         Fisher statement is. Verified by removing the line — six of this feature's tests fail
    ///         with that exact message.
    ///     </para>
    /// </remarks>
    internal async ValueTask ConfigureCommandAsync(DbCommand command, CancellationToken token)
    {
        command.Connection = await ConnectionAsync(token).ConfigureAwait(false);
        command.Transaction = EnlistedTransaction;
        command.CommandTimeout = CommandTimeout;
    }

    /// <summary>
    ///     Flush every queued operation inside one transaction.
    /// </summary>
    public async Task SaveChangesAsync(CancellationToken token = default)
    {
        var streams = Events.PendingStreams.ToArray();

        if (PendingOperationCount == 0 && streams.Length == 0)
        {
            return;
        }

        // Inline projections run before the batch is taken, because applying one queues further
        // operations — the snapshot writes — that have to commit alongside the events that caused
        // them. Assigning event versions first is what lets a projection see them.
        if (streams.Length > 0)
        {
            await ApplyInlineProjectionsAsync(streams, token).ConfigureAwait(false);
        }

        var queued = TakePendingOperations();
        Events.ClearPendingStreams();

        // A document type can be stored without ever having been registered, and a snapshot type is
        // registered by projection configuration — so the first write of either may be the first time
        // its table is needed. Done before the transaction opens, because creating it is its own
        // migration on its own connection.
        await EnsureDocumentTablesAsync(queued, token).ConfigureAwait(false);

        var connection = await ConnectionAsync(token).ConfigureAwait(false);

        if (EnlistedTransaction is { } enlisted)
        {
            // Everything the unit of work holds, written into the caller's transaction — and then
            // nothing. No commit, because it is not ours; no resilience pipeline, because a retried
            // SQLITE_BUSY would re-execute a batch whose first attempt is still sitting in that
            // transaction rather than having rolled back with it; and no post-commit step, because
            // "everyone can see this now" is a claim only the caller's commit can make. See
            // SessionOptions.ForTransaction.
            await WriteAsync(connection, enlisted, queued, streams, token).ConfigureAwait(false);
        }
        else
        {
            await Options.ResiliencePipeline.ExecuteAsync(async ct =>
            {
                // BEGIN IMMEDIATE, not the default deferred transaction. The append planner reads each
                // stream's current version and then writes version+1; under a deferred transaction
                // SQLite would not take the write lock until that write, leaving a window where two
                // sessions both read version N. IMMEDIATE takes the lock up front, which is Fisher's
                // stand-in for Marten's advisory lock and Polecat's UPDLOCK/HOLDLOCK read.
                await using var transaction = (SqliteTransaction)await connection
                    .BeginTransactionAsync(SessionOptions.IsolationLevel, ct).ConfigureAwait(false);

                await WriteAsync(connection, transaction, queued, streams, ct).ConfigureAwait(false);

                await transaction.CommitAsync(ct).ConfigureAwait(false);

                if (_messageBatch is not null)
                {
                    await _messageBatch.AfterCommitAsync(ct).ConfigureAwait(false);
                }

                NotifyAppendObserver(streams);
            }, token).ConfigureAwait(false);
        }

        // Not reused across units of work: a batch's hooks have fired, and a second SaveChangesAsync
        // publishing through it would flush the same messages again.
        _messageBatch = null;
    }

    /// <summary>
    ///     Everything a unit of work does inside its transaction, up to but not including the commit.
    /// </summary>
    private async Task WriteAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<Weasel.Storage.IStorageOperation> queued, StreamAction[] streams, CancellationToken token)
    {
        var operations = new List<Weasel.Storage.IStorageOperation>(queued);

        if (streams.Length > 0)
        {
            var planned = await new AppendPlanner(this)
                .PlanAsync(streams, connection, transaction, token).ConfigureAwait(false);

            operations.AddRange(planned);
        }

        // Before anything is written: a boundary's guarantee is that no matching event has landed
        // since it was read, and checking after the write would be checking against our own
        // appends. Inside the transaction because BEGIN IMMEDIATE already holds the write lock,
        // which is what makes the check meaningful rather than advisory.
        await AssertBoundariesAreStillConsistentAsync(connection, transaction, token).ConfigureAwait(false);

        await ExecuteBatchAsync(connection, transaction, operations, token).ConfigureAwait(false);

        // After the batch, because a tag row is keyed by the seq_id the append's trailing
        // read-back has only just supplied; inside the transaction, because an event that is
        // visible but not yet tagged is indistinguishable to a tag query from one that was never
        // tagged at all.
        if (streams.Length > 0)
        {
            await new Events.Storage.EventTagWriter(EventGraph)
                .WriteAsync(streams, connection, transaction, token).ConfigureAwait(false);

            // Beside the tag writer and for the same reason — a key registered outside the append's
            // transaction leaves either a stream no key resolves to or a key naming a stream that does
            // not exist. Unlike a tag row this needs nothing from the appends' postprocessing, since
            // the stream id was known before any event was written; it runs here so there is one place
            // that says "and these rows commit with the events too".
            await new Events.Storage.NaturalKeyWriter(EventGraph, Options.Projections.NaturalKeys)
                .WriteAsync(streams, connection, transaction, token).ConfigureAwait(false);
        }

        // Last thing inside the transaction: an outbox that wants its messages to be atomic with
        // the write persists them here. Null unless something actually published.
        if (_messageBatch is not null)
        {
            await _messageBatch.BeforeCommitAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Create the table for every document type written in this unit of work that does not have
    ///     one yet.
    /// </summary>
    /// <remarks>
    ///     Only types the schema has already mapped are considered. Every document operation's type
    ///     was mapped when its storage was resolved, so asking the schema is how a document write is
    ///     told apart from an event one — and asking rather than mapping is why this cannot create a
    ///     mapping as a side effect of the question.
    /// </remarks>
    private async Task EnsureDocumentTablesAsync(IReadOnlyList<Weasel.Storage.IStorageOperation> operations,
        CancellationToken token)
    {
        if (operations.Count == 0)
        {
            return;
        }

        foreach (var documentType in operations.Select(x => x.DocumentType).Distinct())
        {
            if (documentType is null || !Options.Schema.HasMappingFor(documentType))
            {
                continue;
            }

            if (EnlistedTransaction is not null)
            {
                await AssertDocumentTableExistsAsync(documentType, token).ConfigureAwait(false);
            }
            else
            {
                await FisherDatabase.EnsureDocumentTableAsync(documentType, token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     For an enlisted session, check that a document type's table is already there rather than
    ///     creating it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Creating it runs a migration on its own connection, and the caller's transaction is
    ///         holding the write lock — so the ordinary path would block for the connection's busy
    ///         timeout and then fail with <c>SQLITE_BUSY</c>, which is a session deadlocking against
    ///         itself dressed up as contention. Naming the type and the fix beats waiting thirty
    ///         seconds to be told "database is locked".
    ///     </para>
    ///     <para>
    ///         The check runs on the caller's own connection, so a table they created inside the same
    ///         transaction counts as existing — which is what makes "create your tables and Fisher's in
    ///         one transaction" work at all.
    ///     </para>
    /// </remarks>
    private async Task AssertDocumentTableExistsAsync(Type documentType, CancellationToken token)
    {
        var table = Options.Schema.MappingFor(documentType).TableName;

        await using var command = new SqliteCommand(
            "select 1 from sqlite_master where type = 'table' and name = $name");
        command.Parameters.AddWithValue("$name", table.Name);

        await ConfigureCommandAsync(command, token).ConfigureAwait(false);

        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException(
                $"There is no table '{table.Name}' for document type {documentType.FullName}, and a session "
                + "enlisted in a transaction it does not own cannot create one — the migration runs on its "
                + "own connection, which would block against the write lock that transaction is holding. "
                + "Call ApplyAllConfiguredChangesToDatabaseAsync (or create the table inside your own "
                + "transaction) before enlisting.");
        }
    }

    /// <summary>
    ///     Fold this unit of work's events through every inline projection, queueing the resulting
    ///     snapshot writes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Event versions have to be assigned before a projection sees them — an aggregate whose
    ///         <c>Apply</c> reads <c>IEvent.Version</c>, and the version stamped onto the snapshot,
    ///         both depend on it. Fisher normally assigns versions inside the write transaction, where
    ///         the current stream version has just been read under the write lock. Doing it here means
    ///         reading that version slightly earlier, outside the lock.
    ///     </para>
    ///     <para>
    ///         That is safe because it is not the guard: the same versions are re-derived inside the
    ///         transaction by <see cref="AppendPlanner" />, and the optimistic concurrency check still
    ///         happens there. A racing writer makes the commit fail, exactly as it would have.
    ///     </para>
    /// </remarks>
    private async Task ApplyInlineProjectionsAsync(IReadOnlyList<StreamAction> streams, CancellationToken token)
    {
        var projections = Options.Projections.BuildInlineProjections();

        if (projections.Length == 0)
        {
            return;
        }

        await new AppendPlanner(this).AssignVersionsAheadOfProjectionsAsync(streams, token).ConfigureAwait(false);

        foreach (var projection in projections)
        {
            await projection.ApplyAsync(this, streams, token).ConfigureAwait(false);
        }
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
    /// <summary>
    ///     Take this session's queued operations, clearing the queue.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         For the async daemon's projection batch, which spans several tenant sessions but must
    ///         commit them — and the progression write — in one transaction, so a crash cannot leave a
    ///         projection ahead of or behind its recorded progress. Each session's operations are run
    ///         back through <see cref="ExecuteOperationsAsync" /> on that session, because
    ///         <c>ConfigureCommand</c> takes the session as its storage context and that is what
    ///         carries tenancy; executing one session's operations against another's would quietly
    ///         mis-scope them.
    ///     </para>
    ///     <para>
    ///         <b>Draining and executing are separate on purpose</b> — see fisher#12. The projection
    ///         batch runs its transaction inside the resilience pipeline, so a retried
    ///         <c>SQLITE_BUSY</c> re-executes the whole delegate; a drain inside it would leave the
    ///         retry with nothing to write while the progression row still committed, advancing a
    ///         projection past events whose documents were never written.
    ///     </para>
    /// </remarks>
    internal IReadOnlyList<Weasel.Storage.IStorageOperation> TakePendingOperations()
    {
        lock (_operationsLock)
        {
            var queued = _operations.ToArray();
            _operations.Clear();

            return queued;
        }
    }

    private int PendingOperationCount
    {
        get
        {
            lock (_operationsLock)
            {
                return _operations.Count;
            }
        }
    }

    /// <summary>
    ///     Run a set of operations built elsewhere through this session's batch executor, on a
    ///     connection and transaction it does not own.
    /// </summary>
    /// <remarks>
    ///     For the projection batch's raised events: the operations are planned there but still need a
    ///     session as their storage context, and the executor's exception transforms are what turn a
    ///     SQLite constraint code into Fisher's own exception types.
    /// </remarks>
    internal Task ExecuteOperationsAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<Weasel.Storage.IStorageOperation> operations, CancellationToken token)
        => operations.Count == 0
            ? Task.CompletedTask
            : ExecuteBatchAsync(connection, transaction, operations, token);

    private async Task ExecuteBatchAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<Weasel.Storage.IStorageOperation> operations, CancellationToken token)
    {
        var exceptions = new List<Exception>();

        foreach (var operation in operations)
        {
            var builder = new Weasel.Sqlite.CommandBuilder();
            operation.ConfigureCommand(builder, this);

            var command = builder.Compile();
            command.Connection = connection;
            command.Transaction = transaction;
            command.CommandTimeout = CommandTimeout;

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
        => (IDocumentStorage)typeof(FisherSession)
            .GetMethod(nameof(StorageFor), System.Reflection.BindingFlags.NonPublic |
                                           System.Reflection.BindingFlags.Instance)!
            .MakeGenericMethod(documentType)
            .Invoke(this, null)!;

    IDocumentStorage<T> IStorageSession.StorageFor<T>() => StorageFor<T>();

    /// <summary>
    ///     The storage flavor this session reads and writes <typeparamref name="T" /> through.
    /// </summary>
    /// <remarks>
    ///     Fisher only opens lightweight sessions today, so this always resolves the lightweight
    ///     flavor. The identity-map and query-only flavors are built and cached alongside it, waiting
    ///     on the session kinds that would select them.
    /// </remarks>
    internal IDocumentStorage<T> StorageFor<T>() where T : notnull
        => FisherDatabase.Providers.StorageFor<T>().Lightweight;

    // ---- JasperFx.Events.IStorageOperations ----
    //
    // The projection write path. Fisher implements it only as far as live aggregation needs, which is
    // to say the type constraint and nothing else: JasperFx's aggregation generics require the write
    // session to be an IStorageOperations, but folding a stream in memory never calls through any of
    // these. They come alive with document storage and the projection graph.

    public bool EnableSideEffectsOnInlineProjections => EventGraph.EnableSideEffectsOnInlineProjections;

    /// <summary>
    ///     Where an inline projection writes its snapshot for this tenant.
    /// </summary>
    /// <remarks>
    ///     The document table is created on demand here rather than at configuration time: a snapshot
    ///     type is registered through <c>Projections.Snapshot&lt;T&gt;</c>, which may run after the
    ///     schema was last applied.
    /// </remarks>
    async Task<IProjectionStorage<TDoc, TId>>
        JasperFx.Events.IStorageOperations.FetchProjectionStorageAsync<TDoc, TId>(
            string tenantId, CancellationToken cancellationToken)
    {
        await FisherDatabase.EnsureDocumentTableAsync(typeof(TDoc), cancellationToken).ConfigureAwait(false);

        var storage = (Weasel.Storage.IDocumentStorage<TDoc, TId>)StorageFor<TDoc>();

        return new Projections.FisherProjectionStorage<TDoc, TId>(this, storage, tenantId);
    }

    private Events.Messaging.IMessageBatch? _messageBatch;

    /// <summary>
    ///     The message batch this unit of work publishes side effects through, created on first use.
    /// </summary>
    /// <remarks>
    ///     Lazy on purpose: a session that never publishes never asks the outbox for a batch, so the
    ///     commit-time hooks stay no-ops for the overwhelmingly common case of an application with no
    ///     bus integration.
    /// </remarks>
    public async ValueTask<IMessageSink> GetOrStartMessageSink()
    {
        return _messageBatch ??= await Options.Events.MessageOutbox.CreateBatch(this).ConfigureAwait(false);
    }

    public virtual void MarkAsAddedForStorage(object id, object document)
    {
    }

    public virtual void MarkAsDocumentLoaded(object id, object document)
    {
    }

    public async Task<DbDataReader> ExecuteReaderAsync(DbCommand command, CancellationToken token = default)
    {
        await ConfigureCommandAsync(command, token).ConfigureAwait(false);

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

    /// <inheritdoc />
    public void QueueSqlCommand(string sql, params object?[] parameterValues)
        => QueueSqlCommand('?', sql, parameterValues);

    /// <inheritdoc />
    public void QueueSqlCommand(char placeholder, string sql, params object?[] parameterValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(parameterValues);

        QueueOperation(new Operations.ExecuteSqlStorageOperation(sql, placeholder, parameterValues));
    }

    /// <inheritdoc />
    public void SetHeader(string key, object value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        Headers ??= new Dictionary<string, object>();
        Headers[key] = value;
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        await _lifetime.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Synchronous disposal, for a container scope that disposes synchronously.
    /// </summary>
    /// <remarks>
    ///     Sessions are registered scoped by <c>AddFisher</c>, so this is the disposal path an
    ///     ASP.NET Core request actually takes when the scope is disposed through
    ///     <see cref="IDisposable" />. A container refuses outright to dispose a service offering only
    ///     <see cref="IAsyncDisposable" />, so declaring only the async form would make a scoped session
    ///     unusable rather than merely less efficient. <c>SqliteConnection</c> supplies both, so there
    ///     is nothing to block on.
    /// </remarks>
    public void Dispose()
    {
        GC.SuppressFinalize(this);

        _lifetime.Dispose();
    }
}
