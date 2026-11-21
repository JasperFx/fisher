using Fisher.Events.Storage;
using Fisher.Internal;
using Fisher.Subscriptions;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Projections;

public record EarlyPauseWeighed(bool Poison);

public class EarlyPauseWeighIn
{
    // Static because the daemon builds the aggregate itself. Only the tests in failure_before_the_first_commit flip
    // it, and the tests of one class never run in parallel.
    public static bool PoisonFails = true;

    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Apply(EarlyPauseWeighed e)
    {
        if (e.Poison && PoisonFails) throw new InvalidOperationException("Can't weigh this one");
        Count++;
    }
}

public class EarlyFailingSubscription : SubscriptionBase
{
    public EarlyFailingSubscription()
    {
        Name = "EarlyFailing";
    }

    public bool Fails { get; set; } = true;

    public List<long> Processed { get; } = [];

    public override Task<IDaemonChangeListener> ProcessEventsAsync(EventRange page, ISubscriptionController controller,
        IDocumentSession operations, CancellationToken cancellationToken)
    {
        if (Fails) throw new InvalidOperationException("Not today");

        lock (Processed)
        {
            Processed.AddRange(page.Events.Select(x => x.Sequence));
        }

        return Task.FromResult<IDaemonChangeListener>(NullDaemonChangeListener.Instance);
    }
}

/// <summary>
///     fisher#660, the port of marten#6127: a shard that fails before its first commit has no progression row, so its
///     failure was written nowhere. It now gets a row at the position it started from, which leaves where it starts
///     next time alone.
/// </summary>
public class failure_before_the_first_commit : IAsyncLifetime
{
    private const string TheShard = "EarlyPauseWeighIn:All";

    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("failure-before-first-commit");
    private DocumentStore? _store;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public failure_before_the_first_commit()
    {
        EarlyPauseWeighIn.PoisonFails = true;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        EarlyPauseWeighIn.PoisonFails = true;

        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        _database.Dispose();
    }

