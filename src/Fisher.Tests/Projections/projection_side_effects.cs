using Fisher.Events.Messaging;
using Fisher.Tests.Events;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Projections;

/// <summary>
///     Projection side effects — fisher#4.
/// </summary>
/// <remarks>
///     Fisher has no message bus, so what it supplies is the seam: an <see cref="IMessageOutbox" /> a
///     bus integration replaces, and the two commit hooks that let it choose a delivery guarantee.
///     These tests stand in for that integration with a recording outbox, because the property under
///     test is <em>when</em> the hooks fire relative to the commit, which is Fisher's half of the
///     contract.
/// </remarks>
public class projection_side_effects : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("side-effects");
    private readonly RecordingOutbox _outbox = new();
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.MessageOutbox = _outbox;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task the_default_outbox_drops_a_message_rather_than_throwing()
    {
        await using var plain = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.DatabaseSchemaName = "nullobox";
        });

        await plain.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using var session = plain.LightweightSession();
        var sink = await ((JasperFx.Events.IStorageOperations)session).GetOrStartMessageSink();

        // The point is that this does not throw. Before the seam existed it did, which made any
        // projection that merely might publish untestable without a bus.
        await Should.NotThrowAsync(async () => await sink.PublishAsync(new QuestFinished("done"), "*DEFAULT*"));
    }

    [Fact]
    public async Task a_message_published_from_a_session_reaches_the_outbox()
    {
        await using var session = _store.LightweightSession();
        var sink = await ((JasperFx.Events.IStorageOperations)session).GetOrStartMessageSink();
        await sink.PublishAsync(new QuestFinished("ring destroyed"), StorageConstants.DefaultTenantId);

        session.Events.StartStream(Guid.NewGuid(), new QuestStarted("Destroy the ring"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        _outbox.Batch.ShouldNotBeNull();
        _outbox.Batch!.Published.ShouldHaveSingleItem().ShouldBeOfType<QuestFinished>()
            .Name.ShouldBe("ring destroyed");
    }

    /// <summary>
    ///     The hooks bracket the commit, in order.
    /// </summary>
    /// <remarks>
    ///     That ordering is the whole contract: an outbox persisting rows in <c>BeforeCommit</c> gets
    ///     atomicity with the write, and one publishing to a broker in <c>AfterCommit</c> gets the
    ///     guarantee that the write actually landed. Recording the sequence is the only way to hold it.
    /// </remarks>
    [Fact]
    public async Task both_commit_hooks_fire_in_order()
    {
        await using var session = _store.LightweightSession();
        var sink = await ((JasperFx.Events.IStorageOperations)session).GetOrStartMessageSink();
        await sink.PublishAsync(new QuestFinished("done"), StorageConstants.DefaultTenantId);

        session.Events.StartStream(Guid.NewGuid(), new QuestStarted("Destroy the ring"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        _outbox.Batch!.Hooks.ShouldBe(["before", "after"]);
    }

    /// <summary>
    ///     <c>BeforeCommit</c> really is inside the transaction and <c>AfterCommit</c> really is
    ///     outside it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Recording the hook order alone does not prove this — the two would fire in that order
    ///         even if both ran before the commit, or both after. What distinguishes them is what the
    ///         rest of the database can see at the moment each runs, so each hook probes the committed
    ///         state over a <em>separate</em> connection: the write is invisible in <c>BeforeCommit</c>
    ///         and visible in <c>AfterCommit</c>.
    ///     </para>
    ///     <para>
    ///         That probe is what an outbox's two delivery guarantees actually rest on, and moving
    ///         <c>AfterCommitAsync</c> to before the commit fails this test — which the hook-order test
    ///         alone did not.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task the_before_hook_runs_inside_the_transaction_and_the_after_hook_outside_it()
    {
        _outbox.Probe = CountCommittedEventsAsync;

        await using var session = _store.LightweightSession();
        var sink = await ((JasperFx.Events.IStorageOperations)session).GetOrStartMessageSink();
        await sink.PublishAsync(new QuestFinished("done"), StorageConstants.DefaultTenantId);

        session.Events.StartStream(Guid.NewGuid(), new QuestStarted("Destroy the ring"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        _outbox.Batch!.VisibleAtBeforeCommit.ShouldBe(0);
        _outbox.Batch.VisibleAtAfterCommit.ShouldBe(1);
    }

    /// <summary>
    ///     A unit of work that fails before the hooks publishes nothing at all.
    /// </summary>
    /// <remarks>
    ///     A stream-id collision is caught by the append itself, well before either hook — so neither
    ///     runs, and the messages the session buffered go nowhere. Asserted as "no hooks fired" rather
    ///     than "no after-commit hook fired", because the weaker claim would pass even if the failure
    ///     had never reached the hook boundary at all.
    /// </remarks>
    [Fact]
    public async Task a_unit_of_work_that_fails_before_the_hooks_publishes_nothing()
    {
        var streamId = Guid.NewGuid();

        await using (var seed = _store.LightweightSession())
        {
            seed.Events.StartStream(streamId, new QuestStarted("First"));
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var session = _store.LightweightSession();
        var sink = await ((JasperFx.Events.IStorageOperations)session).GetOrStartMessageSink();
        await sink.PublishAsync(new QuestFinished("never sent"), StorageConstants.DefaultTenantId);

        session.Events.StartStream(streamId, new QuestStarted("Second"));

        await Should.ThrowAsync<Fisher.Exceptions.ExistingStreamIdCollisionException>(async () =>
            await session.SaveChangesAsync(TestContext.Current.CancellationToken));

        _outbox.Batch!.Hooks.ShouldBeEmpty();
    }

    /// <summary>
    ///     How many events are committed, read over a connection of its own.
    /// </summary>
    /// <remarks>
    ///     A separate connection is the whole point: the session's own would see its uncommitted
    ///     writes. WAL is what keeps this from blocking behind the open write transaction.
    /// </remarks>
    private async Task<long> CountCommittedEventsAsync()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from fi_events";

        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task a_session_that_never_publishes_never_asks_the_outbox_for_a_batch()
    {
        await using var session = _store.LightweightSession();
        session.Events.StartStream(Guid.NewGuid(), new QuestStarted("Quiet"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        _outbox.Batch.ShouldBeNull();
    }

    /// <summary>
    ///     An async projection's side effect reaches the outbox through the daemon's batch.
    /// </summary>
    /// <remarks>
    ///     The daemon's path is a different one from the session's — <c>FisherProjectionBatch</c> owns
    ///     its own buffer and fires its own hooks around its own transaction — so it gets its own
    ///     coverage, including the same visibility probe rather than only the hook order.
    /// </remarks>
    [Fact]
    public async Task an_async_projection_publishes_through_the_daemons_batch()
    {
        await using var database = TemporaryDatabase.Create("side-effects-async");

        var outbox = new RecordingOutbox
        {
            // The projection's own snapshot, which the batch writes in the transaction the hooks
            // bracket. Invisible over another connection until that transaction commits.
            Probe = async () =>
            {
                await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(database.ConnectionString);
                await connection.OpenAsync(TestContext.Current.CancellationToken);

                await using var command = connection.CreateCommand();
                command.CommandText = "select count(*) from fi_doc_announcedquest";

                return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }
        };

        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.MessageOutbox = outbox;
            options.Projections.Add(new AnnouncingProjection(), ProjectionLifecycle.Async);
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

            outbox.Batch.ShouldNotBeNull();
            outbox.Batch!.Published.ShouldContain(x => x is QuestFinished);
            outbox.Batch.Hooks.ShouldBe(["before", "after"]);

            outbox.Batch.VisibleAtBeforeCommit.ShouldBe(0);
            outbox.Batch.VisibleAtAfterCommit.ShouldBe(1);
        }
        finally
        {
            await daemon.StopAllAsync();
            daemon.Dispose();
        }
    }
}

/// <summary>
///     Stands in for a bus integration: records what was published and when each hook fired.
/// </summary>
internal sealed class RecordingOutbox : IMessageOutbox
{
    public RecordingBatch? Batch { get; private set; }

    /// <summary>
    ///     Optional read of the committed state, run at each hook so a test can tell which side of the
    ///     commit that hook is on.
    /// </summary>
    public Func<Task<long>>? Probe { get; set; }

    public ValueTask<IMessageBatch> CreateBatch(IDocumentSession session)
    {
        Batch ??= new RecordingBatch(Probe);
        return new ValueTask<IMessageBatch>(Batch);
    }
}

internal sealed class RecordingBatch : IMessageBatch
{
    private readonly List<object> _published = [];
    private readonly List<string> _hooks = [];
    private readonly Func<Task<long>>? _probe;

    internal RecordingBatch(Func<Task<long>>? probe) => _probe = probe;

    public long? VisibleAtBeforeCommit { get; private set; }

    public long? VisibleAtAfterCommit { get; private set; }

    public IReadOnlyList<object> Published
    {
        get
        {
            lock (_published)
            {
                return _published.ToArray();
            }
        }
    }

    public IReadOnlyList<string> Hooks
    {
        get
        {
            lock (_hooks)
            {
                return _hooks.ToArray();
            }
        }
    }

    public ValueTask PublishAsync<T>(T message, string tenantId)
    {
        lock (_published)
        {
            _published.Add(message!);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask PublishAsync<T>(T message, MessageMetadata metadata) => PublishAsync(message, metadata.TenantId);

    public async Task BeforeCommitAsync(CancellationToken token)
    {
        if (_probe is not null)
        {
            VisibleAtBeforeCommit = await _probe();
        }

        lock (_hooks)
        {
            _hooks.Add("before");
        }
    }

    public async Task AfterCommitAsync(CancellationToken token)
    {
        if (_probe is not null)
        {
            VisibleAtAfterCommit = await _probe();
        }

        lock (_hooks)
        {
            _hooks.Add("after");
        }
    }
}

public record QuestFinished(string Name);

/// <summary>
///     A snapshot that also announces itself.
/// </summary>
/// <remarks>
///     Side effects are raised from the aggregation pipeline's <c>RaiseSideEffects</c> hook against the
///     event slice, not from a projection method's session — that is JasperFx's shape, and the daemon's
///     <c>AggregationRunner</c> is what drains <c>slice.PublishedMessages</c> into
///     <c>IProjectionBatch.PublishMessageAsync</c>.
/// </remarks>
public class AnnouncedQuest
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public void Apply(QuestStarted started) => Name = started.Name;
}

public class AnnouncingProjection : Fisher.Projections.SingleStreamProjection<AnnouncedQuest, Guid>
{
    public override ValueTask RaiseSideEffects(IDocumentSession operations, IEventSlice<AnnouncedQuest> slice)
    {
        slice.PublishMessage(new QuestFinished(slice.Snapshot?.Name ?? "unknown"));
        return ValueTask.CompletedTask;
    }
}
