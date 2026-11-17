using Fisher.Tests.Events;
using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fisher.Tests.Projections;

public class RebuiltRoster
{
    public Guid Id { get; set; }
    public int Members { get; set; }

    public void Apply(MemberJoined joined) => Members++;
}

/// <summary>
///     fisher#629: <c>Advanced.RebuildSingleStreamAsync</c> for a document an asynchronous single-stream projection
///     writes. It goes through the daemon this process hosts when that daemon runs the shard, refuses when the
///     shard is live somewhere the store cannot reach, and otherwise rebuilds offline as it always has.
/// </summary>
/// <remarks>
///     The inline cases stay in <c>advanced_daemon_and_tenant_operations</c>: an inline projection has no shard,
///     so it always takes the offline path.
/// </remarks>
public class rebuilding_one_stream_beside_a_daemon : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("rebuild-one-stream");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private void configure(StoreOptions options)
    {
        options.ConnectionString = _database.ConnectionString;
        options.AutoCreateSchemaObjects = AutoCreate.All;
        options.Projections.Snapshot<RebuiltRoster>(SnapshotLifecycle.Async);
    }

    private static async Task<Guid> aRosterAsync(IDocumentStore store, int members)
    {
        var id = Guid.NewGuid();

        await using var session = store.LightweightSession();
        session.Events.StartStream<RebuiltRoster>(id,
            Enumerable.Range(0, members).Select(x => (object)new MemberJoined($"member {x}")).ToArray());
        await session.SaveChangesAsync(Token);

        return id;
    }

    // The read model gone wrong, which is the state the rebuild exists to repair
    private static async Task corruptAsync(IDocumentStore store, Guid id)
    {
        await using var session = store.LightweightSession();
        session.Store(new RebuiltRoster { Id = id, Members = 99 });
        await session.SaveChangesAsync(Token);
    }

    private static async Task<int?> membersAsync(IDocumentStore store, Guid id)
    {
        await using var query = store.QuerySession();
        return (await query.LoadAsync<RebuiltRoster>(id, Token))?.Members;
    }

    /// <remarks>
    ///     The progression row was written moments ago, so the offline path would have been refused as live
    ///     elsewhere. That it succeeds is what shows it went through the hosted daemon.
    /// </remarks>
    [Fact]
    public async Task a_daemon_this_process_hosts_rebuilds_the_stream_inside_its_shard()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddFisher(configure)
                .ApplyAllDatabaseChangesOnStartup()
                .AddAsyncDaemon())
            .StartAsync(Token);

        var store = host.Services.GetRequiredService<IDocumentStore>();
        var database = ((DocumentStore)store).Database;

        var id = await aRosterAsync(store, 3);
        await database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

        var shard = store.Advanced.AllAsyncProjectionShardNames().Single();
        var position = await database.ProjectionProgressFor(shard, Token);

        await corruptAsync(store, id);

        await store.Advanced.RebuildSingleStreamAsync<RebuiltRoster>(id, token: Token);

        (await membersAsync(store, id)).ShouldBe(3);

        // Bound to an empty (C, C] batch on the idle shard, which records C again
        (await database.ProjectionProgressFor(shard, Token)).ShouldBe(position);

        await host.StopAsync(Token);
    }

    [Fact]
    public async Task a_daemon_the_store_does_not_host_makes_the_rebuild_refuse()
    {
        await using var store = DocumentStore.For(configure);
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var id = await aRosterAsync(store, 2);

        // Built by hand, so the store cannot reach it: it might as well be in another process
        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();
        await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

        await corruptAsync(store, id);

        var refused = await Should.ThrowAsync<IdentityRebuildRefusedException>(() =>
            store.Advanced.RebuildSingleStreamAsync<RebuiltRoster>(id, token: Token));

        refused.Reason.ShouldBe(IdentityRebuildRefusal.ShardLiveOnAnotherNode);
        (await membersAsync(store, id)).ShouldBe(99);

        // The daemon running the shard is where to ask
        var shard = store.Advanced.AllAsyncProjectionShardNames().Single();
        await daemon.RebuildIdentityAsync(shard.Name, null, id, Token);

        (await membersAsync(store, id)).ShouldBe(2);

        await daemon.StopAllAsync();
    }

    [Fact]
    public async Task with_no_daemon_running_the_projection_the_stream_is_rebuilt_offline()
    {
        await using var store = DocumentStore.For(configure);
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var id = await aRosterAsync(store, 4);
        await corruptAsync(store, id);

        await store.Advanced.RebuildSingleStreamAsync<RebuiltRoster>(id, token: Token);

        (await membersAsync(store, id)).ShouldBe(4);
    }

    /// <remarks>
    ///     With extended progression tracking a stopped agent writes its status, so the row says it is not
    ///     running without waiting out the live window.
    /// </remarks>
    [Fact]
    public async Task a_stopped_daemon_with_extended_progression_does_not_refuse()
    {
        await using var store = DocumentStore.For(options =>
        {
            configure(options);
            options.Events.EnableExtendedProgressionTracking = true;
        });
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var id = await aRosterAsync(store, 2);
        var shard = store.Advanced.AllAsyncProjectionShardNames().Single();

        using (var daemon = await store.BuildProjectionDaemonAsync())
        {
            await daemon.StartAllAsync();
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);
            await daemon.StopAllAsync();

            // Wait for the Stopped status until JasperFx/jasperfx#1225 ships; remove after 3.6.1.
            // On JasperFx 3.6.0 the Stopped write is usually lost outright rather than late, so after the wait
            // the test writes the state the stopped agent published itself (SubscriptionAgent.StopAndDrainAsync)
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while ((await store.Database.ReadShardLivenessAsync(shard.Identity, Token))?.AgentStatus != "Stopped"
                   && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(25, Token);
            }

            var row = (await store.Database.ReadShardLivenessAsync(shard.Identity, Token)).ShouldNotBeNull();
            if (row.AgentStatus != "Stopped")
            {
                await store.Database.WriteExtendedProgressionAsync(new ShardState(shard, row.Sequence)
                {
                    Action = ShardAction.Stopped,
                    AgentStatus = "Stopped",
                    LastHeartbeat = DateTimeOffset.UtcNow
                }, Token);
            }

            (await store.Database.ReadShardLivenessAsync(shard.Identity, Token))!.AgentStatus.ShouldBe("Stopped");
        }

        await corruptAsync(store, id);

        await store.Advanced.RebuildSingleStreamAsync<RebuiltRoster>(id, token: Token);

        (await membersAsync(store, id)).ShouldBe(2);
    }
}
