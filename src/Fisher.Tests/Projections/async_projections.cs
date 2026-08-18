using Fisher.Linq;
using Fisher.Tests.Events;
using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Weasel.Sqlite;

namespace Fisher.Tests.Projections;

/// <summary>
///     Async snapshots driven by the projection daemon — the Fisher-specific half of that milestone.
/// </summary>
/// <remarks>
///     <c>AsyncDaemonCompliance</c> already covers "catch up, persist, rebuild" for every Critter Stack
///     store. What is here is what the shared suite cannot see: that Async really does route away from
///     the inline path rather than being quietly applied in the same transaction (which would let the
///     shared suite pass for entirely the wrong reason), that a first-ever rebuild works before the
///     projection's table exists, and that a non-WAL journal mode is reported rather than silently
///     serializing the daemon behind every writer.
/// </remarks>
public class async_projections : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("async-projection");
    private DocumentStore _store = null!;
    private IProjectionDaemon? _daemon;

    public async ValueTask InitializeAsync()
    {
        _store = BuildStore();
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    private DocumentStore BuildStore(Action<StoreOptions>? extra = null)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<AsyncQuestTally>(SnapshotLifecycle.Async);
            extra?.Invoke(options);
        });

    public async ValueTask DisposeAsync()
    {
        if (_daemon is not null)
        {
            await _daemon.StopAllAsync();
            _daemon.Dispose();
        }

        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<IProjectionDaemon> StartDaemonAsync()
    {
        _daemon = await _store.BuildProjectionDaemonAsync();
        await _daemon.StartAllAsync();

        return _daemon;
    }

    private async Task<Guid> AppendAsync(params object[] events)
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Events.StartStream<AsyncQuestTally>(streamId, events);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return streamId;
    }

    /// <summary>
    ///     The negative half of the compliance suite's positive one.
    /// </summary>
    /// <remarks>
    ///     If <c>Snapshot&lt;T&gt;(Async)</c> were registered as Inline — the shape the code had while
    ///     Async was rejected outright — the compliance tests would still pass, because the document
    ///     would already be there by the time the daemon was asked for it. Asserting its <em>absence</em>
    ///     before the daemon runs is what tells the two apart.
    /// </remarks>
    [Fact]
    public async Task an_async_snapshot_is_not_written_by_the_commit_that_appended_the_events()
    {
        var streamId = await AppendAsync(new MemberJoined("Frodo"), new MonsterSlain("Balrog"));

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<AsyncQuestTally>(streamId, TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task the_daemon_catches_up_and_records_its_progress()
    {
        var streamId = await AppendAsync(new MemberJoined("Frodo"), new MemberJoined("Sam"),
            new MonsterSlain("Balrog"));

        await StartDaemonAsync();
        await _store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));

        await using var query = _store.LightweightSession();
        var tally = await query.LoadAsync<AsyncQuestTally>(streamId, TestContext.Current.CancellationToken);

        tally.ShouldNotBeNull();
        tally.Members.ShouldBe(2);
        tally.MonstersSlain.ShouldBe(1);

        // The progression row is the other half of the batch's transaction; a snapshot written without
        // it would be replayed on the next start.
        var progress = await _store.Database.AllProjectionProgress(TestContext.Current.CancellationToken);
        var highest = await _store.Database.FetchHighestEventSequenceNumber(TestContext.Current.CancellationToken);

        progress.ShouldContain(x => x.ShardName.StartsWith("AsyncQuestTally", StringComparison.Ordinal)
                                    && x.Sequence == highest);
    }

    /// <summary>
    ///     fisher#12 — a retried projection batch must still write its documents.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The batch runs its whole transaction inside <c>StoreOptions.ResiliencePipeline</c>, which
    ///         exists to retry <c>SQLITE_BUSY</c>. Every input to that delegate therefore has to survive
    ///         being read twice. <c>FlushOperationsAsync</c> did not: it drained the session's queue as
    ///         it executed, so an attempt that failed after flushing left the retry with nothing to
    ///         write — and the retry then committed the <em>progression row</em> for events whose
    ///         documents were never written. No exception, no shard failure, and a projection
    ///         permanently missing a slice.
    ///     </para>
    ///     <para>
    ///         The failure is injected through the outbox rather than by contending for the write lock,
    ///         because the injection point has to be <em>after</em> the flush and inside the delegate.
    ///         A competing writer fails the <c>BEGIN IMMEDIATE</c> instead, which is before the flush
    ///         and therefore retries cleanly with or without the bug.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_retried_projection_batch_still_writes_its_documents()
    {
        await using var database = TemporaryDatabase.Create("batch-retry");
        var outbox = new FailOnceOutbox();

        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.MessageOutbox = outbox;
            options.Projections.Add(new AnnouncingProjection(), ProjectionLifecycle.Async);

            options.ExtendPolly(builder => builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<TransientBatchFailure>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.Zero
            }));
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(Guid.NewGuid(), new QuestStarted("Destroy the ring"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        try
        {
            await store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));

            // The first attempt was thrown away; the retry has to have written the snapshot, not just
            // the progression row that says it did.
            outbox.Failures.ShouldBe(1);

            await using var query = store.LightweightSession();
            (await query.Query<AnnouncedQuest>().CountAsync(TestContext.Current.CancellationToken))
                .ShouldBe(1);
        }
        finally
        {
            await daemon.StopAllAsync();
            daemon.Dispose();
        }
    }

    /// <summary>
    ///     Teardown tolerates a projection whose document table is not there.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A rebuild clears the projection's documents first, and the table may not exist — Fisher
    ///         creates document tables on demand, so a store whose schema was never applied reaches its
    ///         first rebuild with nothing to delete from.
    ///     </para>
    ///     <para>
    ///         The trap is that on SQLite a <c>delete from</c> naming a missing table fails when the
    ///         statement is <em>prepared</em>, so guarding it with a
    ///         <c>where exists (select … from sqlite_master …)</c> predicate does not help: the guard
    ///         never gets to run. The existence test has to happen in C# first. Dropping the table here
    ///         is how that state is constructed deterministically — the fixture applies the schema up
    ///         front, so the table would otherwise always be present.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task rebuilding_when_the_projection_table_does_not_exist()
    {
        var streamId = await AppendAsync(new MemberJoined("Frodo"), new MonsterSlain("Balrog"));

        await DropTableAsync(_store.Options.Schema.For<AsyncQuestTally>().Mapping.TableName.Name);

        _daemon = await _store.BuildProjectionDaemonAsync();
        await _daemon.RebuildProjectionAsync<AsyncQuestTally>(TestContext.Current.CancellationToken);

        await using var query = _store.LightweightSession();
        var tally = await query.LoadAsync<AsyncQuestTally>(streamId, TestContext.Current.CancellationToken);

        tally.ShouldNotBeNull();
        tally.Members.ShouldBe(1);
        tally.MonstersSlain.ShouldBe(1);
    }

    private async Task DropTableAsync(string table)
    {
        await using var connection = await _store.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"drop table if exists \"{table}\"";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        // The store caches which document tables it has already created; without this the rebuild would
        // skip the migration and write into a table that is no longer there.
        _store.Database.ForgetEnsuredTables();
    }

    /// <summary>
    ///     Rebuilding twice must not double-count: the second run tears the first one's documents down.
    /// </summary>
    [Fact]
    public async Task a_second_rebuild_starts_from_an_empty_projection()
    {
        var streamId = await AppendAsync(new MemberJoined("Frodo"), new MemberJoined("Sam"));

        var daemon = await StartDaemonAsync();
        await daemon.RebuildProjectionAsync<AsyncQuestTally>(TestContext.Current.CancellationToken);
        await daemon.RebuildProjectionAsync<AsyncQuestTally>(TestContext.Current.CancellationToken);

        await using var query = _store.LightweightSession();
        var tally = await query.LoadAsync<AsyncQuestTally>(streamId, TestContext.Current.CancellationToken);

        tally.ShouldNotBeNull();
        tally.Members.ShouldBe(2);
    }

    /// <summary>
    ///     Starting the daemon against a non-WAL database warns rather than quietly serializing.
    /// </summary>
    /// <remarks>
    ///     Without WAL, SQLite blocks readers while a writer holds the database, so the daemon and every
    ///     application session contend instead of overlapping. That presents as a slow projection, not as
    ///     a misconfiguration, which is why it is said out loud at the one moment somebody is looking.
    /// </remarks>
    [Fact]
    public async Task starting_the_daemon_without_wal_warns()
    {
        await using var store = BuildStore(options =>
        {
            options.PragmaSettings = new SqlitePragmaSettings { JournalMode = JournalMode.DELETE };
        });

        var logger = new RecordingLogger();
        var daemon = await store.BuildProjectionDaemonAsync(logger: logger);
        daemon.Dispose();

        logger.Warnings.ShouldContain(x => x.Contains("WAL", StringComparison.Ordinal));
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}

