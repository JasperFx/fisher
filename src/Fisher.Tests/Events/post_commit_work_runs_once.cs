using Fisher.Events.Messaging;
using Fisher.Tests.Projections;
using JasperFx;
using JasperFx.Events;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Events;

/// <summary>
///     fisher#454, the Fisher half of marten#5688: what a save does at its COMMIT, and after it.
/// </summary>
public class post_commit_work_runs_once : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("post-commit-once");
    private readonly BusyOnceOutbox _outbox = new();
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.MessageOutbox = _outbox;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    /// <summary>
    ///     The outbox's <c>AfterCommitAsync</c> ran inside the resilience pipeline, after the commit. An
    ///     outbox writing to the same file meets the write lock there, and the <c>SQLITE_BUSY</c> made
    ///     the pipeline re-execute the whole write: the unit of work was written again. An append's
    ///     version guard caught that replay, so a save that had committed failed as a concurrency
    ///     conflict with itself; a documents-only save was silently rewritten.
    /// </summary>
    [Fact]
    public async Task a_transient_failure_after_the_commit_does_not_write_the_unit_of_work_again()
    {
        var streamId = Guid.NewGuid();

        await using (var seed = _store.LightweightSession())
        {
            seed.Events.StartStream(streamId, new QuestStarted("Ring"));
            await seed.SaveChangesAsync(Token);
        }

        _outbox.FailOnce = true;

        await using (var session = _store.LightweightSession())
        {
            var sink = await ((IStorageOperations)session).GetOrStartMessageSink();
            await sink.PublishAsync(new QuestFinished("done"), StorageConstants.DefaultTenantId);
            session.Events.Append(streamId, new MemberJoined("Sam"));

            // The outbox's own failure, as itself: not a concurrency conflict from a replay, and not
            // a StreamLockedException, which would say the commit never happened.
            var failure = await Should.ThrowAsync<SqliteException>(() => session.SaveChangesAsync(Token));
            failure.SqliteErrorCode.ShouldBe(5);
        }

        _outbox.BeforeCommitCalls.ShouldBe(1, "the unit of work was written once");
        _outbox.AfterCommitCalls.ShouldBe(1);

        await using var query = _store.QuerySession();
        (await query.Events.FetchStreamAsync(streamId, token: Token)).Count.ShouldBe(2);
    }

    /// <summary>
    ///     A token cancelled after the unit of work's statements ran, but before the COMMIT, rolls the
    ///     unit of work back and surfaces as <see cref="OperationCanceledException" />, with the write
    ///     lock released at once. Already true here, because the transaction is ended by
    ///     <c>await using</c> rather than by a <c>RollbackAsync(token)</c>; pinned because it is the
    ///     scenario marten#5688 broke.
    /// </summary>
    [Fact]
    public async Task a_token_cancelled_just_before_the_commit_rolls_back_and_throws()
    {
        var streamId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(streamId, new QuestStarted("Ring"));
            session.AddTransactionParticipant(new CancelBeforeCommit(cts));

            await Should.ThrowAsync<OperationCanceledException>(() => session.SaveChangesAsync(cts.Token));

            // Before the session is disposed: with no busy wait at all, BEGIN IMMEDIATE either gets
            // the write lock or fails on the spot with SQLITE_BUSY.
            await using var connection = new SqliteConnection(_database.ConnectionString);
            await connection.OpenAsync(Token);
            await using (var noWait = connection.CreateCommand())
            {
                noWait.CommandText = "pragma busy_timeout = 0";
                await noWait.ExecuteNonQueryAsync(Token);
            }

            await using var transaction = connection.BeginTransaction(deferred: false);
            await transaction.RollbackAsync(Token);
        }

        await using var query = _store.QuerySession();
        (await query.Events.FetchStreamAsync(streamId, token: Token)).ShouldBeEmpty();
    }

    private sealed class CancelBeforeCommit(CancellationTokenSource cts) : ITransactionParticipant
    {
        public Task BeforeCommitAsync(SqliteConnection connection, SqliteTransaction transaction,
            CancellationToken token)
        {
            // Every statement has run; the caller gives up before the COMMIT is sent
            cts.Cancel();
            return Task.CompletedTask;
        }
    }

    private sealed class BusyOnceOutbox : IMessageOutbox
    {
        public bool FailOnce;
        public int BeforeCommitCalls;
        public int AfterCommitCalls;

        public ValueTask<IMessageBatch> CreateBatch(IDocumentSession session)
            => new(new BusyOnceBatch(this));

        private sealed class BusyOnceBatch(BusyOnceOutbox outbox) : IMessageBatch
        {
            public ValueTask PublishAsync<T>(T message, string tenantId) => ValueTask.CompletedTask;

            public Task BeforeCommitAsync(CancellationToken token)
            {
                Interlocked.Increment(ref outbox.BeforeCommitCalls);
                return Task.CompletedTask;
            }

            public Task AfterCommitAsync(CancellationToken token)
            {
                Interlocked.Increment(ref outbox.AfterCommitCalls);

                // An outbox writing to the same SQLite file meets the write lock here
                if (outbox.FailOnce)
                {
                    outbox.FailOnce = false;
                    throw new SqliteException("database is locked", 5);
                }

                return Task.CompletedTask;
            }
        }
    }
}
