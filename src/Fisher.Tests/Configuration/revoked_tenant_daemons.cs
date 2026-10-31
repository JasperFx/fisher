using Fisher.Events.Daemon;
using Fisher.Storage;
using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#487. A revoked tenant loses its database, so a daemon left running over it would be
///     projecting through a disposed data source.
/// </summary>
public class revoked_tenant_daemons : IAsyncLifetime
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fisher-revoked-" + Guid.NewGuid().ToString("n")[..8]);

    private InMemoryTenantSource _source = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string ConnectionStringFor(string tenantId)
        => new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, $"{tenantId}.db") }.ToString();

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);

        _source = new InMemoryTenantSource()
            .Add(StorageConstants.DefaultTenantId, ConnectionStringFor("default"))
            .Add("north", ConnectionStringFor("north"))
            .Add("south", ConnectionStringFor("south"));

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A tenant file still held by a pooled connection; the directory is under temp.
        }

        return ValueTask.CompletedTask;
    }

    private Task<IHost> StartedHost()
        => Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddFisher(options =>
                {
                    options.AutoCreateSchemaObjects = AutoCreate.All;
                    options.MultiTenantedDatabasesFrom(_source);
                    options.Projections.Snapshot<SightingTally>(SnapshotLifecycle.Async);
                })
                .AddAsyncDaemon())
            .StartAsync(Token);

    private static async Task<string[]> TenantsWithADaemonAsync(IProjectionCoordinator coordinator)
        => (await coordinator.AllDaemonsAsync())
            .Select(x => ((FisherDatabase)((FisherProjectionDaemon)x).Database).TenantId!)
            .ToArray();

    [Fact]
    public async Task a_suspended_tenant_loses_its_daemon_and_a_resumed_one_gets_a_new_one()
    {
        using var host = await StartedHost();

        try
        {
            var coordinator = host.Services.GetRequiredService<IProjectionCoordinator>();

            (await TenantsWithADaemonAsync(coordinator)).ShouldContain("south");

            _source.SetActive("south", false);
            await coordinator.ResumeAsync();

            var running = await TenantsWithADaemonAsync(coordinator);
            running.ShouldNotContain("south");
            running.ShouldContain("north");

            _source.SetActive("south", true);
            await coordinator.ResumeAsync();

            (await TenantsWithADaemonAsync(coordinator)).ShouldContain("south");
        }
        finally
        {
            await host.StopAsync(Token);
        }
    }
}
