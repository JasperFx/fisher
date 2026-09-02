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
///     fisher#138 — the running daemon is reachable from application code as an
///     <see cref="IProjectionCoordinator" />, and <c>Advanced.ResetAllDataAsync</c> no longer strands it.
/// </summary>
/// <remarks>
///     <para>
///         Before this, <c>AddAsyncDaemon</c> registered only <c>IHostedService</c> over an internal
///         class that implemented nothing else, so <b>both</b> documented routes failed: the service was
///         not registered under any reachable interface, and the
///         <c>GetServices&lt;IHostedService&gt;().OfType&lt;IProjectionCoordinator&gt;()</c> fallback
///         found nothing either. Marten and Polecat have registered JasperFx's interface since
///         jasperfx#430, so store-agnostic code could do daemon operations against them and not here.
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

    [Fact]
    public async Task the_coordinator_is_resolvable_from_the_container()
    {
        using var host = await StartedHost();

        host.Services.GetRequiredService<IProjectionCoordinator>().ShouldNotBeNull();

        await host.StopAsync(Token);
    }

    /// <summary>
    ///     The documented fallback for a host that does not register the interface — walking the hosted
    ///     services and casting. It found nothing before, because the class implemented no such
    ///     interface; now it finds the same instance the container hands out, which is the property that
    ///     matters. Two registrations of one coordinator would be two daemons over one file, which on
    ///     SQLite is two writers contending for the single write lock.
    /// </summary>
    [Fact]
    public async Task the_hosted_service_and_the_resolved_coordinator_are_one_instance()
    {
        using var host = await StartedHost();

        var resolved = host.Services.GetRequiredService<IProjectionCoordinator>();
        var walked = host.Services.GetServices<IHostedService>()
            .OfType<IProjectionCoordinator>()
            .ShouldHaveSingleItem();

        walked.ShouldBeSameAs(resolved);

        await host.StopAsync(Token);
    }

    [Fact]
    public async Task the_coordinator_hands_back_the_running_daemon()
    {
        using var host = await StartedHost();
        var coordinator = host.Services.GetRequiredService<IProjectionCoordinator>();

        var id = await OpenWardAsync(host, "Ward A");

        var daemon = coordinator.DaemonForMainDatabase();
        await daemon.WaitForNonStaleData(TimeSpan.FromSeconds(30));

        await using var query = host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        (await query.LoadAsync<WardRoster>(id, Token)).ShouldNotBeNull().Name.ShouldBe("Ward A");

        (await coordinator.AllDaemonsAsync()).ShouldHaveSingleItem().ShouldBeSameAs(daemon);

        await host.StopAsync(Token);
    }

    /// <summary>
    ///     Pause stops the agents without disposing anything, so resume restarts the same daemons rather
    ///     than needing fresh ones — which is what makes the stop/reset/start sequence below possible.
    /// </summary>
    [Fact]
    public async Task pause_then_resume_leaves_the_daemon_projecting()
    {
        using var host = await StartedHost();
        var coordinator = host.Services.GetRequiredService<IProjectionCoordinator>();

        await coordinator.PauseAsync();
        await coordinator.ResumeAsync();

        var id = await OpenWardAsync(host, "Ward B");

        await coordinator.DaemonForMainDatabase().WaitForNonStaleData(TimeSpan.FromSeconds(30));

        await using var query = host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        (await query.LoadAsync<WardRoster>(id, Token)).ShouldNotBeNull().Name.ShouldBe("Ward B");

        await host.StopAsync(Token);
    }

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
