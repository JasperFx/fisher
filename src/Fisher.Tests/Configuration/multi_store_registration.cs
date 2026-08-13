using Fisher.Linq;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#46 — <c>AddFisherStore&lt;T&gt;</c>, <c>IConfigureFisher</c>, and the mistake this
///     feature makes easy.
/// </summary>
/// <remarks>
///     <para>
///         <b>Several stores are a better fit on SQLite than on either sibling.</b> On PostgreSQL or
///         SQL Server a second store usually means a second schema and the isolation is the server's;
///         on SQLite it can be a second <em>file</em> — separately backed up, separately deletable, and
///         with its own write lock. One writer per file is the central constraint, so splitting a
///         workload across files is the primary way to get two concurrent writers.
///     </para>
///     <para>
///         Both shapes already worked at the storage layer. What was missing was the registration
///         surface — and the guard against the one shape that silently is not isolated.
///     </para>
/// </remarks>
public class multi_store_registration : IAsyncLifetime
{
    private readonly TemporaryDatabase _first = TemporaryDatabase.Create("multi-store-one");
    private readonly TemporaryDatabase _second = TemporaryDatabase.Create("multi-store-two");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _first.Dispose();
        _second.Dispose();

        return ValueTask.CompletedTask;
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task two_stores_over_two_files_are_independent()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddFisher(options =>
                    {
                        options.ConnectionString = _first.ConnectionString;
                        options.AutoCreateSchemaObjects = AutoCreate.All;
                        options.Schema.For<Chart>();
                    })
                    .ApplyAllDatabaseChangesOnStartup();

