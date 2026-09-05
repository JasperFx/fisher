using System.Collections.Concurrent;
using Fisher.Internal;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;

namespace Fisher.Events.Daemon;

/// <summary>
///     One transaction's worth of async-projection work: the documents a projection wrote, plus the
///     progression row saying how far it got.
/// </summary>
/// <remarks>
///     <para>
///         <strong>Both halves commit together or neither does</strong>, and that is the whole point of
///         the type. Writing the snapshot and recording progress separately would let a crash between
///         them either replay events already applied or skip events never applied — the projection ends
///         up wrong in one direction or the other, permanently, with nothing to signal it.
///     </para>
///     <para>
///         Sessions are collected rather than merged. Each flushes its <em>own</em> queued operations
///         into the shared transaction, because an operation is configured against the session as its
///         storage context and that is what carries tenancy — running one session's operations through
///         another would quietly mis-scope them.
///     </para>
///     <para>
///         <c>BEGIN IMMEDIATE</c> as everywhere else in Fisher: the daemon is one more writer competing
///         for the single write lock, and taking it up front is what stops a deferred transaction from
///         discovering the conflict only at commit.
///     </para>
/// </remarks>
internal sealed class FisherProjectionBatch : IProjectionBatch<IDocumentSession, IQuerySession>
{
    private readonly DocumentStore _store;
    private readonly EventGraph _events;
    private readonly ConcurrentBag<IDocumentSession> _sessions = [];
    private readonly ConcurrentQueue<Weasel.Storage.IStorageOperation> _progress = new();

    // Null until a projection in this batch actually publishes, which keeps both commit hooks
    // no-ops for the common case of no bus integration.
    private Messaging.IMessageBatch? _messageBatch;
    private readonly SemaphoreSlim _messageBatchGate = new(1, 1);

    // Streams a projection raised events onto, planned at commit rather than when recorded.
    private readonly List<StreamAction> _raisedStreams = [];

    private readonly Fisher.Storage.FisherDatabase _database;

    internal FisherProjectionBatch(DocumentStore store, EventGraph events, Fisher.Storage.FisherDatabase database)
    {
        _store = store;
        _events = events;
        _database = database;
    }

    /// <summary>
    ///     A session for one tenant's slice of this batch.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Thread-safe because a composite projection may ask for sessions concurrently, which is why
    ///         the backing collection is a <see cref="ConcurrentBag{T}" /> rather than a list.
    ///     </para>
    ///     <para>
    ///         <b>Pinned to the batch's own database rather than resolved from the tenant id</b>
    ///         (fisher#57). The two agree under every tenancy but database-per-tenant, where resolving
    ///         would send a slice's documents to whichever file the tenant id names — which for a
    ///         batch's default-tenant slices is not the file its events came from.
    ///     </para>
    /// </remarks>
    public IDocumentSession SessionForTenant(string tenantId)
    {
        var session = _store.OpenSessionOn(_database, tenantId);
        _sessions.Add(session);
        return session;
    }

    /// <summary>
    ///     Record how far the shard reached, to commit alongside the projection's own writes.
    /// </summary>
    /// <remarks>
    ///     A floor of zero means the progression row may not exist yet, so the write upserts; past that
    ///     the row is known to be there and a plain update suffices.
    /// </remarks>
    public ValueTask RecordProgress(EventRange range)
    {
        Diagnostics.DaemonTrace.Record("batch.progress", range.ShardName.Identity,
            range.SequenceFloor, range.SequenceCeiling);

        _progress.Enqueue(_events.UpdateProgressOperation(range.ShardName.Identity, range.SequenceCeiling,
            upsert: range.SequenceFloor == 0));

        return ValueTask.CompletedTask;
    }

    public async Task ExecuteAsync(CancellationToken token)
    {
        var sessions = _sessions.OfType<FisherSession>().ToArray();

        // A snapshot type's table may never have been created — the daemon can easily be the first
        // thing to write one. Done before the transaction opens, because creating it is its own
        // migration on its own connection.
        foreach (var session in sessions)
        {
            foreach (var documentType in session.PendingOperations
                         .Select(x => x.DocumentType)
                         .Where(x => x is not null)
                         .Distinct())
            {
                if (_store.Options.Schema.HasMappingFor(documentType!))
                {
                    await _database.EnsureDocumentTableAsync(documentType!, token).ConfigureAwait(false);
                }
            }
        }

        // Taken here, outside the pipeline, and *not* inside the delegate — fisher#12. A retried
        // SQLITE_BUSY re-executes the whole delegate, so a drain in there would hand the retry an
        // empty queue: the transaction would commit the progression row for events whose documents
        // were never written, with no error anywhere. Every other piece of the delegate's input
        // (_raisedStreams, _progress) is already copied rather than consumed, for the same reason.
        var pending = sessions
            .Select(session => (Session: session, Operations: session.TakePendingOperations()))
            .ToArray();

        // Gathered here rather than read inside the delegate for the same reason as the operations
        // above — everything the delegate consumes has to survive being read twice.
        var participants = sessions.SelectMany(session => session.Participants).ToArray();

        Diagnostics.DaemonTrace.Record("batch.taken", null,
            pending.Sum(x => x.Operations.Count), _progress.Count, sessions.Length);

        await _store.Options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);

