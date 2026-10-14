using System.Collections.Concurrent;
using Fisher.Linq;
using Fisher.Projections;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Events;

public record RestartableIncrement(int Index);

public class RestartableTally
{
    public Guid Id { get; set; }
    public int Count { get; set; }
}

/// <summary>
///     Throws while folding the event with index <see cref="FailOnIndex" />, for as long as <see cref="Failing" />
///     is on, and counts every successful application per event sequence so a replay over documents that are
///     already built shows up.
/// </summary>
/// <remarks>
///     The state is static and belongs to <see cref="rebuild_mode_start_resumes_after_a_failure" /> alone, which has
///     one fact.
/// </remarks>
public partial class RestartableTallyProjection : SingleStreamProjection<RestartableTally, Guid>
{
    public static volatile bool Failing;
    public static int FailOnIndex;
    public static readonly ConcurrentDictionary<long, int> Applications = new();

    public RestartableTallyProjection()
    {
        Name = "RestartableTally";

        // Small pages, so several of them commit before the failing one.
        Options.BatchSize = 10;
    }

    public void Apply(IEvent<RestartableIncrement> e, RestartableTally tally)
    {
        if (Failing && e.Data.Index == FailOnIndex)
        {
            throw new DivideByZeroException($"Boom on event {e.Data.Index}");
        }

        tally.Count++;
        Applications.AddOrUpdate(e.Sequence, 1, (_, count) => count + 1);
    }
}

/// <summary>
///     fisher#441 (JasperFx/jasperfx#1015, wolverine#4569): a projection shard started in Rebuild mode with
///     <see cref="IProjectionDaemon.StartAgentInRebuildModeAsync" /> that fails part of the way through can be
///     stopped and started again, and the second run finishes from the progress the first run committed, instead
///     of replaying from the beginning over the documents the first run already built.
/// </summary>
/// <remarks>
///     <para>
///         The store-local twin of Marten's test of the same name. Extended progression tracking is on, for the
///         failure columns, and there is no other schema change.
///     </para>
///     <para>
///         Before jasperfx#1015, every position strategy answered Rebuild with a floor of 0 and a rewind of the
///         progression row. Nothing tears the documents down, so the second start folded every event again on
///         top of them, and every committed stream ended at a <c>Count</c> of 2.
///     </para>
/// </remarks>
public class rebuild_mode_start_resumes_after_a_failure
{
    private const int NumberOfEvents = 100;
    private static readonly ShardName TheShard = new("RestartableTally");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public rebuild_mode_start_resumes_after_a_failure()
    {
        RestartableTallyProjection.Applications.Clear();
        RestartableTallyProjection.Failing = false;
        RestartableTallyProjection.FailOnIndex = 60;
    }

