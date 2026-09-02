using System.Reflection;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#142 — Fisher surfaces JasperFx's application-assembly-reuse warning, which JasperFx
///     detects and explicitly leaves to its consumers to say out loud.
/// </summary>
/// <remarks>
///     <para>
///         The condition (GH-3521) is a host adopting an application assembly pinned by an <em>earlier</em>
///         host in the same process. It is order-dependent and presents as a type this host registered
///         simply not being discovered — so a warning nobody prints is a warning that does not exist.
///     </para>
///     <para>
///         <b>The warning value is planted by reflection, and that is the right amount of test.</b>
///         <c>JasperFxOptions.ApplicationAssemblyReuseWarning</c> has an internal setter, and reproducing
///         the real condition needs two hosts from two assemblies in one process in a specific order —
///         which under xUnit's parallel collections would be an intermittent rather than a test. What is
///         Fisher's to get right is narrow and entirely covered here: read it, buffer it, log it once.
///         Detecting it is JasperFx's, and is tested there.
///     </para>
/// </remarks>
public class application_assembly_reuse_warning : IAsyncLifetime
{
    private const string Warning = "JasperFx adopted application assembly 'Other.Assembly' for code generation";

    private readonly TemporaryDatabase _primary = TemporaryDatabase.Create("assembly-reuse-primary");
    private readonly TemporaryDatabase _secondary = TemporaryDatabase.Create("assembly-reuse-secondary");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _primary.Dispose();
        _secondary.Dispose();

        return ValueTask.CompletedTask;
    }

    private static void Plant(JasperFxOptions options, string warning)
        => typeof(JasperFxOptions)
            .GetProperty(nameof(JasperFxOptions.ApplicationAssemblyReuseWarning),
                BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(options, warning);

    private static ServiceProvider ProviderWith(Action<IServiceCollection> configure, RecordingLoggerProvider log)
    {
        var services = new ServiceCollection();
        services.AddLogging(x => x.AddProvider(log).SetMinimumLevel(LogLevel.Debug));
        configure(services);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void the_warning_is_logged_when_the_host_carries_one()
    {
        var log = new RecordingLoggerProvider();

        using var provider = ProviderWith(services =>
        {
            services.AddJasperFx(o => Plant(o, Warning));
            services.AddFisher(options => options.ConnectionString = _primary.ConnectionString);
        }, log);

        provider.GetRequiredService<IDocumentStore>();

        log.Warnings.ShouldContain(x => x.Contains(Warning, StringComparison.Ordinal));
    }

    [Fact]
    public void nothing_is_logged_when_the_host_carries_no_warning()
    {
        var log = new RecordingLoggerProvider();

        using var provider = ProviderWith(services =>
        {
            services.AddJasperFx();
            services.AddFisher(options => options.ConnectionString = _primary.ConnectionString);
        }, log);

        provider.GetRequiredService<IDocumentStore>();

        log.Warnings.ShouldBeEmpty();
    }

    /// <summary>
    ///     The condition belongs to the host, not to a store, so several stores in one container must not
    ///     each repeat the same four-sentence warning.
    /// </summary>
    [Fact]
    public void several_stores_in_one_container_warn_once()
    {
        var log = new RecordingLoggerProvider();

        using var provider = ProviderWith(services =>
        {
            services.AddJasperFx(o => Plant(o, Warning));
            services.AddFisher(options => options.ConnectionString = _primary.ConnectionString);
            services.AddFisherStore<IReuseWarningStore>(options =>
                options.ConnectionString = _secondary.ConnectionString);
        }, log);

        provider.GetRequiredService<IDocumentStore>();
        provider.GetRequiredService<IReuseWarningStore>();

        log.Warnings.Count(x => x.Contains(Warning, StringComparison.Ordinal)).ShouldBe(1);
    }

    /// <summary>
    ///     And the other half of that rule, which matters more than the noise it saves: the warning exists
    ///     <em>because</em> a second host started in this process, so a process-wide "already said it"
    ///     flag would silence it for exactly the host that needs to hear it. Dedupe is per container.
    /// </summary>
    [Fact]
    public void a_second_host_in_the_same_process_gets_its_own_warning()
    {
        var first = new RecordingLoggerProvider();
        var second = new RecordingLoggerProvider();

        using (var provider = ProviderWith(services =>
               {
                   services.AddJasperFx(o => Plant(o, Warning));
                   services.AddFisher(options => options.ConnectionString = _primary.ConnectionString);
               }, first))
        {
            provider.GetRequiredService<IDocumentStore>();
        }

        using (var provider = ProviderWith(services =>
               {
                   services.AddJasperFx(o => Plant(o, Warning));
                   services.AddFisher(options => options.ConnectionString = _secondary.ConnectionString);
               }, second))
        {
            provider.GetRequiredService<IDocumentStore>();
        }

        first.Warnings.ShouldContain(x => x.Contains(Warning, StringComparison.Ordinal));
        second.Warnings.ShouldContain(x => x.Contains(Warning, StringComparison.Ordinal));
    }

    /// <summary>
    ///     A host with no logging registered is the ordinary case for a console application, and building
    ///     a store must not become conditional on one.
    /// </summary>
    [Fact]
    public void a_container_without_logging_still_builds_a_store()
    {
        var services = new ServiceCollection();
        services.AddJasperFx(o => Plant(o, Warning));
        services.AddFisher(options => options.ConnectionString = _primary.ConnectionString);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IDocumentStore>().ShouldNotBeNull();
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _warnings = [];

        public List<string> Warnings
        {
            get
            {
                lock (_warnings) return [.._warnings];
            }
        }

        public ILogger CreateLogger(string categoryName) => new Recording(_warnings);

        public void Dispose()
        {
        }

        private sealed class Recording : ILogger
        {
            private readonly List<string> _warnings;

            public Recording(List<string> warnings) => _warnings = warnings;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel != LogLevel.Warning) return;

                lock (_warnings) _warnings.Add(formatter(state, exception));
            }
        }
    }
}

public interface IReuseWarningStore : IDocumentStore;