            foreach (var (session, operations) in pending)
            {
                await session.ExecuteOperationsAsync(connection, transaction, operations, ct)
                    .ConfigureAwait(false);
            }

            await AppendRaisedEventsAsync(connection, transaction, sessions, ct).ConfigureAwait(false);

            foreach (var operation in _progress)
            {
                await ExecuteProgressAsync(operation, connection, transaction, sessions.FirstOrDefault(), ct)
                    .ConfigureAwait(false);
            }

            // Last thing inside the transaction, so an outbox wanting its messages atomic with the
            // projection write and the progression row gets exactly that.
            if (_messageBatch is not null)
            {
                await _messageBatch.BeforeCommitAsync(ct).ConfigureAwait(false);
            }

            // And anything a projection asked to write with us, on the batch's connection and in the
            // batch's transaction — the same position and the same visibility semantics
            // FisherSession.SaveChangesAsync gives a participant, so a projection that enlists one
            // does not have to know which of the two commit paths it is running under.
            foreach (var participant in participants)
            {
                await participant.BeforeCommitAsync(connection, transaction, ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }, token).ConfigureAwait(false);

        // Outside the resilience pipeline: a retried SQLITE_BUSY re-runs the whole delegate, and a
        // post-commit publish that ran inside it would fire again for a transaction that already
        // committed. There is nothing to retry here anyway — the write is durable.
        if (_messageBatch is not null)
        {
            await _messageBatch.AfterCommitAsync(token).ConfigureAwait(false);
        }

