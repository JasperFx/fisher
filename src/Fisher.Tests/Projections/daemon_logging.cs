using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fisher.Tests.Projections;

/// <summary>
///     fisher#658: a daemon logs through the host's <c>ILoggerFactory</c> however it was started.
/// </summary>
/// <remarks>
///     Wolverine-managed projection distribution builds each daemon through
///     <c>IEventStore.BuildProjectionDaemonAsync(DatabaseId)</c> and starts one agent at a time with
///     <c>StartAgentAsync(ShardName)</c>. That seam has no logger parameter, and Fisher used to hand the daemon
///     <c>NullLogger</c> there, so the identity access line, every parked identity and
///     <c>ParkedLimitReachedException</c> went nowhere. The first test drives that seam exactly as Wolverine does;
///     Wolverine itself isn't referenced here.
/// </remarks>
public class daemon_logging : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("daemon-logging");
    private readonly CapturingLoggerProvider _log = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private ServiceProvider providerWith(Action<FisherConfigurationExpression>? fisher = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(x => x.AddProvider(_log).SetMinimumLevel(LogLevel.Debug));

        var expression = services.AddFisher(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<GuidParkTally>(SnapshotLifecycle.Async);

            // So the second poisoned identity passes the cap and throws ParkedLimitReachedException
            options.Projections.Errors.MaxParkedIdentitiesPerShard = 1;
        });

        fisher?.Invoke(expression);

        return services.BuildServiceProvider();
    }

    private static async Task appendAsync(IDocumentStore store, Guid stream, params object[] events)
    {
        await using var session = store.LightweightSession();
        session.Events.Append(stream, events);
        await session.SaveChangesAsync(Token);
    }

    private static async Task eventuallyAsync(Func<Task<bool>> condition, string description)
    {
        using var timeout = new CancellationTokenSource(DaemonWait.Timeout);
        while (!await condition())
        {
            if (timeout.IsCancellationRequested) throw new TimeoutException($"Timed out waiting until {description}");
            await Task.Delay(50, Token);
        }
    }

    private static async Task<DatabaseId> mainDatabaseIdAsync(IEventStore store)
    {
        // The same descriptor and the same id Wolverine's EventStoreAgents builds its agent URIs from
        var usage = (await store.TryDescribeDatabasesAsync(Token)).ShouldNotBeNull();
        var database = usage.MainDatabase ?? usage.Databases.ShouldHaveSingleItem();
        return new DatabaseId(database.ServerName, database.DatabaseName);
    }

    private void theIdentityAccessLineWasLogged()
    {
        _log.Entries.ShouldContain(x => x.Level == LogLevel.Information
                                        && x.Message.StartsWith("Projection GuidParkTally parks on an apply error (identity access"),
            "No identity access line was logged for GuidParkTally. Logged:\n" + _log.Dump());
    }

    private void theParkWasLogged(Guid stream)
    {
        _log.Entries.ShouldContain(x => x.Level == LogLevel.Warning
                                        && x.Message.Contains("parked " + ParkedIdentityText.Format(stream))
                                        && x.Exception is InvalidOperationException,
            "No parked identity was logged. Logged:\n" + _log.Dump());
    }

    [Fact]
    public async Task a_daemon_built_and_started_the_way_wolverine_does_it_logs_its_parking()
    {
        await using var provider = providerWith();
        var store = provider.GetRequiredService<DocumentStore>();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        IEventStore eventStore = store;
        var databaseId = await mainDatabaseIdAsync(eventStore);
        var shard = store.Advanced.AllAsyncProjectionShardNames().Single();

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        ParkTallyPoison.On(first);
        ParkTallyPoison.On(second);

        var daemon = await eventStore.BuildProjectionDaemonAsync(databaseId);
        try
        {
            theIdentityAccessLineWasLogged();

            await daemon.StartAgentAsync(shard, Token);

            await appendAsync(store, first, new ParkTallied(), new ParkTallyPoisoned());
            await eventuallyAsync(async () => (await store.Database.FetchParkedIdentitiesAsync(
                new ParkedIdentityQuery(), Token)).Count == 1, "the first identity is parked");

            theParkWasLogged(first);

            // Over the cap of 1, so the shard pauses on ParkedLimitReachedException
            await appendAsync(store, second, new ParkTallied(), new ParkTallyPoisoned());
            await eventuallyAsync(() => Task.FromResult(daemon.StatusFor(shard.Identity) == AgentStatus.Paused),
                "the shard pauses");

            _log.Entries.ShouldContain(x => x.Exception is ParkedLimitReachedException,
                "ParkedLimitReachedException was not logged. Logged:\n" + _log.Dump());
        }
        finally
        {
            ParkTallyPoison.Off(first);
            ParkTallyPoison.Off(second);
            await daemon.StopAllAsync();
            daemon.Dispose();
        }
    }

    [Fact]
    public async Task the_hosted_daemon_logs_its_parking()
    {
        await using var provider = providerWith(x => x.AddAsyncDaemon(DaemonMode.Solo));
        var store = provider.GetRequiredService<DocumentStore>();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var stream = Guid.NewGuid();
        ParkTallyPoison.On(stream);

        var hosted = provider.GetServices<IHostedService>().ShouldHaveSingleItem();
        await hosted.StartAsync(Token);
        try
        {
            theIdentityAccessLineWasLogged();

            await appendAsync(store, stream, new ParkTallied(), new ParkTallyPoisoned());
            await eventuallyAsync(async () => (await store.Database.FetchParkedIdentitiesAsync(
                new ParkedIdentityQuery(), Token)).Count == 1, "the identity is parked");

            theParkWasLogged(stream);
        }
        finally
        {
            ParkTallyPoison.Off(stream);
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task a_daemon_built_by_hand_from_a_registered_store_logs_through_the_host()
    {
        await using var provider = providerWith();
        var store = provider.GetRequiredService<DocumentStore>();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        using var daemon = await store.BuildProjectionDaemonAsync();

        theIdentityAccessLineWasLogged();
        _log.Entries.ShouldContain(x => x.Category == typeof(Fisher.Events.Daemon.FisherProjectionDaemon).FullName);
    }

    [Fact]
    public async Task a_daemon_handed_a_logger_uses_that_one()
    {
        await using var provider = providerWith();
        var store = provider.GetRequiredService<DocumentStore>();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        using var daemon = await store.BuildProjectionDaemonAsync(logger: NullLogger.Instance);

        _log.Entries.ShouldNotContain(x => x.Message.StartsWith("Projection GuidParkTally"));
    }

    [Fact]
    public async Task a_store_built_without_a_container_has_no_logger_factory()
    {
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        store.Options.LogFactory.ShouldBeNull();
        store.DefaultDaemonLogger().ShouldBeSameAs(NullLogger.Instance);
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries) return [.._entries];
            }
        }

        public string Dump()
            => string.Join("\n", Entries.Select(x => $"[{x.Level}] {x.Category}: {x.Message} {x.Exception?.GetType().Name}"));

        public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class Capturing(string category, List<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (entries) entries.Add(new LogEntry(category, logLevel, formatter(state, exception), exception));
            }
        }
    }
}
