using Fisher.Tests.Daemon;
using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Events;

/// <summary>
///     The daemon's progression write is guarded on the floor its range started from (fisher#402).
/// </summary>
/// <remarks>
///     <para>
///         The exposure is two processes each hosting a <c>Solo</c> daemon over one file. Both read the
///         same floor, both apply the range, and an unguarded <c>update … where name = ?</c> let both
///         commit. A non-idempotent projection write was applied twice with nothing to say so. The
///         ruling: unsupported, and detected, so the second batch fails rather than double-applying.
///     </para>
///     <para>
///         Two real processes racing is not a test anyone can make deterministic. What is deterministic
///         is the state the loser finds: its own last commit says the shard is at N, and the row says
///         something else because another agent moved it. So the row is moved out of band, which is
///         exactly what the other process's commit does, and the next batch has to refuse.
///     </para>
/// </remarks>
public class progression_floor_guard
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task a_batch_whose_floor_has_moved_is_refused_and_rolled_back()
    {
        using var database = TemporaryDatabase.Create("progression-floor-guard");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<PoisonTally>(SnapshotLifecycle.Async);
        });

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

            // Another process's daemon commits further along. This one still believes the shard is at 1.
            await ExecuteAsync(store, "update fi_event_progression set last_seq_id = 99 where name = 'PoisonTally:All'");

            await using (var session = store.LightweightSession())
            {
                session.Events.Append(stream, new Fisher.Tests.Daemon.Counted(1));
                await session.SaveChangesAsync(Token);
            }

            // The shard stops on ProgressionProgressOutOfOrderException, which is how JasperFx's agent
            // treats it on every store.
            using (var timeout = new CancellationTokenSource(DaemonWait.Timeout))
            {
                while (daemon.CurrentAgents().Single(x => x.Name.Identity == "PoisonTally:All").Status != AgentStatus.Stopped)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    await Task.Delay(50, Token);
                }
            }

            // The whole batch rolled back: the projection did not apply the event a second time, and the
            // progression row still says what the other process wrote.
            await using var query = store.QuerySession();
            (await query.LoadAsync<PoisonTally>(stream, Token))!.Total.ShouldBe(1);
            (await ScalarAsync(store, "select last_seq_id from fi_event_progression where name = 'PoisonTally:All'"))
                .ShouldBe(99L);
        }
        finally
        {
            await daemon.StopAllAsync();
            daemon.Dispose();
        }
    }

    /// <remarks>
    ///     The guard must not cost the ordinary paths. A shard with no row yet is inserted whatever its
    ///     floor, which a shard starting from the present needs. A shard advancing from its own last
    ///     commit is updated. Both are covered by the whole daemon suite running green. This pins the
    ///     two cases next to the refusal so a change to the statement fails here first.
    /// </remarks>
    [Fact]
    public async Task an_ordinary_catch_up_still_records_progress()
    {
        using var database = TemporaryDatabase.Create("progression-floor-guard-ok");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<PoisonTally>(SnapshotLifecycle.Async);
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var stream = Guid.NewGuid();
        var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        try
        {
            for (var i = 0; i < 3; i++)
            {
                await using (var session = store.LightweightSession())
                {
                    session.Events.Append(stream, new Fisher.Tests.Daemon.Counted(1));
                    await session.SaveChangesAsync(Token);
                }

                await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);
            }

            await using var query = store.QuerySession();
            (await query.LoadAsync<PoisonTally>(stream, Token))!.Total.ShouldBe(3);
            (await ScalarAsync(store, "select last_seq_id from fi_event_progression where name = 'PoisonTally:All'"))
                .ShouldBe(3L);
        }
        finally
        {
            await daemon.StopAllAsync();
            daemon.Dispose();
        }
    }

    private static async Task ExecuteAsync(DocumentStore store, string sql)
    {
        await using var connection = await store.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task<object?> ScalarAsync(DocumentStore store, string sql)
    {
        await using var connection = await store.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Token);
    }
}
