using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fisher.Tests.Configuration;

/// <summary>
///     <c>AddFisher(...)</c> — fisher#20.
/// </summary>
/// <remarks>
///     The gap this closed was not a missing feature but a missing on-ramp: every Fisher application
///     built a <c>DocumentStore</c> by hand, called
///     <c>ApplyAllConfiguredChangesToDatabaseAsync</c> itself and hosted the daemon itself. So the
///     assertions are about lifetimes, about the opt-ins actually taking effect at host start, and
///     about the one mode Fisher refuses.
/// </remarks>
public class service_registration : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("registration");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    private ServiceProvider ProviderFor(Action<FisherConfigurationExpression>? configure = null,
        Action<StoreOptions>? options = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var expression = services.AddFisher(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
            options?.Invoke(o);
        });

        configure?.Invoke(expression);

        return services.BuildServiceProvider();
    }

    // ---- lifetimes ----

    [Fact]
    public void the_store_is_a_singleton()
    {
        using var provider = ProviderFor();

        var first = provider.GetRequiredService<DocumentStore>();
        var second = provider.GetRequiredService<DocumentStore>();

        first.ShouldBeSameAs(second);
    }

    [Fact]
    public void sessions_are_scoped()
    {
        using var provider = ProviderFor();

        using var scope = provider.CreateScope();
        var first = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var second = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        first.ShouldBeSameAs(second);

        using var other = provider.CreateScope();
        other.ServiceProvider.GetRequiredService<IDocumentSession>().ShouldNotBeSameAs(first);
    }

    [Fact]
    public void a_query_session_resolves_too()
    {
        using var provider = ProviderFor();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IQuerySession>().ShouldNotBeNull();
    }

    /// <summary>
    ///     The bridge CritterWatch and Wolverine discover a store through. <c>DocumentStore</c>
    ///     implements <see cref="IEventStore" /> explicitly, so without the registration the store is
    ///     invisible to <c>GetServices&lt;IEventStore&gt;()</c> despite implementing it.
    /// </summary>
    [Fact]
    public void the_store_is_discoverable_as_an_event_store()
    {
        using var provider = ProviderFor();

        provider.GetServices<IEventStore>().ShouldHaveSingleItem()
            .ShouldBeSameAs(provider.GetRequiredService<DocumentStore>());
    }

    [Fact]
    public void the_connection_string_overload_configures_the_store()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFisher(_database.ConnectionString);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<DocumentStore>().Options.ConnectionString
            .ShouldBe(_database.ConnectionString);
    }

    /// <summary>
    ///     The seam exists so session policy is decided once. <c>TryAddSingleton</c> is what lets a
    ///     registration land on either side of <c>AddFisher</c> and still win.
    /// </summary>
    [Fact]
    public void a_custom_session_factory_replaces_the_default()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISessionFactory, CountingSessionFactory>();
        services.AddFisher(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
        });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISessionFactory>().ShouldBeOfType<CountingSessionFactory>();
    }

    // ---- the daemon mode Fisher refuses ----

    /// <summary>
    ///     Refused rather than silently downgraded to Solo. Accepting it would give an application the
    ///     opposite of the guarantee it asked for — every node projecting at once — and the reason is a
    ///     real property of the store rather than an unimplemented feature.
    /// </summary>
    [Fact]
    public void hot_cold_is_refused_and_says_why()
    {
        var services = new ServiceCollection();

        var ex = Should.Throw<NotSupportedException>(() =>
            services.AddFisher(_database.ConnectionString).AddAsyncDaemon(DaemonMode.HotCold));

        ex.Message.ShouldContain("Solo");
        ex.Message.ShouldContain("file");
    }

    [Fact]
    public void a_disabled_daemon_registers_no_hosted_service()
    {
        using var provider = ProviderFor(x => x.AddAsyncDaemon(DaemonMode.Disabled));

        provider.GetServices<IHostedService>().ShouldBeEmpty();
    }

    [Fact]
    public void an_externally_managed_daemon_registers_no_hosted_service()
    {
        using var provider = ProviderFor(x => x.AddAsyncDaemon(DaemonMode.ExternallyManaged));

        provider.GetServices<IHostedService>().ShouldBeEmpty();
    }

    // ---- what the hosted services actually do ----

    [Fact]
    public async Task applying_changes_on_startup_creates_the_schema()
    {
        using var provider = ProviderFor(x => x.ApplyAllDatabaseChangesOnStartup());

        var hosted = provider.GetServices<IHostedService>().ShouldHaveSingleItem();
        await hosted.StartAsync(TestContext.Current.CancellationToken);

        // The event tables exist without anything having been written.
        await using var session = provider.GetRequiredService<DocumentStore>().LightweightSession();
        var state = await session.Events.FetchStreamStateAsync(Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        state.ShouldBeNull();
    }

    /// <summary>
    ///     <see cref="AutoCreate.None" /> wins over the opt-in: the hosted service starts and does
    ///     nothing, rather than the registration being the thing that quietly overrides schema policy.
    /// </summary>
    [Fact]
    public async Task auto_create_none_is_honoured_on_startup()
    {
        using var provider = ProviderFor(
            x => x.ApplyAllDatabaseChangesOnStartup(),
            o => o.AutoCreateSchemaObjects = AutoCreate.None);

        var hosted = provider.GetServices<IHostedService>().ShouldHaveSingleItem();
        await hosted.StartAsync(TestContext.Current.CancellationToken);

        // Nothing was created, so reading fails rather than returning an empty answer.
        await using var session = provider.GetRequiredService<DocumentStore>().LightweightSession();

        await Should.ThrowAsync<Microsoft.Data.Sqlite.SqliteException>(async () =>
            await session.Events.FetchStreamStateAsync(Guid.NewGuid(),
                TestContext.Current.CancellationToken));
    }

    /// <summary>
    ///     The end-to-end shape an application actually uses: register, start the host's services, write
    ///     through a scoped session, and let the hosted daemon project it.
    /// </summary>
    [Fact]
    public async Task a_registered_store_projects_through_the_hosted_daemon()
    {
        using var provider = ProviderFor(
            x => x.ApplyAllDatabaseChangesOnStartup().AddAsyncDaemon(),
            o => o.Projections.Snapshot<RegisteredQuest>(SnapshotLifecycle.Async));

        var hosted = provider.GetServices<IHostedService>().ToArray();
        hosted.Length.ShouldBe(2);

        foreach (var service in hosted)
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
        }

        var streamId = Guid.NewGuid();

        using (var scope = provider.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Events.StartStream<RegisteredQuest>(streamId, new QuestBegun("Find the ring"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var store = provider.GetRequiredService<DocumentStore>();
        await store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));

        await using var query = store.LightweightSession();
        var quest = await query.LoadAsync<RegisteredQuest>(streamId, TestContext.Current.CancellationToken);

        quest.ShouldNotBeNull();
        quest.Name.ShouldBe("Find the ring");

        foreach (var service in hosted)
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private sealed class CountingSessionFactory : ISessionFactory
    {
        private readonly DocumentStore _store;

        public CountingSessionFactory(DocumentStore store) => _store = store;

        public IDocumentSession OpenSession() => _store.LightweightSession();

        public IQuerySession QuerySession() => _store.LightweightSession();
    }
}

public record QuestBegun(string Name);

public class RegisteredQuest
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public static RegisteredQuest Create(QuestBegun e) => new() { Name = e.Name };
}
