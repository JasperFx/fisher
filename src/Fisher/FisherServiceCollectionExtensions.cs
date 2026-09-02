using System.Diagnostics.CodeAnalysis;
using Fisher.Internal;
using JasperFx;
using JasperFx.Descriptors;
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
///         <c>AddFisherStore&lt;T&gt;</c> registers a second, independently configured store —
///         see it for why several stores are a <em>better</em> fit on SQLite than on either sibling.
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

        services.AddSingleton(sp => Configured(sp, optionSource(sp), forStore: null));

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

    /// <summary>
    ///     Register a second, independently configured store, resolved through the marker interface
    ///     <typeparamref name="T" /> (fisher#46).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Several stores are a better fit here than on either sibling.</b> On PostgreSQL or SQL
    ///         Server a second store usually means a second schema in one database, and the isolation is
    ///         the server's. On SQLite a second store can simply be a second <em>file</em> — genuinely
    ///         isolated, separately backed up, separately deletable, <b>and with its own write lock</b>.
    ///         That last point is not a small one: one writer per file is SQLite's central constraint,
    ///         so splitting a workload across two files is the primary way to get two concurrent
    ///         writers, and this is the ergonomic front door to it.
    ///     </para>
    ///     <para>
    ///         Both shapes already worked at the storage layer — two logical stores in one file are
    ///         isolated by the table prefix <c>FisherTableNaming</c> folds the schema name into. What
    ///         was missing was the registration surface.
    ///     </para>
    ///     <para>
    ///         <b>A secondary store's sessions are reached through the store</b>
    ///         (<c>services.GetRequiredService&lt;IOtherStore&gt;().LightweightSession()</c>) rather
    ///         than injected. <c>IDocumentSession</c> cannot be registered scoped for two stores at
    ///         once, and Polecat answers this the same way. Inventing keyed registrations would give
    ///         Fisher a shape neither sibling has, for a case where a property access already reads
    ///         perfectly well.
    ///     </para>
    ///     <para>
    ///         <see cref="StoreOptions.StoreName" /> defaults to the marker's name, so the two stores
    ///         are distinguishable in a monitoring tool and in a trace without anything being said.
    ///     </para>
    /// </remarks>
    public static FisherStoreConfigurationExpression<T> AddFisherStore<T>(this IServiceCollection services,
        Action<StoreOptions> configure) where T : class, IDocumentStore
    {
        ArgumentNullException.ThrowIfNull(configure);

        return services.AddFisherStore<T>(_ =>
        {
            var options = new StoreOptions { StoreName = typeof(T).Name };
            configure(options);
            return options;
        });
    }

    /// <inheritdoc cref="AddFisherStore{T}(IServiceCollection,Action{StoreOptions})" />
    public static FisherStoreConfigurationExpression<T> AddFisherStore<T>(this IServiceCollection services,
        Func<IServiceProvider, StoreOptions> optionSource) where T : class, IDocumentStore
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionSource);

        services.TryAddSingleton<FisherStoreRegistry>();

        services.AddSingleton(sp =>
        {
            var options = Configured(sp, optionSource(sp), typeof(T));
            var store = new DocumentStore(options);

            sp.GetRequiredService<FisherStoreRegistry>().Register(typeof(T), options);

            return SecondaryStoreProxy.For<T>(store);
        });

        // Discoverable by monitoring tools alongside the primary store, which is the whole reason a
        // secondary store carries its own StoreName.
        services.AddSingleton<JasperFx.Events.IEventStore>(sp
            => (JasperFx.Events.IEventStore)UnwrapForTooling(sp.GetRequiredService<T>()));

        return new FisherStoreConfigurationExpression<T>(services);
    }

    /// <summary>
    ///     Contribute configuration to the primary store's options from a lambda (fisher#70).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The lambda convenience over <see cref="IConfigureFisher" />, and the same surface both
    ///         siblings ship (<c>ConfigureMarten</c>, <c>ConfigurePolecat</c>) — so integration code that
    ///         layers its own <see cref="StoreOptions" /> contributions onto a store somebody else
    ///         registered reads alike across the three stores. That unification is what
    ///         JasperFx/wolverine#3907 asked for.
    ///     </para>
    ///     <para>
    ///         Runs after the <c>AddFisher(...)</c> lambda, in registration order, and may be called
    ///         either side of it — the contributions are resolved when the store is built, not when this
    ///         is called.
    ///     </para>
    /// </remarks>
    public static IServiceCollection ConfigureFisher(this IServiceCollection services,
        Action<StoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        return services.ConfigureFisher((_, options) => configure(options));
    }

    /// <inheritdoc cref="ConfigureFisher(IServiceCollection,Action{StoreOptions})" />
    public static IServiceCollection ConfigureFisher(this IServiceCollection services,
        Action<IServiceProvider, StoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddSingleton<IConfigureFisher>(new LambdaConfigureFisher(configure));
        return services;
    }

    /// <summary>
    ///     Contribute configuration to one secondary store's options from a lambda (fisher#70).
    /// </summary>
    /// <remarks>
    ///     The targeted form, reaching the store registered as <typeparamref name="T" /> and no other.
    ///     An untargeted <see cref="ConfigureFisher(IServiceCollection,Action{StoreOptions})" /> is about
    ///     the primary store, which is the one an application that never registers a second has.
    /// </remarks>
    public static IServiceCollection ConfigureFisher<T>(this IServiceCollection services,
        Action<StoreOptions> configure) where T : IDocumentStore
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        return services.ConfigureFisher<T>((_, options) => configure(options));
    }

    /// <inheritdoc cref="ConfigureFisher{T}(IServiceCollection,Action{StoreOptions})" />
    public static IServiceCollection ConfigureFisher<T>(this IServiceCollection services,
        Action<IServiceProvider, StoreOptions> configure) where T : IDocumentStore
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // Registered against both service types, which is what makes either resolution style find it —
        // see Configured for why there are two.
        var lambda = new LambdaConfigureFisher<T>(configure);

        services.AddSingleton<IConfigureFisher>(lambda);
        services.AddSingleton<IConfigureFisher<T>>(lambda);

        return services;
    }

    /// <summary>
    ///     Apply every matching <see cref="IConfigureFisher" /> to a store's options.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         After the registration lambda, so the lambda states the store's shape and these adjust it —
    ///         and filtered by store, because a contribution registered by one library reaching every store
    ///         in the application, including ones it has never heard of, is not configuration but a
    ///         surprise.
    ///     </para>
    ///     <para>
    ///         <b>Both registration styles are swept, and the second was silently ignored</b> (fisher#70).
    ///         Fisher resolves <see cref="IConfigureFisher" /> and filters by the contribution's own
    ///         interfaces; Polecat and Marten resolve the closed <c>IConfigure*&lt;T&gt;</c> directly, so
    ///         code ported from either registers against <see cref="IConfigureFisher{T}" /> — which
    ///         <c>GetServices&lt;IConfigureFisher&gt;()</c> does not return, because the container matches
    ///         on the service type a registration named rather than on what it implements. That is a
    ///         contribution that compiles, registers, and never runs. Both are swept and deduplicated by
    ///         reference, so registering against both service types (as
    ///         <see cref="ConfigureFisher{T}(IServiceCollection,Action{StoreOptions})" /> does) still
    ///         configures once.
    ///     </para>
    /// </remarks>
    private static StoreOptions Configured(IServiceProvider services, StoreOptions options, Type? forStore)
    {
        var applied = new HashSet<object>(ReferenceEqualityComparer.Instance);

        foreach (var configure in services.GetServices<IConfigureFisher>().Concat(TargetedAt(services, forStore)))
        {
            if (AppliesTo(configure, forStore) && applied.Add(configure))
            {
                configure.Configure(services, options);
            }
        }

        // fisher#141. After the contribution chain, not before: the host switch is the outer statement,
        // and applying it first would let a per-store instrumentation default clobber it. It only ever
        // adds, so a store that asked for extended tracking itself is unaffected either way.
        options.ReadJasperFxOptions(services.GetService<JasperFx.JasperFxOptions>());

        return options;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2055:MakeGenericType",
        Justification = "IConfigureFisher<T> is closed over a marker interface the caller supplied to "
            + "AddFisherStore<T> at registration time, so the trimmer already sees the instantiation.")]
    private static IEnumerable<IConfigureFisher> TargetedAt(IServiceProvider services, Type? forStore)
        => forStore is null
            ? []
            : services.GetServices(typeof(IConfigureFisher<>).MakeGenericType(forStore))
                .OfType<IConfigureFisher>();

    private static bool AppliesTo(IConfigureFisher configure, Type? forStore)
    {
        var targeted = configure.GetType().GetInterfaces()
            .Where(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IConfigureFisher<>))
            .Select(x => x.GetGenericArguments()[0])
            .ToArray();

        // An untargeted contribution is about the primary store, which is the one an application that
        // never registers a second has.
        return targeted.Length == 0 ? forStore is null : forStore is not null && targeted.Contains(forStore);
    }

    /// <summary>
    ///     The real store behind a marker proxy, for the interfaces the store implements explicitly.
    /// </summary>
    /// <remarks>
    ///     <see cref="DispatchProxy" /> implements the interfaces it was asked for and no others, so a
    ///     proxy over <c>IOtherStore</c> is not an <c>IEventStore</c> — the tooling surfaces are
    ///     implemented explicitly and are not on <see cref="IDocumentStore" />. Reaching through to the
    ///     wrapped store is what keeps a secondary store visible to a monitoring console.
    /// </remarks>
    private static object UnwrapForTooling(IDocumentStore store) => SecondaryStoreProxy.Unwrap(store);
}