/// <summary>
///     A self-aggregating snapshot maintained by the daemon. Declared here rather than reusing
///     <c>QuestTally</c> so the two lifecycles cannot collide over one table.
/// </summary>
public class AsyncQuestTally
{
    public Guid Id { get; set; }
    public int Members { get; set; }
    public int MonstersSlain { get; set; }

    public void Apply(MemberJoined joined) => Members++;

    public void Apply(MonsterSlain slain) => MonstersSlain++;
}

/// <summary>
///     Fails the first <c>BeforeCommitAsync</c> with a retryable exception, so the projection batch's
///     transaction is thrown away and re-executed exactly once — fisher#12's condition, injected at a
///     point inside the retried delegate and after the session flush.
/// </summary>
internal sealed class FailOnceOutbox : Fisher.Events.Messaging.IMessageOutbox
{
    private readonly FailOnceBatch _batch;

    public FailOnceOutbox() => _batch = new FailOnceBatch(this);

    public int Failures { get; private set; }

    internal void RecordFailure() => Failures++;

    public ValueTask<Fisher.Events.Messaging.IMessageBatch> CreateBatch(IDocumentSession session)
        => new(_batch);
}

internal sealed class FailOnceBatch : Fisher.Events.Messaging.IMessageBatch
{
    private readonly FailOnceOutbox _outbox;
    private bool _hasFailed;

    internal FailOnceBatch(FailOnceOutbox outbox) => _outbox = outbox;

    public ValueTask PublishAsync<T>(T message, string tenantId) => ValueTask.CompletedTask;

    public ValueTask PublishAsync<T>(T message, JasperFx.Events.MessageMetadata metadata)
        => ValueTask.CompletedTask;

    public Task BeforeCommitAsync(CancellationToken token)
    {
        if (_hasFailed)
        {
            return Task.CompletedTask;
        }

        _hasFailed = true;
        _outbox.RecordFailure();

        throw new TransientBatchFailure();
    }

    public Task AfterCommitAsync(CancellationToken token) => Task.CompletedTask;
}

internal sealed class TransientBatchFailure : Exception;

/// <summary>
///     A second async-snapshotted aggregate over the same events, for tests that need two shards
///     rather than one — fisher#102's rule is only visible when one shard can stand in for another.
/// </summary>
public class AsyncQuestRoster
{
    public Guid Id { get; set; }
    public int Members { get; set; }

    public void Apply(MemberJoined joined) => Members++;
}