                services.AddFisherStore<IArchiveStore>(options =>
                    {
                        options.ConnectionString = _second.ConnectionString;
                        options.AutoCreateSchemaObjects = AutoCreate.All;
                        options.Schema.For<Chart>();
                    })
                    .ApplyAllDatabaseChangesOnStartup();
            })
            .StartAsync(Token);

        var primary = host.Services.GetRequiredService<IDocumentStore>();
        var archive = host.Services.GetRequiredService<IArchiveStore>();

        var id = Guid.NewGuid();

        await using (var session = archive.LightweightSession())
        {
            session.Store(new Chart { Id = id, Name = "Admiralty 1" });
            await session.SaveChangesAsync(Token);
        }

        // Isolated in both directions, which is the assertion that matters — a store that leaked would
        // still answer correctly for the one holding the data.
        await using (var reading = archive.LightweightSession())
        {
            (await reading.LoadAsync<Chart>(id, Token)).ShouldNotBeNull();
        }

        await using (var reading = primary.LightweightSession())
        {
            (await reading.LoadAsync<Chart>(id, Token)).ShouldBeNull();
        }

        await host.StopAsync(Token);
    }

    /// <remarks>
    ///     The supported same-file shape: two logical stores isolated by the table prefix
    ///     <c>FisherTableNaming</c> folds the schema name into.
    /// </remarks>
    [Fact]
    public async Task two_stores_over_one_file_with_different_schemas_are_isolated()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddFisher(options =>
                    {
                        options.ConnectionString = _first.ConnectionString;
                        options.AutoCreateSchemaObjects = AutoCreate.All;
                        options.Schema.For<Chart>();
                    })
                    .ApplyAllDatabaseChangesOnStartup();

                services.AddFisherStore<IArchiveStore>(options =>
                    {
                        options.ConnectionString = _first.ConnectionString;
                        options.DatabaseSchemaName = "archive";
                        options.AutoCreateSchemaObjects = AutoCreate.All;
                        options.Schema.For<Chart>();
                    })
                    .ApplyAllDatabaseChangesOnStartup();
            })
            .StartAsync(Token);

        var id = Guid.NewGuid();

        await using (var session = host.Services.GetRequiredService<IArchiveStore>().LightweightSession())
        {
            session.Store(new Chart { Id = id, Name = "Admiralty 1" });
            await session.SaveChangesAsync(Token);
        }

        await using (var reading = host.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            (await reading.LoadAsync<Chart>(id, Token)).ShouldBeNull();
        }

        await host.StopAsync(Token);
    }

    /// <summary>
    ///     The mistake this feature makes easy, and it is silent.
    /// </summary>
    /// <remarks>
    ///     Two stores over one file with the same schema name share every table, so each reads, writes
    ///     and cleans the other's rows. Refused when the second store is built rather than left to be
    ///     discovered as "why did my other store's data disappear".
    /// </remarks>
    [Fact]
    public async Task two_stores_over_one_file_with_the_same_schema_are_refused()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddFisherStore<IArchiveStore>(options =>
                {
                    options.ConnectionString = _first.ConnectionString;
                    options.Schema.For<Chart>();
                });

                services.AddFisherStore<IAuditStore>(options =>
                {
                    options.ConnectionString = _first.ConnectionString;
                    options.Schema.For<Chart>();
                });
            })
            .StartAsync(Token);

        host.Services.GetRequiredService<IArchiveStore>();

        var ex = Should.Throw<InvalidOperationException>(()
            => host.Services.GetRequiredService<IAuditStore>());

        ex.Message.ShouldContain("same DatabaseSchemaName");
        ex.Message.ShouldContain("IArchiveStore");

        await host.StopAsync(Token);
    }

    /// <remarks>
    ///     A secondary store's sessions are reached through the store rather than injected, because
    ///     <c>IDocumentSession</c> cannot be registered scoped for two stores at once. Pinned so the
    ///     convention is a decision — Polecat answers this the same way, and keyed registrations would
    ///     be a shape neither sibling has.
    /// </remarks>
    [Fact]
    public async Task an_injected_session_belongs_to_the_primary_store()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddFisher(options =>
                    {
                        options.ConnectionString = _first.ConnectionString;
                        options.AutoCreateSchemaObjects = AutoCreate.All;
                        options.StoreName = "Primary";
                    })
                    .ApplyAllDatabaseChangesOnStartup();

                services.AddFisherStore<IArchiveStore>(options =>
                {
                    options.ConnectionString = _second.ConnectionString;
                    options.AutoCreateSchemaObjects = AutoCreate.All;
                });
            })
            .StartAsync(Token);

        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        ((Fisher.Internal.FisherSession)session).Options.StoreName.ShouldBe("Primary");

        // And the secondary store's default name is its marker, so the two are distinguishable in a
        // monitoring tool without anything being said.
        host.Services.GetRequiredService<IArchiveStore>().Options.StoreName.ShouldBe(nameof(IArchiveStore));

        await host.StopAsync(Token);
    }

    /// <remarks>
    ///     A monitoring console reads <c>GetServices&lt;IEventStore&gt;()</c>. A marker proxy is not one
    ///     — <c>DispatchProxy</c> implements the interfaces it was asked for and no others, and the
    ///     tooling surfaces are implemented explicitly and are deliberately not on
    ///     <c>IDocumentStore</c> — so the registration reaches through to the real store.
    /// </remarks>
    [Fact]
    public async Task both_stores_are_discoverable_by_monitoring_tools()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddFisher(options =>
                {
                    options.ConnectionString = _first.ConnectionString;
                    options.StoreName = "Primary";
                });

                services.AddFisherStore<IArchiveStore>(options =>
                    options.ConnectionString = _second.ConnectionString);
            })
            .StartAsync(Token);

        var stores = host.Services.GetServices<IEventStore>().ToList();

        stores.Count.ShouldBe(2);
        stores.ShouldAllBe(x => x != null);

        await host.StopAsync(Token);
    }

    // ---- IConfigureFisher ----

    [Fact]
    public async Task deferred_configuration_can_read_a_service()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(new ProjectionSwitch(true));
                services.AddSingleton<IConfigureFisher, EnableTheProjection>();

                services.AddFisher(options =>
                    {
                        options.ConnectionString = _first.ConnectionString;
                        options.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup();
            })
            .StartAsync(Token);

        var store = host.Services.GetRequiredService<IDocumentStore>();

        store.Options.Projections.All.Any(x => x is CourseProjection).ShouldBeTrue();

        await host.StopAsync(Token);
    }

    /// <remarks>
    ///     Which store a contribution is about has to be sayable, or a library's configuration reaches
    ///     stores it has never heard of.
    /// </remarks>
    [Fact]
    public async Task deferred_configuration_is_scoped_to_the_store_it_names()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(new ProjectionSwitch(true));
                services.AddSingleton<IConfigureFisher, EnableTheProjectionOnTheArchive>();

                services.AddFisher(options => options.ConnectionString = _first.ConnectionString);

                services.AddFisherStore<IArchiveStore>(options =>
                    options.ConnectionString = _second.ConnectionString);
            })
            .StartAsync(Token);

        host.Services.GetRequiredService<IDocumentStore>()
            .Options.Projections.All.Any(x => x is CourseProjection).ShouldBeFalse();

        host.Services.GetRequiredService<IArchiveStore>()
            .Options.Projections.All.Any(x => x is CourseProjection).ShouldBeTrue();

        await host.StopAsync(Token);
    }

    // ---- ConfigureFisher(...) — the lambda form (fisher#70) ----

    /// <remarks>
    ///     The lambda convenience both siblings ship, so integration code that layers its own options
    ///     onto a store somebody else registered reads alike across the three stores.
    /// </remarks>
    [Fact]
    public async Task a_lambda_contribution_reaches_the_primary_store()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.ConfigureFisher(options =>
                    options.Projections.Add(new CourseProjection(), ProjectionLifecycle.Inline));

                services.AddFisher(options => options.ConnectionString = _first.ConnectionString);
            })
            .StartAsync(Token);

        host.Services.GetRequiredService<IDocumentStore>()
            .Options.Projections.All.Any(x => x is CourseProjection).ShouldBeTrue();

        await host.StopAsync(Token);
    }

    [Fact]
    public async Task a_lambda_contribution_can_read_a_service()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(new ProjectionSwitch(true));

                services.ConfigureFisher((sp, options) =>
                {
                    if (sp.GetRequiredService<ProjectionSwitch>().Enabled)
                    {
                        options.Projections.Add(new CourseProjection(), ProjectionLifecycle.Inline);
                    }
                });

                services.AddFisher(options => options.ConnectionString = _first.ConnectionString);
            })
            .StartAsync(Token);

        host.Services.GetRequiredService<IDocumentStore>()
            .Options.Projections.All.Any(x => x is CourseProjection).ShouldBeTrue();

        await host.StopAsync(Token);
    }

    /// <remarks>
    ///     The targeted form, which is what an ancillary-store integration needs — a contribution that
    ///     reached every store in the application, including ones the library has never heard of, would
    ///     not be configuration but a surprise.
    /// </remarks>
    [Fact]
    public async Task a_targeted_lambda_reaches_only_the_store_it_names()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.ConfigureFisher<IArchiveStore>(options =>
                    options.Projections.Add(new CourseProjection(), ProjectionLifecycle.Inline));

                services.AddFisher(options => options.ConnectionString = _first.ConnectionString);

                services.AddFisherStore<IArchiveStore>(options =>
                    options.ConnectionString = _second.ConnectionString);
            })
            .StartAsync(Token);

        host.Services.GetRequiredService<IDocumentStore>()
            .Options.Projections.All.Any(x => x is CourseProjection).ShouldBeFalse();

        host.Services.GetRequiredService<IArchiveStore>()
            .Options.Projections.All.Any(x => x is CourseProjection).ShouldBeTrue();

        await host.StopAsync(Token);
    }

    /// <remarks>
    ///     <c>ConfigureFisher&lt;T&gt;</c> registers its lambda against both service types, so the
    ///     dedup in <c>Configured</c> is what keeps one call from configuring twice. Counted rather than
    ///     asserted on the options, because adding the same projection twice would look identical to
    ///     adding it once through most assertions.
    /// </remarks>
    [Fact]
    public async Task a_targeted_lambda_configures_once()
    {
        var calls = 0;

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.ConfigureFisher<IArchiveStore>(_ => Interlocked.Increment(ref calls));

                services.AddFisherStore<IArchiveStore>(options =>
                    options.ConnectionString = _second.ConnectionString);
            })
            .StartAsync(Token);

        _ = host.Services.GetRequiredService<IArchiveStore>().Options;

        calls.ShouldBe(1);

        await host.StopAsync(Token);
    }

    /// <remarks>
    ///     <b>This registration style silently did nothing</b> (fisher#70). Polecat and Marten resolve
    ///     the closed <c>IConfigure*&lt;T&gt;</c>, so code ported from either registers against
    ///     <c>IConfigureFisher&lt;T&gt;</c> — which <c>GetServices&lt;IConfigureFisher&gt;()</c> does not
    ///     return, because the container matches on the service type a registration named rather than on
    ///     what it implements. A contribution that compiles, registers and never runs.
    /// </remarks>
    [Fact]
    public async Task a_contribution_registered_against_the_closed_interface_is_applied()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(new ProjectionSwitch(true));
                services.AddSingleton<IConfigureFisher<IArchiveStore>, EnableTheProjectionOnTheArchive>();

                services.AddFisher(options => options.ConnectionString = _first.ConnectionString);

                services.AddFisherStore<IArchiveStore>(options =>
                    options.ConnectionString = _second.ConnectionString);
            })
            .StartAsync(Token);

        host.Services.GetRequiredService<IDocumentStore>()
            .Options.Projections.All.Any(x => x is CourseProjection).ShouldBeFalse();

        host.Services.GetRequiredService<IArchiveStore>()
            .Options.Projections.All.Any(x => x is CourseProjection).ShouldBeTrue();

        await host.StopAsync(Token);
    }

    /// <remarks>
    ///     One daemon per store, which is the shape that gets two concurrent writers out of SQLite.
    /// </remarks>
    [Fact]
    public async Task a_secondary_store_runs_its_own_daemon()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddFisherStore<IArchiveStore>(options =>
                    {
                        options.ConnectionString = _second.ConnectionString;
                        options.AutoCreateSchemaObjects = AutoCreate.All;
                        options.Projections.Snapshot<Course>(SnapshotLifecycle.Async);
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .AddAsyncDaemon();
            })
            .StartAsync(Token);

        var archive = host.Services.GetRequiredService<IArchiveStore>();
        var streamId = Guid.NewGuid();

        await using (var session = archive.LightweightSession())
        {
            session.Events.StartStream<Course>(streamId, new LegSailed(12));
            await session.SaveChangesAsync(Token);
        }

        await archive.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));

        await using var query = archive.LightweightSession();
        (await query.LoadAsync<Course>(streamId, Token))!.Miles.ShouldBe(12);

        await host.StopAsync(Token);
    }

    [Fact]
    public void hot_cold_is_refused_for_a_secondary_store_too()
    {
        var services = new ServiceCollection();

        Should.Throw<NotSupportedException>(()
                => services.AddFisherStore<IArchiveStore>(options
                    => options.ConnectionString = _second.ConnectionString)
                    .AddAsyncDaemon(JasperFx.Events.Daemon.DaemonMode.HotCold))
            .Message.ShouldContain("hot-cold");
    }
}

public interface IArchiveStore : IDocumentStore;

public interface IAuditStore : IDocumentStore;

public record ProjectionSwitch(bool Enabled);

public class EnableTheProjection : IConfigureFisher
{
    public void Configure(IServiceProvider services, StoreOptions options)
    {
        if (services.GetRequiredService<ProjectionSwitch>().Enabled)
        {
            options.Projections.Add(new CourseProjection(), ProjectionLifecycle.Inline);
        }
    }
}

public class EnableTheProjectionOnTheArchive : EnableTheProjection, IConfigureFisher<IArchiveStore>;

public class CourseProjection : Fisher.Projections.SingleStreamProjection<Course, Guid>;

public class Chart
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public record LegSailed(int Miles);

public class Course
{
    public Guid Id { get; set; }
    public int Miles { get; set; }

    public void Apply(LegSailed leg) => Miles += leg.Miles;
}