/// <summary>
///     Which stores this container has registered, so two that would share a table space are caught
///     rather than silently sharing one (fisher#46).
/// </summary>
/// <remarks>
///     <para>
///         <b>This is the mistake multi-store registration makes easy, and it is silent.</b> Two stores
///         over one file with different <c>DatabaseSchemaName</c>s are correctly isolated — that is a
///         supported and useful shape. Two over one file with the <em>same</em> schema name share every
///         table, so each will happily read and clean the other's rows.
///     </para>
///     <para>
///         Scoped to the container rather than to the process, deliberately. Building two
///         <c>DocumentStore</c>s over one file by hand is something tests and migrations legitimately
///         do — and <c>applying_the_configuration_again_is_a_no_op</c> is exactly that. What this
///         refuses is registering two, which is never what anyone means.
///     </para>
/// </remarks>
internal sealed class FisherStoreRegistry
{
    private readonly Dictionary<string, string> _registered = new(StringComparer.Ordinal);

    internal void Register(Type marker, StoreOptions options)
    {
        var key = $"{options.ConnectionString}|{options.DatabaseSchemaName}";

        lock (_registered)
        {
            if (_registered.TryGetValue(key, out var existing))
            {
                throw new InvalidOperationException(
                    $"'{marker.Name}' and '{existing}' are registered over the same database file with the "
                    + $"same DatabaseSchemaName ('{options.DatabaseSchemaName}'), so they would share every "
                    + "table — each would read, write and clean the other's rows. Give one a different "
                    + "ConnectionString (a second file, which also gives it its own write lock) or a "
                    + "different DatabaseSchemaName (the same file, isolated by table prefix).");
            }

            _registered[key] = marker.Name;
        }
    }
}