    private static DocumentStore BuildStore(TemporaryDatabase database)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.EnableExtendedProgressionTracking = true;
            options.Projections.Add(new RestartableTallyProjection(), ProjectionLifecycle.Async);
        });

    [Fact]
    public async Task a_failed_rebuild_mode_run_can_be_restarted_and_finished_from_its_committed_progress()
    {
        using var database = TemporaryDatabase.Create("rebuild-mode-resume");
        await using var store = BuildStore(database);
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await AppendAsync(store, 1, NumberOfEvents);
        var highest = await HighestSequenceAsync(store);
        highest.ShouldBe(NumberOfEvents);

        var daemon = await store.BuildProjectionDaemonAsync();

        try
        {
            // First run: fails on event 60.
            RestartableTallyProjection.Failing = true;
            var paused = daemon.Tracker.WaitForShardCondition(
                state => state.ShardName == TheShard.Identity && state.Action == ShardAction.Paused,
                $"{TheShard.Identity} is paused", DaemonWait.Timeout);

            var first = await daemon.StartAgentInRebuildModeAsync(TheShard, Token);
            first.Mode.ShouldBe(ShardExecutionMode.Rebuild);

            await paused;

            // The failure columns are written by the progression writer on its own flush, a moment after the
            // pause, so they are polled for rather than read once.
            var afterFailure = await WaitForRowAsync(store, row => row.FailureCategory is not null,
                "extended progression tracking records the failure");

            TestContext.Current.TestOutputHelper?.WriteLine(
                $"First run committed {afterFailure.LastSequence} of {highest}; failure at {afterFailure.FailureEventSequence} ({afterFailure.FailureCategory})");

            afterFailure.LastSequence.ShouldBeGreaterThan(0);
            afterFailure.LastSequence.ShouldBeLessThan(highest);
            afterFailure.FailureCategory.ShouldNotBeNull();
            afterFailure.FailureEventSequence.ShouldNotBeNull();

            // Every event at or below the committed mark is already in a document.
            for (var sequence = 1L; sequence <= afterFailure.LastSequence; sequence++)
            {
                RestartableTallyProjection.Applications.ShouldContainKey(sequence);
            }

            // Stop, clear the fault, and start the Rebuild-mode run again.
            await daemon.StopAgentAsync(TheShard.Identity);
            RestartableTallyProjection.Failing = false;

            var second = await daemon.StartAgentInRebuildModeAsync(TheShard, Token);
            second.Mode.ShouldBe(ShardExecutionMode.Rebuild);

            await WaitForRowAsync(store, row => row.LastSequence >= highest,
                $"the shard reaches the high-water mark {highest}");

            // Finished: every event folded into its document exactly once. A replay from the floor over the
            // documents the first run built would leave every committed stream with a Count of 2.
            await using (var session = store.QuerySession())
            {
                var tallies = await session.Query<RestartableTally>().ToListAsync(Token);
                tallies.Count.ShouldBe(NumberOfEvents);

                var folds = tallies.GroupBy(x => x.Count).OrderBy(x => x.Key)
                    .Select(x => $"{x.Count()} with Count == {x.Key}");
                tallies.ShouldAllBe(x => x.Count == 1, $"documents: {string.Join(", ", folds)}");
            }

            // No event at or below the first run's committed mark was folded again by the second run.
            for (var sequence = 1L; sequence <= afterFailure.LastSequence; sequence++)
            {
                RestartableTallyProjection.Applications[sequence].ShouldBe(1, $"event at sequence {sequence}");
            }

            // The second run started with no failure, so the failure columns are cleared. Polled for the same
            // reason as above.
            var afterRestart = await WaitForRowAsync(store, row => row.FailureCategory is null,
                "the restarted shard clears the failure columns");
            afterRestart.FailureEventSequence.ShouldBeNull();

            // And the restarted run keeps following the store past where the rebuild started.
            await AppendAsync(store, NumberOfEvents + 1, 5);
            var newHighest = await HighestSequenceAsync(store);
            newHighest.ShouldBe(NumberOfEvents + 5);

            await WaitForRowAsync(store, row => row.LastSequence >= newHighest,
                $"the shard follows the store to {newHighest}");

            daemon.CurrentAgents().Single(x => x.Name.Identity == TheShard.Identity)
                .Status.ShouldBe(AgentStatus.Running);
        }
        finally
        {
            await daemon.StopAllAsync();
            daemon.Dispose();
        }
    }

    // One event per stream: a document that was folded twice has a Count of 2.
    private static async Task AppendAsync(DocumentStore store, int from, int count)
    {
        await using var session = store.LightweightSession();
        for (var index = from; index < from + count; index++)
        {
            session.Events.StartStream<RestartableTally>(Guid.NewGuid(), new RestartableIncrement(index));
        }

        await session.SaveChangesAsync(Token);
    }

    private static async Task<long> HighestSequenceAsync(DocumentStore store)
    {
        await using var connection = await store.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "select coalesce(max(seq_id), 0) from fi_events";
        return Convert.ToInt64(await command.ExecuteScalarAsync(Token));
    }

    private sealed record ProgressionRow(
        long LastSequence,
        string? AgentStatus,
        string? FailureCategory,
        long? FailureEventSequence);

    private static async Task<ProgressionRow> WaitForRowAsync(DocumentStore store, Func<ProgressionRow, bool> until,
        string description)
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
                throw new TimeoutException($"Timed out waiting until {description}; last saw {row}.");
            }

            await Task.Delay(50, Token);
        }
    }

    private static async Task<ProgressionRow> ReadRowAsync(DocumentStore store)
    {
        await using var connection = await store.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              select last_seq_id, agent_status, failure_category, failure_event_sequence
                              from fi_event_progression where name = @name
                              """;
        command.Parameters.AddWithValue("@name", TheShard.Identity);

        await using var reader = await command.ExecuteReaderAsync(Token);
        if (!await reader.ReadAsync(Token))
        {
            return new ProgressionRow(0, null, null, null);
        }

        return new ProgressionRow(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3));
    }
}
