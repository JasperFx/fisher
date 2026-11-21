using Fisher.Storage;
using Fisher.Tests.Daemon;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Events;

/// <summary>
///     With extended progression tracking on, the daemon writes agent status, pause reasons and failures
///     onto <c>fi_event_progression</c> (fisher#395).
/// </summary>
/// <remarks>
///     <para>
///         The opt-in created the columns and nothing ever filled them. JasperFx's
///         <c>ExtendedProgressionWriter</c> is gated on <c>IEventStore.ExtendedProgressionEnabled</c>,
///         which Fisher left at the interface's <see langword="false" />, and behind that gate
///         <c>WriteExtendedProgressionAsync</c> was the interface's no-op. Either one alone would have
///         kept the columns empty, so both are pinned.
///     </para>
///     <para>
///         <c>a_running_daemon_never_writes_the_heartbeat_column</c> (fisher#60) is about the high-water
///         row, which the writer skips by design, and is unaffected.
///     </para>
/// </remarks>
public class extended_progression_tracking
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static DocumentStore BuildStore(TemporaryDatabase database, bool extended = true)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.EnableExtendedProgressionTracking = extended;
            options.Projections.Snapshot<PoisonTally>(SnapshotLifecycle.Async);
            options.Projections.Errors.SkipApplyErrors = false;
        });

    [Fact]
    public async Task the_store_reports_its_own_opt_in()
    {
        using var on = TemporaryDatabase.Create("extended-progression-on");
        using var off = TemporaryDatabase.Create("extended-progression-off");

        await using var tracked = BuildStore(on);
        await using var untracked = BuildStore(off, extended: false);

        ((IEventStore)tracked).ExtendedProgressionEnabled.ShouldBeTrue();
        ((IEventStore)untracked).ExtendedProgressionEnabled.ShouldBeFalse();
    }

    /// <summary>
    ///     The issue's scenario: a shard that pauses on an apply error leaves its status and reason on
    ///     its progression row, where a console polling the database can see them.
    /// </summary>
    /// <remarks>
    ///     A good event is processed first, so the shard reads as running before it pauses. A shard that
    ///     fails on its very first event, before it has committed a row, is covered by
    ///     <c>failure_before_the_first_commit</c> (fisher#660).
    /// </remarks>
    [Fact]
    public async Task a_paused_shard_records_its_status_reason_and_failure()
    {
        using var database = TemporaryDatabase.Create("extended-progression-paused");
        await using var store = BuildStore(database);
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var stream = Guid.NewGuid();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<PoisonTally>(stream, new Fisher.Tests.Daemon.Counted(1));
            await session.SaveChangesAsync(Token);
        }

        var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        try
        {
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

            // The Started transition published at floor 0 is replayed once the first commit proves
            // the row exists (jasperfx#631), so a healthy shard reads as running.
            (await WaitForRowAsync(store, row => row.AgentStatus is not null)).AgentStatus.ShouldBe("Running");

            await using (var session = store.LightweightSession())
            {
                session.Events.Append(stream, new Fisher.Tests.Daemon.Counted(PoisonTally.Poison));
                await session.SaveChangesAsync(Token);
            }

            var paused = await WaitForRowAsync(store, row => row.AgentStatus == "Paused");

            paused.PauseReason.ShouldNotBeNull();
            paused.PauseReason.ShouldContain("This event cannot be applied.");
            paused.FailureCategory.ShouldBe(nameof(ShardFailureCategory.ApplyEvent));
            paused.FailureEventSequence.ShouldBe(2);
            paused.FailureEventType.ShouldNotBeNull();

            // Telemetry only — the failing event did not move the committed progress.
            paused.LastSequence.ShouldBe(1);
        }
        finally
        {
            await daemon.StopAllAsync();
            daemon.Dispose();
        }
    }

    // ---- the write itself ----

    /// <remarks>
    ///     The jasperfx#565 rule, which Polecat and Marten share: the failure columns are written when a
    ///     state carries a failure, LEFT ALONE on a plain Stopped (which an agent publishes right behind
    ///     a Paused, and which would otherwise erase the reason as soon as it was recorded), and cleared
    ///     on a Started that carries none.
    /// </remarks>
    [Fact]
    public async Task the_failure_survives_a_stop_and_is_cleared_by_a_restart()
    {
        using var database = TemporaryDatabase.Create("extended-progression-failure");
        await using var store = BuildStore(database);
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        await SeedRowAsync(store, "PoisonTally:All", 5);

        IEventDatabase target = store.Database;

        await target.WriteExtendedProgressionAsync(new ShardState("PoisonTally:All", 5)
        {
            Action = ShardAction.Paused,
            AgentStatus = "Paused",
            PauseReason = "boom",
            Failure = new ShardFailure
            {
                Category = ShardFailureCategory.ApplyEvent,
                ExceptionType = "X",
                RootExceptionType = "X",
                Message = "boom",
                Detail = "boom",
                OccurredAt = DateTimeOffset.UtcNow,
                Event = new EventFailureDetails { Sequence = 6, EventTypeName = "counted", TenantId = "north" }
            }
        }, Token);

        await target.WriteExtendedProgressionAsync(new ShardState("PoisonTally:All", 5)
        {
            Action = ShardAction.Stopped,
            AgentStatus = "Stopped"
        }, Token);

        var stopped = await ReadRowAsync(store);
        stopped.AgentStatus.ShouldBe("Stopped");
        stopped.FailureCategory.ShouldBe("ApplyEvent");
        stopped.FailureEventSequence.ShouldBe(6);
        stopped.FailureEventType.ShouldBe("counted");
        stopped.FailureTenantId.ShouldBe("north");

        await target.WriteExtendedProgressionAsync(new ShardState("PoisonTally:All", 5)
        {
            Action = ShardAction.Started,
            AgentStatus = "Running"
        }, Token);

        var restarted = await ReadRowAsync(store);
        restarted.AgentStatus.ShouldBe("Running");
        restarted.PauseReason.ShouldBeNull();
        restarted.FailureCategory.ShouldBeNull();
        restarted.FailureEventSequence.ShouldBeNull();

        // And none of it ever touched the committed progress.
        restarted.LastSequence.ShouldBe(5);
    }

    /// <remarks>
    ///     A state without a failure is update-only: a shard with no row is a no-op, never an insert. Only a
    ///     failure creates the row (fisher#660, <c>failure_before_the_first_commit</c>).
    /// </remarks>
    [Fact]
    public async Task a_shard_with_no_row_gets_none()
    {
        using var database = TemporaryDatabase.Create("extended-progression-norow");
        await using var store = BuildStore(database);
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await ((IEventDatabase)store.Database).WriteExtendedProgressionAsync(
            new ShardState("PoisonTally:All", 0) { Action = ShardAction.Started, AgentStatus = "Running" }, Token);

        (await store.Database.AllProjectionProgress(Token)).ShouldBeEmpty();
    }

    /// <remarks>
    ///     A store without extended tracking has none of the columns, so a direct call is a no-op rather
    ///     than <c>no such column</c>.
    /// </remarks>
    [Fact]
    public async Task without_extended_tracking_the_write_is_a_no_op()
    {
        using var database = TemporaryDatabase.Create("extended-progression-disabled");
        await using var store = BuildStore(database, extended: false);
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        await SeedRowAsync(store, "PoisonTally:All", 5);

        await ((IEventDatabase)store.Database).WriteExtendedProgressionAsync(
            new ShardState("PoisonTally:All", 5) { Action = ShardAction.Started, AgentStatus = "Running" }, Token);

        // And the read leaves the status and failure alone, since there are no columns to read them from
        var state = (await store.Database.AllProjectionProgress(Token)).Single(x => x.ShardName == "PoisonTally:All");
        state.AgentStatus.ShouldBeNull();
        state.Failure.ShouldBeNull();
    }

    // ---- the read ----

    /// <remarks>
    ///     fisher#661, the read Marten's <c>AllProjectionProgress</c> already did: the status and failure columns
    ///     come back on the <see cref="ShardState" />, so a monitor polling the database (the parked identities health
    ///     check, CritterWatch while the node running the shard is down) sees why a shard stopped. Exception types are
    ///     not stored, so they read back as Marten's "Unknown"; the pause reason is the failure's message and detail,
    ///     and the heartbeat is when it happened.
    /// </remarks>
    [Fact]
    public async Task all_projection_progress_carries_the_status_and_the_failure()
    {
        using var database = TemporaryDatabase.Create("extended-progression-read");
        await using var store = BuildStore(database);
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        await SeedRowAsync(store, "PoisonTally:All", 5);

        IEventDatabase target = store.Database;
        var heartbeat = new DateTimeOffset(2026, 11, 20, 10, 30, 0, TimeSpan.Zero);

        await target.WriteExtendedProgressionAsync(new ShardState("PoisonTally:All", 5)
        {
            Action = ShardAction.Paused,
            AgentStatus = "Paused",
            PauseReason = "boom, in full",
            RunningOnNode = 3,
            LastHeartbeat = heartbeat,
            ParkedCount = 1,
            Failure = new ShardFailure
            {
                Category = ShardFailureCategory.ParkedLimitReached,
                ExceptionType = "X",
                RootExceptionType = "X",
                Message = "boom",
                Detail = "boom, in full",
                OccurredAt = heartbeat,
                Event = new EventFailureDetails { Sequence = 6, EventTypeName = "counted", TenantId = "north" }
            }
        }, Token);

        var state = (await target.AllProjectionProgress(Token)).Single(x => x.ShardName == "PoisonTally:All");

        state.Sequence.ShouldBe(5);
        state.AgentStatus.ShouldBe("Paused");
        state.PauseReason.ShouldBe("boom, in full");
        state.RunningOnNode.ShouldBe(3);
        state.LastHeartbeat.ShouldBe(heartbeat);
        state.ParkedCount.ShouldBe(1);

        var failure = state.Failure.ShouldNotBeNull();
        failure.Category.ShouldBe(ShardFailureCategory.ParkedLimitReached);
        failure.ExceptionType.ShouldBe("Unknown");
        failure.RootExceptionType.ShouldBe("Unknown");
        failure.Message.ShouldBe("boom, in full");
        failure.Detail.ShouldBe("boom, in full");
        failure.OccurredAt.ShouldBe(heartbeat);

        var failing = failure.Event.ShouldNotBeNull();
        failing.Sequence.ShouldBe(6);
        failing.EventTypeName.ShouldBe("counted");
        failing.TenantId.ShouldBe("north");

        // A restart clears the failure, and the read says so
        await target.WriteExtendedProgressionAsync(new ShardState("PoisonTally:All", 5)
        {
            Action = ShardAction.Started,
            AgentStatus = "Running"
        }, Token);

        var restarted = (await target.AllProjectionProgress(Token)).Single(x => x.ShardName == "PoisonTally:All");
        restarted.AgentStatus.ShouldBe("Running");
        restarted.PauseReason.ShouldBeNull();
        restarted.Failure.ShouldBeNull();
    }

    /// <remarks>
    ///     fisher#661 with fisher#660: a shard that hits the parking cap on its very first batch has a row with the
    ///     failure in it, and <c>AllProjectionProgress</c> returns that failure.
    /// </remarks>
    [Fact]
    public async Task all_projection_progress_carries_a_parking_cap_reached_on_the_first_batch()
    {
        using var database = TemporaryDatabase.Create("extended-progression-read-cap");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.EnableExtendedProgressionTracking = true;
            options.Projections.Snapshot<PoisonTally>(SnapshotLifecycle.Async);
            options.Projections.Errors.ApplyErrors = ProjectionErrorAction.Park;
            options.Projections.Errors.MaxParkedIdentitiesPerShard = 1;
        });
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<PoisonTally>(Guid.NewGuid(), new Fisher.Tests.Daemon.Counted(PoisonTally.Poison));
            session.Events.StartStream<PoisonTally>(Guid.NewGuid(), new Fisher.Tests.Daemon.Counted(PoisonTally.Poison));
            await session.SaveChangesAsync(Token);
        }

        var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        try
        {
            using var timeout = new CancellationTokenSource(DaemonWait.Timeout);
            ShardState? state;
            while (true)
            {
                state = (await store.Database.AllProjectionProgress(Token))
                    .SingleOrDefault(x => x.ShardName == "PoisonTally:All");
                if (state?.Failure is not null || timeout.IsCancellationRequested) break;

                await Task.Delay(50, Token);
            }

            state.ShouldNotBeNull();
            state.Sequence.ShouldBe(0);
            state.AgentStatus.ShouldBe("Paused");

            var failure = state.Failure.ShouldNotBeNull();
            failure.Category.ShouldBe(ShardFailureCategory.ParkedLimitReached);
            failure.Detail.ShouldBe(state.PauseReason);
            // The cap's failure carries one of the batch's failing events. Which one depends on the order the batch's
            // slices ran in, which is not fixed.
            failure.Event.ShouldNotBeNull().Sequence.ShouldBeOneOf(1L, 2L);
        }
        finally
        {
            await daemon.StopAllAsync();
            daemon.Dispose();
        }
    }

    private sealed record ProgressionRow(
        long LastSequence,
        string? AgentStatus,
        string? PauseReason,
        string? FailureCategory,
        long? FailureEventSequence,
        string? FailureEventType,
        string? FailureTenantId);

    private static async Task<ProgressionRow> WaitForRowAsync(DocumentStore store, Func<ProgressionRow, bool> until)
    {
        using var timeout = new CancellationTokenSource(DaemonWait.Timeout);

        while (true)
        {
            var row = await ReadRowAsync(store);
            if (until(row))
            {
                return row;
            }

            if (timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"The progression row never reached the expected state; last saw {row}.");
            }

            await Task.Delay(50, Token);
        }
    }

    private static async Task<ProgressionRow> ReadRowAsync(DocumentStore store)
    {
        await using var connection = await store.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              select last_seq_id, agent_status, pause_reason, failure_category,
                                     failure_event_sequence, failure_event_type, failure_event_tenant_id
                              from fi_event_progression where name = 'PoisonTally:All'
                              """;

        await using var reader = await command.ExecuteReaderAsync(Token);
        if (!await reader.ReadAsync(Token))
        {
            return new ProgressionRow(0, null, null, null, null, null, null);
        }

        return new ProgressionRow(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    private static async Task SeedRowAsync(DocumentStore store, string name, long sequence)
    {
        await using var connection = await store.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "insert into fi_event_progression (name, last_seq_id) values (@name, @seq)";
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@seq", sequence);
        await command.ExecuteNonQueryAsync(Token);
    }
}