    private async Task<DocumentStore> buildStoreAsync(Action<StoreOptions>? alter = null)
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.EnableExtendedProgressionTracking = true;
            options.Projections.Snapshot<EarlyPauseWeighIn>(SnapshotLifecycle.Async);
            alter?.Invoke(options);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        return _store;
    }

    private static ShardState paused(string shard, long sequence, ShardFailureCategory category)
    {
        var failure = new ShardFailure
        {
            Category = category,
            ExceptionType = "JasperFx.Events.Daemon.ParkedLimitReachedException",
            RootExceptionType = "JasperFx.Events.Daemon.ParkedLimitReachedException",
            Message = "Paused instead of parking",
            Detail = "JasperFx.Events.Daemon.ParkedLimitReachedException: Paused instead of parking",
            OccurredAt = DateTimeOffset.UtcNow,
            Event = new EventFailureDetails { Sequence = 2, EventTypeName = "early_pause_weighed" }
        };

        return new ShardState(shard, sequence)
        {
            Action = ShardAction.Paused,
            AgentStatus = "Paused",
            PauseReason = failure.Detail,
            Failure = failure,
            LastHeartbeat = DateTimeOffset.UtcNow
        };
    }

    // The first commit of a shard that starts at 0, exactly as the daemon's projection batch writes it
    private static async Task commitFromZeroAsync(DocumentStore store, long ceiling)
    {
        await using var session = (FisherSession)store.LightweightSession();
        session.QueueOperation(new ShardProgressionOperation(store.Options.EventGraph.ProgressionTableName, TheShard,
            0, ceiling, extendedTracking: true));
        await session.SaveChangesAsync(Token);
    }

    [Fact]
    public async Task a_failure_with_no_row_creates_the_row_at_the_start_position()
    {
        var store = await buildStoreAsync();

        await ((IEventDatabase)store.Database).WriteExtendedProgressionAsync(
            paused(TheShard, 0, ShardFailureCategory.ParkedLimitReached), Token);

        var row = (await ProgressionRow.ReadAsync(store, TheShard)).ShouldNotBeNull();
        row.LastSequence.ShouldBe(0);
        row.AgentStatus.ShouldBe("Paused");
        row.PauseReason.ShouldNotBeNull().ShouldContain("Paused instead of parking");
        row.FailureCategory.ShouldBe(nameof(ShardFailureCategory.ParkedLimitReached));
        row.FailureEventSequence.ShouldBe(2);
        row.FailureEventType.ShouldBe("early_pause_weighed");
    }

    [Fact]
    public async Task a_failure_with_no_row_uses_the_floor_the_shard_started_from()
    {
        var store = await buildStoreAsync();

        await ((IEventDatabase)store.Database).WriteExtendedProgressionAsync(
            paused(TheShard, 7, ShardFailureCategory.ApplyEvent), Token);

        var row = (await ProgressionRow.ReadAsync(store, TheShard)).ShouldNotBeNull();
        row.LastSequence.ShouldBe(7);
        row.FailureCategory.ShouldBe(nameof(ShardFailureCategory.ApplyEvent));
    }

    [Fact]
    public async Task a_state_without_a_failure_still_creates_no_row()
    {
        var store = await buildStoreAsync();

        await ((IEventDatabase)store.Database).WriteExtendedProgressionAsync(
            new ShardState(TheShard, 0) { Action = ShardAction.Paused, AgentStatus = "Paused", PauseReason = "asked" },
            Token);

        (await ProgressionRow.ReadAsync(store, TheShard)).ShouldBeNull();
    }

    [Fact]
    public async Task a_failure_never_moves_a_committed_position()
    {
        var store = await buildStoreAsync();
        await commitFromZeroAsync(store, 25);

        await ((IEventDatabase)store.Database).WriteExtendedProgressionAsync(
            paused(TheShard, 3, ShardFailureCategory.ApplyEvent), Token);

        var row = (await ProgressionRow.ReadAsync(store, TheShard)).ShouldNotBeNull();
        row.LastSequence.ShouldBe(25);
        row.FailureCategory.ShouldBe(nameof(ShardFailureCategory.ApplyEvent));
    }

    [Fact]
    public async Task the_first_commit_replaces_the_failure_row_at_zero()
    {
        var store = await buildStoreAsync();
        await ((IEventDatabase)store.Database).WriteExtendedProgressionAsync(
            paused(TheShard, 0, ShardFailureCategory.ApplyEvent), Token);

        await commitFromZeroAsync(store, 12);

        var row = (await ProgressionRow.ReadAsync(store, TheShard)).ShouldNotBeNull();
        row.LastSequence.ShouldBe(12);
        row.PauseReason.ShouldBeNull();
        row.FailureCategory.ShouldBeNull();
        row.FailureEventSequence.ShouldBeNull();
        row.FailureEventType.ShouldBeNull();
    }

    [Fact]
    public async Task a_first_commit_still_fails_on_a_row_another_daemon_committed()
    {
        var store = await buildStoreAsync();
        await commitFromZeroAsync(store, 12);

        await Should.ThrowAsync<Exception>(() => commitFromZeroAsync(store, 20));

        (await store.Database.ProjectionProgressFor(new ShardName("EarlyPauseWeighIn"), Token)).ShouldBe(12);
    }

    [Fact]
    public async Task a_shard_paused_at_the_parking_cap_on_its_first_batch_records_it_and_starts_from_zero_again()
    {
        var store = await buildStoreAsync(options =>
        {
            options.Projections.Errors.ApplyErrors = ProjectionErrorAction.Park;
            options.Projections.Errors.MaxParkedIdentitiesPerShard = 1;
        });

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        // Two failing identities in the shard's first batch pass a cap of one, before anything has committed
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<EarlyPauseWeighIn>(first, new EarlyPauseWeighed(true));
            session.Events.StartStream<EarlyPauseWeighIn>(second, new EarlyPauseWeighed(true));
            await session.SaveChangesAsync(Token);
        }

        using (var daemon = await store.BuildProjectionDaemonAsync())
        {
            await daemon.StartAllAsync();

            var row = await ProgressionRow.WaitForFailureAsync(store, TheShard);
            row.LastSequence.ShouldBe(0);
            row.FailureCategory.ShouldBe(nameof(ShardFailureCategory.ParkedLimitReached));

            await daemon.StopAllAsync();
        }

        // The projection is fixed. The shard starts where it would have without the row, from the beginning.
        EarlyPauseWeighIn.PoisonFails = false;

        using (var daemon = await store.BuildProjectionDaemonAsync())
        {
            await daemon.StartAllAsync();
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);
            await daemon.StopAllAsync();
        }

        await using var query = store.QuerySession();
        (await query.LoadAsync<EarlyPauseWeighIn>(first, Token)).ShouldNotBeNull().Count.ShouldBe(1);
        (await query.LoadAsync<EarlyPauseWeighIn>(second, Token)).ShouldNotBeNull().Count.ShouldBe(1);

        var progress = (await ProgressionRow.ReadAsync(store, TheShard)).ShouldNotBeNull();
        progress.LastSequence.ShouldBe(2);
        progress.FailureCategory.ShouldBeNull();
    }

    [Fact]
    public async Task a_from_present_subscription_that_fails_before_its_first_commit_starts_from_present_after_a_restart()
    {
        var subscription = new EarlyFailingSubscription();
        subscription.Options.SubscribeFromPresent();

        var store = await buildStoreAsync(options => options.Projections.Subscribe(subscription));

        // Started on an empty store, so "present" is 0 and the subscription has no row
        using (var daemon = await store.BuildProjectionDaemonAsync())
        {
            await daemon.StartAllAsync();

            await appendAsync(store, 2);

            var row = await ProgressionRow.WaitForFailureAsync(store, "EarlyFailing:All");
            row.LastSequence.ShouldBe(0);

            await daemon.StopAllAsync();
        }

        subscription.Fails = false;

        using (var daemon = await store.BuildProjectionDaemonAsync())
        {
            await daemon.StartAllAsync();

            await appendAsync(store, 1);
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

            await daemon.StopAllAsync();
        }

        // Only the event appended after the restart: it started from the present (2), not from the row at 0
        subscription.Processed.ShouldBe([3L]);
        (await ProgressionRow.ReadAsync(store, "EarlyFailing:All")).ShouldNotBeNull().LastSequence.ShouldBe(3);
    }

    [Fact]
    public async Task a_from_present_subscription_keeps_its_present_mark_when_it_fails_before_its_first_commit()
    {
        var subscription = new EarlyFailingSubscription();
        subscription.Options.SubscribeFromPresent();

        var store = await buildStoreAsync(options => options.Projections.Subscribe(subscription));

        await appendAsync(store, 3);

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        await appendAsync(store, 1);

        var row = await ProgressionRow.WaitForFailureAsync(store, "EarlyFailing:All");
        row.LastSequence.ShouldBe(3);

        await daemon.StopAllAsync();
    }

    private static async Task appendAsync(DocumentStore store, int count)
    {
        await using var session = store.LightweightSession();
        session.Events.StartStream(Guid.NewGuid(),
            Enumerable.Range(0, count).Select(_ => (object)new EarlyPauseWeighed(false)).ToArray());
        await session.SaveChangesAsync(Token);
    }

    // The row as it is stored, so these tests check what was written rather than how AllProjectionProgress reads it
    private sealed record ProgressionRow(
        long LastSequence,
        string? AgentStatus,
        string? PauseReason,
        string? FailureCategory,
        long? FailureEventSequence,
        string? FailureEventType)
    {
        public static async Task<ProgressionRow?> ReadAsync(DocumentStore store, string shard)
        {
            await using var connection = await store.Database.OpenConnectionAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                                   select last_seq_id, agent_status, pause_reason, failure_category,
                                          failure_event_sequence, failure_event_type
                                   from {store.Options.EventGraph.ProgressionTableName} where name = @name
                                   """;
            command.Parameters.AddWithValue("@name", shard);

            await using var reader = await command.ExecuteReaderAsync(Token);
            if (!await reader.ReadAsync(Token))
            {
                return null;
            }

            return new ProgressionRow(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5));
        }

        public static async Task<ProgressionRow> WaitForFailureAsync(DocumentStore store, string shard)
        {
            using var timeout = new CancellationTokenSource(DaemonWait.Timeout);

            while (true)
            {
                var row = await ReadAsync(store, shard);
                if (row?.FailureCategory is not null)
                {
                    return row;
                }

                if (timeout.IsCancellationRequested)
                {
                    throw new TimeoutException($"The failure of {shard} was never written; last saw {row}.");
                }

                await Task.Delay(50, Token);
            }
        }
    }
}