/// <summary>
///     The opt-ins available after <c>AddFisherStore&lt;T&gt;</c> (fisher#46).
/// </summary>
/// <remarks>
///     The same three as the primary store's, each resolving <typeparamref name="T" /> rather than
///     <see cref="IDocumentStore" /> — so a secondary store applies its own schema, seeds its own data
///     and runs its own daemon, independently and with its own hosted services.
/// </remarks>
public sealed class FisherStoreConfigurationExpression<T> where T : class, IDocumentStore
{
    private readonly IServiceCollection _services;

    internal FisherStoreConfigurationExpression(IServiceCollection services) => _services = services;

    /// <summary>
    ///     The service collection <c>AddFisherStore&lt;T&gt;</c> was called against.
    /// </summary>
    /// <remarks>
    ///     Exposed so an integration package can keep registering against the same container after the
    ///     Fisher call — <c>services.AddFisher(...).IntegrateWithWolverine()</c> being the case that
    ///     asked for it. Marten's and Polecat's equivalent expressions both surface this; without it an
    ///     integration has to make the caller pass the collection a second time.
    /// </remarks>
    public IServiceCollection Services => _services;

    /// <inheritdoc cref="FisherConfigurationExpression.ApplyAllDatabaseChangesOnStartup" />
    public FisherStoreConfigurationExpression<T> ApplyAllDatabaseChangesOnStartup()
    {
        _services.AddSingleton<IHostedService>(sp
            => new FisherSchemaActivator(sp.GetRequiredService<T>()));

        return this;
    }

