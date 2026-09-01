using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#141 — the host-level <see cref="JasperFxOptions" /> reaches every Fisher store the
///     container builds.
/// </summary>
/// <remarks>
///     <para>
///         <c>JasperFxOptions.EnableAdvancedTracking</c> is documented as telling <em>all</em> Critter
///         Stack tools to opt into advanced tracking, and is the one switch a CritterWatch host throws.
///         Fisher had <c>Events.EnableExtendedProgressionTracking</c> and never read <c>JasperFxOptions</c> at
///         all, so the same host configuration lit up Marten and Polecat and silently did nothing here
///         — and a console then shows no per-shard state for those stores with nothing to say why. The
///         absence of monitoring data is indistinguishable from having none to report, which is the
///         shape that makes this worth a test rather than a line in the docs.
///     </para>
///     <para>
///         Fisher reads it in one place, where the siblings each need two: every registration path goes
///         through <c>FisherServiceCollectionExtensions.Configured</c>, primary and ancillary alike, so
///         "and the ancillary stores too" holds by construction. The ancillary test below is what keeps
///         that true if the paths ever diverge.
///     </para>
/// </remarks>
public class host_level_jasperfx_options : IAsyncLifetime
{
    private readonly TemporaryDatabase _primary = TemporaryDatabase.Create("jasperfx-options-primary");
    private readonly TemporaryDatabase _secondary = TemporaryDatabase.Create("jasperfx-options-secondary");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _primary.Dispose();
        _secondary.Dispose();

        return ValueTask.CompletedTask;
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private Task<IHost> HostWith(Action<IServiceCollection> configure)
        => Host.CreateDefaultBuilder().ConfigureServices(configure).StartAsync(Token);

    [Fact]
    public async Task advanced_tracking_reaches_the_primary_store()
    {
        using var host = await HostWith(services =>
        {
            services.AddJasperFx(o => o.EnableAdvancedTracking = true);
            services.AddFisher(options => options.ConnectionString = _primary.ConnectionString);
        });

        host.Services.GetRequiredService<IDocumentStore>()
            .Options.Events.EnableExtendedProgressionTracking.ShouldBeTrue();

        await host.StopAsync(Token);
    }

    /// <summary>
    ///     The half a monitoring console actually notices, since a modular monolith's stores are the
    ///     ancillary ones. Marten applies its equivalent to both explicitly; here they share a code path,
    ///     and this is what says so.
    /// </summary>
    [Fact]
    public async Task advanced_tracking_reaches_an_ancillary_store()
    {
        using var host = await HostWith(services =>
        {
            services.AddJasperFx(o => o.EnableAdvancedTracking = true);
            services.AddFisher(options => options.ConnectionString = _primary.ConnectionString);
            services.AddFisherStore<IAdvancedTrackingStore>(options =>
                options.ConnectionString = _secondary.ConnectionString);
        });

        host.Services.GetRequiredService<IDocumentStore>()
            .Options.Events.EnableExtendedProgressionTracking.ShouldBeTrue();

        host.Services.GetRequiredService<IAdvancedTrackingStore>()
            .Options.Events.EnableExtendedProgressionTracking.ShouldBeTrue();

        await host.StopAsync(Token);
    }

    [Fact]
    public async Task advanced_tracking_off_leaves_the_store_at_its_own_default()
    {
        using var host = await HostWith(services =>
        {
            services.AddJasperFx();
            services.AddFisher(options => options.ConnectionString = _primary.ConnectionString);
        });

        host.Services.GetRequiredService<IDocumentStore>()
            .Options.Events.EnableExtendedProgressionTracking.ShouldBeFalse();

        await host.StopAsync(Token);
    }

    /// <summary>
    ///     The host switch only ever adds. A false <c>EnableAdvancedTracking</c> is the default rather
    ///     than a statement, so reading it must not turn <em>off</em> a store that asked for extended
    ///     tracking in its own configuration — which is what a plain assignment would do.
    /// </summary>
    [Fact]
    public async Task a_store_that_opted_in_itself_is_not_switched_off_by_a_quiet_host()
    {
        using var host = await HostWith(services =>
        {
            services.AddJasperFx();
            services.AddFisher(options =>
            {
                options.ConnectionString = _primary.ConnectionString;
                options.Events.EnableExtendedProgressionTracking = true;
            });
        });

        host.Services.GetRequiredService<IDocumentStore>()
            .Options.Events.EnableExtendedProgressionTracking.ShouldBeTrue();

        await host.StopAsync(Token);
    }

    /// <summary>
    ///     Where the read sits in the registration is a decision, not an accident: it runs <b>after</b>
    ///     the <c>IConfigureFisher</c> chain, so a per-store contribution cannot clobber the host's
    ///     opt-in. Moving the call above the loop makes this fail and nothing else.
    /// </summary>
    [Fact]
    public async Task the_host_opt_in_outranks_a_store_level_contribution()
    {
        using var host = await HostWith(services =>
        {
            services.AddJasperFx(o => o.EnableAdvancedTracking = true);
            services.AddFisher(options => options.ConnectionString = _primary.ConnectionString);
            services.ConfigureFisher(options => options.Events.EnableExtendedProgressionTracking = false);
        });

        host.Services.GetRequiredService<IDocumentStore>()
            .Options.Events.EnableExtendedProgressionTracking.ShouldBeTrue();

        await host.StopAsync(Token);
    }

    /// <summary>
    ///     A host that never calls <c>AddJasperFx</c> registers no options at all, which is the ordinary
    ///     case for an application using Fisher on its own. The read has to tolerate the null rather than
    ///     making JasperFx's host integration a prerequisite for building a store.
    /// </summary>
    [Fact]
    public async Task a_host_with_no_jasperfx_options_registered_still_builds_a_store()
    {
        using var host = await HostWith(services =>
            services.AddFisher(options => options.ConnectionString = _primary.ConnectionString));

        host.Services.GetRequiredService<IDocumentStore>()
            .Options.Events.EnableExtendedProgressionTracking.ShouldBeFalse();

        await host.StopAsync(Token);
    }
}

public interface IAdvancedTrackingStore : IDocumentStore;
