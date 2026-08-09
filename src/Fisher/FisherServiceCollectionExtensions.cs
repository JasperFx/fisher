using JasperFx;
using JasperFx.Events.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fisher;

/// <summary>
///     <c>AddFisher(...)</c> — registering a Fisher store and its sessions with an
///     <see cref="IServiceCollection" /> (fisher#20).
/// </summary>
/// <remarks>
///     <para>
///         Mirrors <c>AddMarten</c> and <c>AddPolecat</c>: the store is a singleton, sessions are
///         scoped so a session's lifetime matches a request, and the configuration expression returned
///         carries the opt-ins — schema application on startup, and the async daemon.
///     </para>
///     <para>
///         Deliberately smaller than either sibling's. There is no multi-store
///         <c>AddFisherStore&lt;T&gt;</c>, no <c>IConfigureFisher</c> chain and no initial-data seeding;
///         each of those is additive and none of them is needed to make a Fisher store usable in an
///         ASP.NET Core application, which was the whole gap.
///     </para>
/// </remarks>
public static class FisherServiceCollectionExtensions
{
    /// <summary>
    ///     Register a Fisher store against a SQLite connection string.
    /// </summary>
    public static FisherConfigurationExpression AddFisher(this IServiceCollection services,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        return services.AddFisher(options => options.ConnectionString = connectionString);
    }

    /// <summary>
    ///     Register a Fisher store, configuring its <see cref="StoreOptions" />.
    /// </summary>
    public static FisherConfigurationExpression AddFisher(this IServiceCollection services,
        Action<StoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        return services.AddFisher(_ =>
        {
            var options = new StoreOptions();
            configure(options);
            return options;
        });
    }

    /// <summary>
    ///     Register a Fisher store whose options are built from the container — for a connection string
    ///     out of <c>IConfiguration</c>, or anything else that needs a resolved service.
    /// </summary>
    public static FisherConfigurationExpression AddFisher(this IServiceCollection services,
        Func<IServiceProvider, StoreOptions> optionSource)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionSource);

        services.AddSingleton(optionSource);

        services.AddSingleton(sp => new DocumentStore(sp.GetRequiredService<StoreOptions>()));

        // Both the concrete type and the interface resolve to the one singleton. The interface is what
        // application code should depend on (fisher#45); the concrete registration stays so that code
        // written before it — and every test in this repo — keeps resolving.
        services.AddSingleton<IDocumentStore>(sp => sp.GetRequiredService<DocumentStore>());

        // The bridge monitoring tools discover a store through — CritterWatch and Wolverine both read
        // GetServices<IEventStore>(). Without it a registered Fisher store is invisible to them even
        // though DocumentStore implements the interface, because it does so explicitly.
        services.AddSingleton<JasperFx.Events.IEventStore>(
            sp => sp.GetRequiredService<DocumentStore>());

        // TryAdd so an application that registers its own factory — to scope sessions to a tenant read
        // off the request, say — keeps it whichever side of AddFisher the registration lands.
        services.TryAddSingleton<ISessionFactory>(
            sp => new DefaultSessionFactory(sp.GetRequiredService<IDocumentStore>()));

        services.AddScoped(sp => sp.GetRequiredService<ISessionFactory>().OpenSession());
        services.AddScoped(sp => sp.GetRequiredService<ISessionFactory>().QuerySession());

        return new FisherConfigurationExpression(services);
    }
}

/// <summary>
///     The opt-ins available after <c>AddFisher</c>.
/// </summary>
public sealed class FisherConfigurationExpression
{
    private readonly IServiceCollection _services;

    internal FisherConfigurationExpression(IServiceCollection services) => _services = services;

    /// <summary>
    ///     Apply every configured schema change when the host starts.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Without this a consumer has to call
    ///         <see cref="DocumentStore.ApplyAllConfiguredChangesToDatabaseAsync" /> itself, which is what
    ///         every Fisher application did before fisher#20. Honours
    ///         <see cref="StoreOptions.AutoCreateSchemaObjects" />: under
    ///         <see cref="AutoCreate.None" /> the hosted service starts and does nothing, rather than
    ///         this method being the thing that quietly overrides the policy.
    ///     </para>
    ///     <para>
    ///         Document tables are still created on demand at first write, because a document type can
    ///         be stored without ever being registered — so this covers the tables the schema knows
    ///         about, not every table that will exist.
    ///     </para>
    /// </remarks>
    public FisherConfigurationExpression ApplyAllDatabaseChangesOnStartup()
    {
        _services.AddSingleton<IHostedService, FisherSchemaActivator>();
        return this;
    }

