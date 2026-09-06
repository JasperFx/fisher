using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fisher.Tests.Events;

public record WardOpened(string Name);

public class WardRoster
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public void Apply(WardOpened e) => Name = e.Name;
}

/// <summary>
///     fisher#138 — what is left of it here after
///     <c>ProjectionCoordinatorCompliance</c> took the reachability half: the refusal message, and
///     <c>Advanced.ResetAllDataAsync</c> not stranding a running daemon.
/// </summary>
/// <remarks>
///     <para>
///         Before fisher#138, <c>AddAsyncDaemon</c> registered only <c>IHostedService</c> over an
///         internal class that implemented nothing else, so <b>both</b> documented routes failed: the
///         service was not registered under any reachable interface, and the
///         <c>GetServices&lt;IHostedService&gt;().OfType&lt;IProjectionCoordinator&gt;()</c> fallback
///         found nothing either. Marten and Polecat have registered JasperFx's interface since
///         jasperfx#430, so store-agnostic code could do daemon operations against them and not here.
///     </para>
///     <para>
///         <b>That gap is now a shared suite rather than a local test.</b> jasperfx#732 was filed
///         because of it — no suite could see it, since every other daemon suite drives a daemon the
///         fixture built by hand, and Fisher passed all 37 while the registration was broken. The four
///         facts that covered it here are retired in favour of
///         <c>ProjectionCoordinatorCompliance</c>; see the note below.
///     </para>
///     <para>
///         Fisher registers <em>JasperFx's</em> interface rather than a Fisher-local sub-interface. Both
///         siblings have a local one that adds no members; theirs are historical, and a store-agnostic
///         consumer resolves the shared one anyway.
///     </para>
/// </remarks>
public class projection_coordinator : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("projection-coordinator");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();

        return ValueTask.CompletedTask;
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private Task<IHost> StartedHost()
        => Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddFisher(options =>
                {
                    options.ConnectionString = _database.ConnectionString;
                    options.AutoCreateSchemaObjects = AutoCreate.All;
                    options.Projections.Snapshot<WardRoster>(SnapshotLifecycle.Async);
                })
                .ApplyAllDatabaseChangesOnStartup()
                .AddAsyncDaemon())
            .StartAsync(Token);

    private async Task<Guid> OpenWardAsync(IHost host, string name)
    {
        var id = Guid.NewGuid();

        await using var session = host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Events.StartStream<WardRoster>(id, new WardOpened(name));
        await session.SaveChangesAsync(Token);

        return id;
    }

    /*
     * Four facts retired here in fisher#184, superseded verbatim by ProjectionCoordinatorCompliance
     * (jasperfx#732) — which exists BECAUSE of fisher#138, and is the shared version of this file:
     *
     *   the_coordinator_is_resolvable_from_the_container          -> same name upstream
     *   the_hosted_service_and_the_resolved_coordinator_are_one_instance -> same name upstream
     *   the_coordinator_hands_back_the_running_daemon             -> the_main_database_daemon_is_
     *                                                               reachable_and_projecting, plus
     *                                                               all_daemons_includes_the_main_
     *                                                               database_daemon
     *   pause_then_resume_leaves_the_daemon_projecting            -> same name upstream, and
     *                                                               STRONGER: it appends and waits
     *                                                               BEFORE the pause, so a
     *                                                               post-resume timeout indicts
     *                                                               resume rather than startup
     *
     * The suite drives a host built the documented way (AddFisher + AddAsyncDaemon), which is the
     * whole point of it and exactly what these four did. Keeping local copies of facts three stores
     * are now held to would make a divergence look like a Fisher decision.
     *
     * What stays below is Fisher's alone: the refusal message, and ResetAllDataAsync's daemon
     * handling, which is a deliberate divergence from Marten (see CLAUDE.md, "The async daemon").
     */

    [Fact]
    public async Task an_unknown_database_is_refused_by_name()
    {
        using var host = await StartedHost();
        var coordinator = host.Services.GetRequiredService<IProjectionCoordinator>();

        var ex = await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await coordinator.DaemonForDatabase("no-such-database"));

        ex.Message.ShouldContain("no-such-database");

        await host.StopAsync(Token);
    }

    /// <summary>
    ///     <b>The failure fisher#138 was reported for.</b> A wipe deletes <c>fi_event_progression</c> out
    ///     from under a running daemon whose agents hold their positions in memory, so they carry on from
    ///     where they were and record nothing against an event store that now starts at zero. Every later
    ///     wait then times out saying shards have recorded no progress — silent until something waits,
    ///     which in the reporting case was a spec suite resetting between scenarios.
    /// </summary>
    /// <remarks>
    ///     Two rounds, and the second is the assertion: the first establishes real in-memory positions to
    ///     be stranded, and a single-round test would pass against the old behaviour.
    /// </remarks>
    [Fact]
    public async Task a_reset_leaves_the_running_daemon_able_to_project_again()
    {
        using var host = await StartedHost();
        var store = host.Services.GetRequiredService<IDocumentStore>();

        var before = await OpenWardAsync(host, "Ward C");
        await store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));

        await store.Advanced.ResetAllDataAsync(Token);

        await using (var query = store.LightweightSession())
        {
            (await query.LoadAsync<WardRoster>(before, Token)).ShouldBeNull();
        }

        var after = await OpenWardAsync(host, "Ward D");
        await store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));

        await using (var query = store.LightweightSession())
        {
            (await query.LoadAsync<WardRoster>(after, Token)).ShouldNotBeNull().Name.ShouldBe("Ward D");
        }

        await host.StopAsync(Token);
    }

    /// <summary>
    ///     A store nobody is hosting a daemon for still resets. The pause is conditional on a daemon this
    ///     process is actually running, which is the only one the store can know about — an externally
    ///     managed daemon, or one in another process, keeps the hazard above because nothing here can
    ///     reach it.
    /// </summary>
    [Fact]
    public async Task a_reset_without_a_hosted_daemon_still_works()
    {
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(Guid.NewGuid(), new WardOpened("Ward E"));
            await session.SaveChangesAsync(Token);
        }

        await store.Advanced.ResetAllDataAsync(Token);

        await using var query = store.LightweightSession();
        (await query.Events.QueryEventsAsync(new JasperFx.Events.EventQuery(), Token))
            .Events.ShouldBeEmpty();
    }
}
