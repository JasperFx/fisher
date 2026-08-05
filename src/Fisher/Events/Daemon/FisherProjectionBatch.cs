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

    internal FisherProjectionBatch(DocumentStore store, EventGraph events)
    {
        _store = store;
        _events = events;
    }

    /// <summary>
    ///     A session for one tenant's slice of this batch.
    /// </summary>
    /// <remarks>
    ///     Thread-safe because a composite projection may ask for sessions concurrently, which is why
    ///     the backing collection is a <see cref="ConcurrentBag{T}" /> rather than a list.
    /// </remarks>
    public IDocumentSession SessionForTenant(string tenantId)
    {
        var session = _store.LightweightSession(tenantId);
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
                    await _store.Database.EnsureDocumentTableAsync(documentType!, token).ConfigureAwait(false);
                }
            }
        }

        await _store.Options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await _store.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);

            foreach (var session in sessions)
            {
                await session.FlushOperationsAsync(connection, transaction, ct).ConfigureAwait(false);
            }

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

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }, token).ConfigureAwait(false);

        // Outside the resilience pipeline: a retried SQLITE_BUSY re-runs the whole delegate, and a
        // post-commit publish that ran inside it would fire again for a transaction that already
        // committed. There is nothing to retry here anyway — the write is durable.
        if (_messageBatch is not null)
        {
            await _messageBatch.AfterCommitAsync(token).ConfigureAwait(false);
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

            var command = builder.Compile();
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
    // fisher#3. A projection that appends events of its own needs the append planner's version
    // assignment and sequence read-back inside this batch's transaction. Throwing names the gap
    // rather than dropping the events.

    public void QuickAppendEventWithVersion(StreamAction action, IEvent @event) => throw EventEmission();

    public void UpdateStreamVersion(StreamAction action) => throw EventEmission();

    public void QuickAppendEvents(StreamAction action) => throw EventEmission();

    private static NotSupportedException EventEmission()
        => new("Fisher does not support an async projection that appends events yet — see "
               + "https://github.com/JasperFx/fisher/issues/3. The projection's own document writes are "
               + "supported; emitting new events from one is not.");

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

        foreach (var session in _sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