    /// <summary>
    ///     Run <see cref="StoreOptions.InitialData" /> at startup (fisher#39).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Ordering is the whole reason this is separate from registering the seeders.</b>
    ///         Hosted services start in registration order, so this must be called after
    ///         <see cref="ApplyAllDatabaseChangesOnStartup" /> — a seeder writing to a table that does
    ///         not exist yet would fail, and nothing else creates them. Calling it first is refused by
    ///         name rather than left to present as "no such table" at startup.
    ///     </para>
    ///     <para>
    ///         Seeders run once per host start and are expected to be idempotent; Fisher keeps no
    ///         "already seeded" marker. See <see cref="IInitialData" />.
    ///     </para>
    /// </remarks>
    public FisherConfigurationExpression SeedInitialDataOnStartup()
    {
        if (_services.All(x => x.ImplementationType != typeof(FisherSchemaActivator)))
        {
            throw new InvalidOperationException(
                "Call ApplyAllDatabaseChangesOnStartup() before SeedInitialDataOnStartup(). Hosted "
                + "services start in registration order, and a seeder that runs before the schema is "
                + "applied writes to tables that do not exist yet. If the schema is applied some other "
                + "way, run the seeders that way too.");
        }

        _services.AddSingleton<IHostedService, FisherInitialDataActivator>();
        return this;
    }

    /// <summary>
    ///     Host the async projection daemon for the lifetime of the application.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b><see cref="DaemonMode.HotCold" /> is refused</b>, and that is a real limitation rather
    ///         than an omission. Hot-cold failover means several nodes competing for one leadership
    ///         lease through the database, and a Fisher store is a file — usually a local one, and one
    ///         that SQLite does not make safe to share over a network filesystem. Accepting the mode and
    ///         silently running Solo would give an application the opposite of the guarantee it asked
    ///         for: every node projecting at once.
    ///     </para>
    ///     <para>
    ///         <see cref="DaemonMode.Solo" /> starts the daemon here.
    ///         <see cref="DaemonMode.Disabled" /> registers nothing.
    ///         <see cref="DaemonMode.ExternallyManaged" /> also registers nothing, on the understanding that
    ///         something else is running the projections — which is exactly what that mode means.
    ///     </para>
    /// </remarks>
    public FisherConfigurationExpression AddAsyncDaemon(DaemonMode mode = DaemonMode.Solo)
    {
        switch (mode)
        {
            case DaemonMode.Solo:
                _services.AddSingleton<IHostedService, FisherDaemonHostedService>();
                break;

            case DaemonMode.HotCold:
                throw new NotSupportedException(
                    "Fisher has no hot-cold daemon coordination. Leadership failover requires several "
                    + "nodes sharing one database, and a SQLite store is a file — SQLite does not make "
                    + "one safe to share across nodes. Use DaemonMode.Solo, or DaemonMode.ExternallyManaged if "
                    + "something else runs the projections.");

            case DaemonMode.Disabled:
            case DaemonMode.ExternallyManaged:
                break;
        }

        return this;
    }
}

/// <summary>
///     Applies configured schema changes once, at host start.
/// </summary>
internal sealed class FisherSchemaActivator : IHostedService
{
    private readonly IDocumentStore _store;

    public FisherSchemaActivator(IDocumentStore store) => _store = store;

    public Task StartAsync(CancellationToken cancellationToken)
        => _store.Options.AutoCreateSchemaObjects == AutoCreate.None
            ? Task.CompletedTask
            : _store.ApplyAllConfiguredChangesToDatabaseAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
///     Runs the configured initial-data seeders once, at host start, after the schema activator
///     (fisher#39).
/// </summary>
internal sealed class FisherInitialDataActivator : IHostedService
{
    private readonly IDocumentStore _store;

    public FisherInitialDataActivator(IDocumentStore store) => _store = store;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var data in _store.Options.InitialData)
        {
            await data.Populate(_store, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
///     Runs the async projection daemon for the host's lifetime.
/// </summary>
/// <remarks>
///     <para>
///         One daemon, stopped before the host finishes shutting down. A second daemon over the same
///         file would mean two writers contending for one write lock and two shards replaying the same
///         range, which is why this holds the instance rather than building one per call.
///     </para>
///     <para>
///         <b>The WAL check runs here, and this is the point of putting it here.</b>
///         <c>BuildProjectionDaemonAsync</c> has warned about a non-WAL journal since the daemon
///         landed, but only a consumer building a daemon by hand ever saw it. Under a hosted service
///         the warning reaches the application log at startup, which is where somebody is actually
///         looking — and the misconfiguration it describes presents as a slow projection rather than
///         as an error, so being seen is the whole value. It stays a warning rather than a refusal:
///         a non-WAL store still projects correctly, just serialised against its writers.
///     </para>
/// </remarks>
internal sealed class FisherDaemonHostedService : IHostedService, IDisposable
{
    private readonly IDocumentStore _store;
    private readonly ILogger<FisherDaemonHostedService> _logger;
    private IProjectionDaemon? _daemon;

    public FisherDaemonHostedService(IDocumentStore store, ILogger<FisherDaemonHostedService>? logger = null)
    {
        _store = store;
        _logger = logger ?? NullLogger<FisherDaemonHostedService>.Instance;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _daemon = await _store.BuildProjectionDaemonAsync(logger: _logger).ConfigureAwait(false);
        await _daemon.StartAllAsync().ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_daemon is not null)
        {
            await _daemon.StopAllAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc cref="Storage.FisherDatabase.Dispose" />
    public void Dispose()
    {
        _daemon?.Dispose();
        _daemon = null;
    }
}