    /// <inheritdoc cref="FisherConfigurationExpression.SeedInitialDataOnStartup" />
    public FisherStoreConfigurationExpression<T> SeedInitialDataOnStartup()
    {
        _services.AddSingleton<IHostedService>(sp
            => new FisherInitialDataActivator(sp.GetRequiredService<T>()));

        return this;
    }

    /// <inheritdoc cref="FisherConfigurationExpression.AddAsyncDaemon" />
    /// <remarks>
    ///     One daemon per store, so two stores over two files project independently — which is the
    ///     shape that gets two concurrent writers out of SQLite. <see cref="DaemonMode.HotCold" /> stays
    ///     refused for each of them, for the reason fisher#20 documented: a Fisher store is a file, and
    ///     SQLite does not make one safe to share across nodes.
    /// </remarks>
    public FisherStoreConfigurationExpression<T> AddAsyncDaemon(DaemonMode mode = DaemonMode.Solo)
    {
        switch (mode)
        {
            case DaemonMode.Solo:
                // One instance, reachable two ways — as the hosted service that runs it and as the
                // coordinator application code resolves. Registering the hosted service on its own is
                // what left the daemon unreachable (fisher#138); registering them separately would run
                // two daemons over one file, which is two writers contending for one write lock.
                _services.AddSingleton<IProjectionCoordinator<T>>(sp => new FisherDaemonHostedService<T>(
                    sp.GetRequiredService<T>(),
                    sp.GetService<ILogger<FisherDaemonHostedService>>()));
                _services.AddSingleton<IHostedService>(sp =>
                    sp.GetRequiredService<IProjectionCoordinator<T>>());
                break;

            case DaemonMode.HotCold:
                throw new NotSupportedException(
                    "Fisher has no hot-cold daemon coordination, for a secondary store as for the primary "
                    + "one. Leadership failover requires several nodes sharing one database, and a Fisher "
                    + "store is a file. Use DaemonMode.Solo, or DaemonMode.ExternallyManaged.");

            case DaemonMode.Disabled:
            case DaemonMode.ExternallyManaged:
                break;
        }

        return this;
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
    ///     The service collection <c>AddFisher</c> was called against.
    /// </summary>
    /// <remarks>
    ///     Exposed so an integration package can keep registering against the same container after the
    ///     Fisher call — <c>services.AddFisher(...).IntegrateWithWolverine()</c> being the case that
    ///     asked for it. Marten's and Polecat's equivalent expressions both surface this; without it an
    ///     integration has to make the caller pass the collection a second time.
    /// </remarks>
    public IServiceCollection Services => _services;

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
                // See the ancillary overload: one instance, registered as the coordinator and resolved
                // from there as the hosted service, so the daemon that runs is the daemon you reach.
                _services.AddSingleton<IProjectionCoordinator, FisherDaemonHostedService>();
                _services.AddSingleton<IHostedService>(sp =>
                    sp.GetRequiredService<IProjectionCoordinator>());
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
///         <b>One daemon per database</b> — one under every tenancy but database-per-tenant, where it is
///         one per tenant file (fisher#57). A second daemon over the <em>same</em> file would mean two
///         writers contending for one write lock and two shards replaying the same range, which is why
///         this holds the instances rather than building them per call; N daemons over N files is the
///         opposite situation, and they do not contend at all.
///     </para>
///     <para>
///         <b>It is also the store's <see cref="IProjectionCoordinator" />, which is how application
///         code reaches the running daemon</b> (fisher#138). Before that it held its daemons privately
///         and implemented nothing else, so neither documented route worked — the service was not
///         registered, and the <c>GetServices&lt;IHostedService&gt;().OfType&lt;IProjectionCoordinator&gt;()</c>
///         fallback found nothing. Marten and Polecat have registered the JasperFx interface since
///         jasperfx#430, so store-agnostic code could do daemon operations against them and not
///         against Fisher. Fisher registers <em>that</em> interface rather than a Fisher-local
///         sub-interface: the siblings' local ones add no members and are historical.
///     </para>
///     <para>
///         <b>No leadership loop, deliberately.</b> Polecat and Marten close over JasperFx's
///         <c>ProjectionCoordinatorBase</c>, which exists to elect a leader across nodes; Fisher
///         refuses <see cref="DaemonMode.HotCold" /> outright, because a Fisher store is a file. What
///         is left of a coordinator here is the daemon cache and the pause/resume pair, both of which
///         this class already had in all but name.
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
internal class FisherDaemonHostedService : IProjectionCoordinator, IDisposable
{
    private readonly IDocumentStore _store;
    private readonly ILogger<FisherDaemonHostedService> _logger;
    private readonly List<IProjectionDaemon> _daemons = [];
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _polling;
    private Task? _poller;

    public FisherDaemonHostedService(IDocumentStore store, ILogger<FisherDaemonHostedService>? logger = null)
    {
        _store = store;
        _logger = logger ?? NullLogger<FisherDaemonHostedService>.Instance;
    }

    /// <summary>
    ///     How often to look for tenant databases that have appeared since startup (fisher#58).
    /// </summary>
    /// <remarks>
    ///     Polling rather than a notification, because the set of tenants belongs to the application and
    ///     Fisher is not told when it changes — an <c>ITenantSource</c> is asked, never pushed. A minute
    ///     is chosen so a new tenant's projections start without anyone waiting on them; a tenant's
    ///     <em>sessions</em> work immediately either way, because resolution does not go through here.
    ///     Only runs when the store's tenancy can actually gain a database.
    /// </remarks>
    public static TimeSpan TenantPollingInterval { get; set; } = TimeSpan.FromMinutes(1);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Published so Advanced.ResetAllDataAsync can pause around a wipe (fisher#138). Set on every
        // start rather than in the constructor, because it is a claim that daemons are running, and
        // unwrapped because an ancillary store arrives here as its marker proxy.
        if (Concrete() is { } starting) starting.RunningDaemons = this;

        // Drain first, so a second StartAsync is equivalent to PauseAsync followed by StartAsync —
        // the property JasperFx's own coordinator base establishes, and the one ResumeAsync rests on.
        await StopPollingAsync().ConfigureAwait(false);

        // Agents on daemons this instance already holds, which is what a resume after PauseAsync
        // needs: StartAnyNewDaemonsAsync skips a database already in _running, so on its own it would
        // resume nothing at all.
        foreach (var daemon in _daemons)
        {
            await daemon.StartAllAsync().ConfigureAwait(false);
        }

        await StartAnyNewDaemonsAsync().ConfigureAwait(false);

        if (_store.Tenancy.Cardinality != DatabaseCardinality.DynamicMultiple)
        {
            return;
        }

        _polling = new CancellationTokenSource();
        _poller = PollForNewTenantsAsync(_polling.Token);
    }

    /// <inheritdoc />
    public IProjectionDaemon DaemonForMainDatabase()
        => _daemons.Count > 0
            ? _daemons[0]
            : throw new InvalidOperationException(
                "No projection daemon is running. AddAsyncDaemon() registers this coordinator as a "
                + "hosted service, so its daemons exist only once the host has started — resolve it "
                + "after StartAsync rather than while the container is still being built.");

    /// <inheritdoc />
    public async ValueTask<IProjectionDaemon> DaemonForDatabase(string databaseIdentifier)
    {
        if (TryFindDaemon(databaseIdentifier, out var daemon)) return daemon;

        // A tenant database that has appeared since the last poll has no daemon yet, and the
        // interface's own contract expects an implementation to go and look. Only worth a sweep under
        // a tenancy that can gain a database; anywhere else the answer cannot change.
        if (_store.Tenancy.Cardinality == DatabaseCardinality.DynamicMultiple)
        {
            await StartAnyNewDaemonsAsync().ConfigureAwait(false);

            if (TryFindDaemon(databaseIdentifier, out daemon)) return daemon;
        }

        throw new ArgumentOutOfRangeException(nameof(databaseIdentifier),
            $"No projection daemon is running for database '{databaseIdentifier}'. Running: "
            + $"{string.Join(", ", _running)}.");
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<IProjectionDaemon>> AllDaemonsAsync()
        => new((IReadOnlyList<IProjectionDaemon>)_daemons.ToArray());

    /// <summary>
    ///     Stop every agent without disposing anything, so <see cref="ResumeAsync" /> can restart them.
    /// </summary>
    /// <remarks>
    ///     The tenant poller stops too, and that is the half a caller would not think to ask for: it
    ///     builds and starts daemons of its own, so leaving it running would let a tenant appearing
    ///     mid-pause quietly begin projecting. "Paused" has to mean paused for tenants that do not
    ///     exist yet.
    /// </remarks>
    public async Task PauseAsync()
    {
        await StopPollingAsync().ConfigureAwait(false);

        foreach (var daemon in _daemons)
        {
            try
            {
                await daemon.StopAllAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException e)
            {
                // Benign during shutdown, where a second pause can fan out over daemons the first
                // already disposed. Same reading JasperFx's own coordinator base takes.
                _logger.LogDebug(e, "Fisher projection daemon was already disposed while pausing.");
            }
        }
    }

    /// <inheritdoc />
    public Task ResumeAsync() => StartAsync(CancellationToken.None);

    /// <summary>
    ///     The concrete store behind whatever this service was handed — an ancillary store arrives as
    ///     the <see cref="DispatchProxy" /> over its marker interface, which is not a
    ///     <see cref="DocumentStore" />.
    /// </summary>
    private DocumentStore? Concrete() => SecondaryStoreProxy.Unwrap(_store) as DocumentStore;

    private bool TryFindDaemon(string databaseIdentifier, out IProjectionDaemon daemon)
    {
        foreach (var candidate in _daemons)
        {
            if (string.Equals(((Events.Daemon.FisherProjectionDaemon)candidate).Database.Identifier,
                    databaseIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                daemon = candidate;
                return true;
            }
        }

        daemon = null!;
        return false;
    }

    private async Task StopPollingAsync()
    {
        if (_polling is not null)
        {
            await _polling.CancelAsync().ConfigureAwait(false);
        }

        if (_poller is not null)
        {
            try
            {
                await _poller.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: that is how the loop ends.
            }
        }

        _polling?.Dispose();
        _polling = null;
        _poller = null;
    }

    private async Task StartAnyNewDaemonsAsync()
    {
        foreach (var daemon in await _store.BuildProjectionDaemonsAsync(_logger).ConfigureAwait(false))
        {
            // Keyed by database, because starting a second daemon over one file is two writers
            // contending for one write lock and two shards replaying the same range. The database is
            // on the concrete daemon rather than on IProjectionDaemon, hence the cast.
            var identifier = ((Events.Daemon.FisherProjectionDaemon)daemon).Database.Identifier;

            if (!_running.Add(identifier))
            {
                daemon.Dispose();
                continue;
            }

            _daemons.Add(daemon);
            await daemon.StartAllAsync().ConfigureAwait(false);
        }
    }

    private async Task PollForNewTenantsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TenantPollingInterval, token).ConfigureAwait(false);
                await StartAnyNewDaemonsAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                // A tenant whose database cannot be reached must not take down the daemons already
                // running for every other tenant. Logged and retried on the next tick.
                _logger.LogError(e, "Fisher could not start projections for a newly discovered tenant.");
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Withdrawn before the agents stop, so a wipe racing shutdown does not pause a coordinator
        // that is on its way out and then resume it.
        if (Concrete() is { } stopping) stopping.RunningDaemons = null;

        await PauseAsync().ConfigureAwait(false);
    }

    /// <inheritdoc cref="Storage.FisherDatabase.Dispose" />
    public void Dispose()
    {
        _polling?.Dispose();

        foreach (var daemon in _daemons)
        {
            daemon.Dispose();
        }

        _daemons.Clear();
        _running.Clear();
    }
}

/// <summary>
///     The same coordinator, addressable by an ancillary store's marker interface.
/// </summary>
/// <remarks>
///     One non-generic <see cref="IProjectionCoordinator" /> can only mean one store, so a second store
///     needs a second registration to be reachable at all — which is what JasperFx's marker variant is
///     for. The primary store keeps the unmarked registration; a marker-typed store gets this and not
///     that, so resolving <see cref="IProjectionCoordinator" /> is never ambiguous about which store's
///     daemons it hands back.
/// </remarks>
internal sealed class FisherDaemonHostedService<T> : FisherDaemonHostedService, IProjectionCoordinator<T>
    where T : class
{
    public FisherDaemonHostedService(IDocumentStore store, ILogger<FisherDaemonHostedService>? logger = null)
        : base(store, logger)
    {
    }
}