        // Same position and the same reason — a participant holding its work replayable across
        // attempts is told once that the write is durable.
        foreach (var participant in participants)
        {
            await participant.AfterCommitAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Plan and write the events this batch's projections raised, inside the batch's transaction.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The same <see cref="AppendPlanner" /> a session's <c>SaveChangesAsync</c> uses, on the
    ///         batch's own connection and transaction — so a raised event is numbered from the stream's
    ///         version as read under the write lock, and the optimistic concurrency check runs there
    ///         too. A projection raising events onto a stream another writer has moved on fails the
    ///         batch, which is what stops the shard rather than writing a wrong version.
    ///     </para>
    ///     <para>
    ///         Tag rows come last and inside the same transaction, for the reason the session path has:
    ///         a tag is keyed by the <c>seq_id</c> only the append's trailing read-back supplies, and an
    ///         event that is visible but untagged is indistinguishable to a tag query from one that was
    ///         never tagged.
    ///     </para>
    /// </remarks>
    private async Task AppendRaisedEventsAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<FisherSession> sessions, CancellationToken token)
    {
        StreamAction[] raised;

        lock (_raisedStreams)
        {
            raised = _raisedStreams.ToArray();
        }

        if (raised.Length == 0)
        {
            return;
        }

        // The planner and the batch executor both need a session as their storage context. Any of this
        // batch's will do — the append is scoped by each StreamAction's own tenant id, not by the
        // session's — and one is opened only when no projection wrote a document.
        var owned = sessions.Count == 0;
        var context = owned ? (FisherSession)_store.LightweightSession() : sessions[0];

        try
        {
            var operations = await new AppendPlanner(context)
                .PlanAsync(raised, connection, transaction, token).ConfigureAwait(false);

            await context.ExecuteOperationsAsync(connection, transaction, operations, token).ConfigureAwait(false);

            await new Storage.EventTagWriter(_events)
                .WriteAsync(raised, connection, transaction, token).ConfigureAwait(false);
        }
        finally
        {
            if (owned)
            {
                await context.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteProgressAsync(Weasel.Storage.IStorageOperation operation,
        SqliteConnection connection, SqliteTransaction transaction, FisherSession? session, CancellationToken token)
    {
        // The progression write is store-global rather than tenant-scoped, so any session serves as
        // its storage context; one is opened only when the batch produced no sessions of its own,
        // which happens when a shard advances past events none of its projections matched.
        var owned = session is null;
        var context = session ?? (FisherSession)_store.LightweightSession();

        try
        {
            var builder = new Weasel.Sqlite.CommandBuilder();
            operation.ConfigureCommand(builder, context);

            // Disposed like every other compiled command — an undisposed SqliteCommand keeps its
            // native prepared statement alive until finalization.
            await using var command = builder.Compile();
            command.Connection = connection;
            command.Transaction = transaction;
            command.CommandTimeout = _store.Options.CommandTimeout;

            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally
        {
            if (owned)
            {
                await context.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // ---- event-emitting projections ----
    //
    // JasperFx's EventSlice.BuildOperations drives these three. They are synchronous, but Fisher
    // cannot build an append operation without reading the stream's current version, and that read has
    // to happen under the write lock this batch's transaction has not yet taken. So all three do the
    // same thing: record the StreamAction, and let ExecuteAsync plan it inside the transaction.
    //
    // All three funnel into one list because the StreamAction JasperFx hands over already carries every
    // raised event. The single-stream-start path calls QuickAppendEventWithVersion once per event and
    // then UpdateStreamVersion, all with the SAME action instance — so recording it once and planning
    // it whole covers that path too. Reference identity is what dedupes them.
    //
    // Marten queues three different storage operations here instead, which is right for Postgres. It
    // is not right for Fisher: the versions the slice pre-assigns are computed client-side from the
    // slice's own event count, whereas the planner re-reads the stream under the write lock and keeps
    // the optimistic guard real. Routing everything through the planner also means raised events go
    // through FisherQuickAppendEventsOperation and therefore get the trailing sequence read-back —
    // without which no tag row could be written for an event a projection raised.

    public void QuickAppendEventWithVersion(StreamAction action, IEvent @event) => RecordRaisedStream(action);

    public void UpdateStreamVersion(StreamAction action) => RecordRaisedStream(action);

    public void QuickAppendEvents(StreamAction action) => RecordRaisedStream(action);

    /// <summary>
    ///     Note a stream a projection raised events onto, for planning at commit.
    /// </summary>
    /// <remarks>
    ///     Locked rather than lock-free: a composite projection can raise events from several slices
    ///     concurrently, and the dedupe is a scan for reference identity rather than a hash lookup —
    ///     <see cref="StreamAction" /> overrides neither <c>Equals</c> nor <c>GetHashCode</c>, so a set
    ///     would compare by identity anyway but read as though it compared by value.
    /// </remarks>
    private void RecordRaisedStream(StreamAction action)
    {
        lock (_raisedStreams)
        {
            foreach (var existing in _raisedStreams)
            {
                if (ReferenceEquals(existing, action))
                {
                    return;
                }
            }

            _raisedStreams.Add(action);
        }
    }

    // ---- projection side effects ----

    /// <summary>
    ///     Publish a message a projection emitted while processing this batch.
    /// </summary>
    /// <remarks>
    ///     Buffered into the batch's <see cref="Messaging.IMessageBatch" /> rather than sent here.
    ///     <see cref="ExecuteAsync" /> fires its hooks around the commit, so a transaction that rolls
    ///     back publishes nothing. The default outbox drops the message; see
    ///     <see cref="StoreOptions.MessageOutbox" />.
    /// </remarks>
    public async Task PublishMessageAsync(object message, string tenantId)
    {
        var batch = await CurrentMessageBatchAsync().ConfigureAwait(false);
        await Messaging.MessagePublishing.PublishAsync(batch, message, tenantId).ConfigureAwait(false);
    }

    /// <inheritdoc cref="PublishMessageAsync(object, string)" />
    public async Task PublishMessageAsync(object message, MessageMetadata metadata)
    {
        var batch = await CurrentMessageBatchAsync().ConfigureAwait(false);
        await Messaging.MessagePublishing.PublishAsync(batch, message, metadata).ConfigureAwait(false);
    }

    /// <summary>
    ///     The batch's message buffer, created on first publish.
    /// </summary>
    /// <remarks>
    ///     Guarded, because a composite projection can publish from several tenants' work concurrently
    ///     — the same reason <see cref="SessionForTenant" /> writes into a concurrent collection. The
    ///     outbox is handed a session so it can enlist in this batch's transaction if it wants the
    ///     before-commit guarantee; one is opened if the batch has produced none of its own, which
    ///     happens when a projection publishes without writing a document.
    /// </remarks>
    private async ValueTask<Messaging.IMessageBatch> CurrentMessageBatchAsync()
    {
        if (_messageBatch is not null)
        {
            return _messageBatch;
        }

        await _messageBatchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_messageBatch is not null)
            {
                return _messageBatch;
            }

            var session = _sessions.FirstOrDefault() ?? SessionForTenant(JasperFx.StorageConstants.DefaultTenantId);

            return _messageBatch = await _store.Options.Events.MessageOutbox.CreateBatch(session)
                .ConfigureAwait(false);
        }
        finally
        {
            _messageBatchGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _messageBatchGate.Dispose();

        // Participants first, because one may still be holding something that writes on a session's
        // connection. A participant enlisted for a batch is scoped to that batch — an EF-backed
        // projection's DbContext is created per batch and cannot dispose itself, since it has to
        // outlive the apply that created it and survive a retry of the commit. Disposing here covers
        // the failed batch as well as the committed one, which is the case that would otherwise leak a
        // context per attempt behind a persistently failing shard.
        foreach (var session in _sessions.OfType<FisherSession>())
        {
            foreach (var participant in session.Participants)
            {
                switch (participant)
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        break;

                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
        }

        foreach (var session in _sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
