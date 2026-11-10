using System.Collections.Concurrent;
using JasperFx.Events.Documents;
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
///     <para>
///         <b>The session list, the message batch, the raised-event bookkeeping and the post-commit ordering
///         are the shared <see cref="ProjectionBatchBase{TSession}" />'s</b> (jasperfx#1045). What stays here is
///         the SQLite transaction, the progression write and planning raised events under the write lock.
///         One behaviour came with the base: outside <see cref="ShardExecutionMode.Continuous" /> a projection
///         publishes nothing and raises nothing, because a rebuild or catch-up replays history and a message
///         or raised event from one is a second delivery of something already done.
///     </para>
/// </remarks>
internal sealed class FisherProjectionBatch : ProjectionBatchBase<IDocumentSession>,
    IProjectionBatch<IDocumentSession, IQuerySession>
{
    private readonly DocumentStore _store;
    private readonly EventGraph _events;
    private readonly ConcurrentQueue<Weasel.Storage.IStorageOperation> _progress = new();
    private readonly Fisher.Storage.FisherDatabase _database;

    // Gathered before the resilience pipeline (fisher#12) and told about the commit after it.
    private ITransactionParticipant[] _participants = [];

    internal FisherProjectionBatch(DocumentStore store, EventGraph events, Fisher.Storage.FisherDatabase database,
        ShardExecutionMode mode) : base(mode)
    {
        _store = store;
        _events = events;
        _database = database;
    }

    protected override IMessageOutbox Outbox => _store.Options.Events.MessageOutbox;

    protected override IDocumentSession OpenSessionForTenant(string tenantId)
    {
        var session = _store.OpenSessionOn(_database, tenantId);

        // fisher#305. The batch owns this session's participants, not the session — JasperFx's
        // ProjectionExecution takes a session from here with `await using` and disposes it the moment
        // the projection has been applied, while this batch lives on to run their BeforeCommitAsync.
        // Without the flag, that disposal would release an EF-backed projection's DbContext between
        // the apply and the write and the entities would silently never be saved.
        if (session is FisherSession fisherSession)
        {
            fisherSession.ParticipantsOwnedByBatch = true;
        }

        return session;
    }

    public override ValueTask RecordProgress(EventRange range)
    {
        Diagnostics.DaemonTrace.Record("batch.progress", range.ShardName.Identity,
            range.SequenceFloor, range.SequenceCeiling);

        _progress.Enqueue(new Storage.ShardProgressionOperation(_events.ProgressionTableName,
            range.ShardName.Identity, range.SequenceFloor, range.SequenceCeiling,
            _events.EnableExtendedProgressionTracking));

        return ValueTask.CompletedTask;
    }

    protected override async Task ExecuteOperationsInTransactionAsync(Func<CancellationToken, Task> beforeCommit,
        CancellationToken token)
    {
        var sessions = Sessions.OfType<FisherSession>().ToArray();

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
        // (RaisedEvents, _progress) is already copied rather than consumed, for the same reason.
        var pending = sessions
            .Select(session => (Session: session, Operations: session.TakePendingOperations()))
            .ToArray();

        // Gathered here rather than read inside the delegate for the same reason as the operations
        // above — everything the delegate consumes has to survive being read twice.
        var participants = sessions.SelectMany(session => session.Participants).ToArray();
        _participants = participants;

        var raised = RaisedEvents;

        Diagnostics.DaemonTrace.Record("batch.taken", null,
            pending.Sum(x => x.Operations.Count), _progress.Count, sessions.Length);

        await _store.Options.ResiliencePipeline.ExecuteAsync(async ct =>
        {
            await using var connection = await _database.OpenConnectionAsync(ct).ConfigureAwait(false);

            // fisher#208. The daemon is one more writer competing for the file's single write lock, and
            // charting its wait beside a session's is what tells "the application is contended" apart
            // from "the daemon is starving the application" — the two look identical from a session's
            // side alone.
            var waitedFrom = _store.Options.OpenTelemetry.StartWriteLockWait();

            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);

            _store.Options.OpenTelemetry.RecordWriteLockWait(
                waitedFrom, Fisher.Services.OpenTelemetryOptions.DaemonHolder);

            foreach (var (session, operations) in pending)
            {
                await session.ExecuteOperationsAsync(connection, transaction, operations, ct)
                    .ConfigureAwait(false);
            }

            await AppendRaisedEventsAsync(raised, connection, transaction, sessions, ct).ConfigureAwait(false);

            foreach (var operation in _progress)
            {
                await ExecuteProgressAsync(operation, connection, transaction, sessions.FirstOrDefault(), ct)
                    .ConfigureAwait(false);
            }

            // Anything a projection asked to write with us, on the batch's connection and in the batch's
            // transaction — the same position and visibility FisherSession.SaveChangesAsync gives a
            // participant, so a projection that enlists one does not have to know which of the two
            // commit paths it is running under.
            foreach (var participant in participants)
            {
                await participant.BeforeCommitAsync(connection, transaction, ct).ConfigureAwait(false);
            }

            // Last thing inside the transaction, as the shared base requires, so an outbox wanting its
            // messages atomic with the projection write and the progression row gets exactly that.
            await beforeCommit(ct).ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }

    // Runs after the commit, outside the resilience pipeline and not on the shard's token (fisher#434):
    // the base calls it once the write is durable, and a participant holding its work replayable across
    // attempts is told so exactly once.
    protected override async Task AfterCommitAsync()
    {
        foreach (var participant in _participants)
        {
            await participant.AfterCommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    // Raised events are planned INSIDE the transaction rather than through PlanRaisedEventsAsync, which
    // runs before it: Fisher numbers them from a version read under the write lock, so the optimistic guard
    // stays real, and routes them through FisherQuickAppendEventsOperation for the trailing sequence
    // read-back a tag row needs.
    private async Task AppendRaisedEventsAsync(IReadOnlyList<StreamAction> raised, SqliteConnection connection,
        SqliteTransaction transaction, IReadOnlyList<FisherSession> sessions, CancellationToken token)
    {
        if (raised.Count == 0)
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
            var operations = await new AppendPlanner(context, JasperFx.Events.Appending.AppendOrigin.RaisedByProjection)
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

            // Read rather than executed blind, so the operation's postprocess can say whether its guard
            // matched — the progression write is guarded on the range's floor (fisher#402).
            var exceptions = new List<Exception>();
            await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                await operation.PostprocessAsync(reader, exceptions, token).ConfigureAwait(false);
            }

            if (exceptions.Count > 0)
            {
                throw exceptions.Count == 1 ? exceptions[0] : new AggregateException(exceptions);
            }
        }
        finally
        {
            if (owned)
            {
                await context.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        // Participants first, because one may still be holding something that writes on a session's
        // connection. A participant enlisted for a batch is scoped to that batch — an EF-backed
        // projection's DbContext is created per batch and cannot dispose itself, since it has to
        // outlive the apply that created it and survive a retry of the commit. Disposing here covers
        // the failed batch as well as the committed one, which is the case that would otherwise leak a
        // context per attempt behind a persistently failing shard.
        // Through the session's own drain rather than a loop here (fisher#305), so the release lives
        // in one place — the session releases its own participants too, for the inline path that has
        // no batch to do it. The drain takes the list and nulls it, so the second owner to reach a
        // participant finds nothing and cannot dispose it twice.
        foreach (var session in Sessions.OfType<FisherSession>())
        {
            await session.DisposeParticipantsAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
