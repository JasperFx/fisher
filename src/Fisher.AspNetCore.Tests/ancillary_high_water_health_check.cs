using Fisher.AspNetCore.Daemon;
using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Fisher.AspNetCore.Tests;

public interface ITackleStore : IDocumentStore;

/// <summary>
///     fisher#492, from marten#5705. A store from <c>AddFisherStore&lt;T&gt;()</c> has its own daemon,
///     usually over its own file, and the check could only ever see the primary store.
/// </summary>
public class ancillary_high_water_health_check : IAsyncLifetime
{
    private readonly TemporaryDatabase _primary = TemporaryDatabase.Create("health-primary");
    private readonly TemporaryDatabase _ancillary = TemporaryDatabase.Create("health-ancillary");
    private ServiceProvider _services = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void Configure(StoreOptions options, string connectionString)
    {
        options.ConnectionString = connectionString;
        options.AutoCreateSchemaObjects = AutoCreate.All;
        options.Projections.Snapshot<high_water_health_check.Catch>(SnapshotLifecycle.Async);
        options.DaemonSettings.AsyncMode = DaemonMode.Solo;
    }

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFisher(options => Configure(options, _primary.ConnectionString));
        services.AddFisherStore<ITackleStore>(options => Configure(options, _ancillary.ConnectionString));

        services.AddHealthChecks()
            .AddFisherHighWaterHealthCheck(staleThreshold: TimeSpan.FromMinutes(1))
            .AddFisherHighWaterHealthCheck<ITackleStore>(staleThreshold: TimeSpan.FromMinutes(1));

        _services = services.BuildServiceProvider();

        await _services.GetRequiredService<IDocumentStore>().ApplyAllConfiguredChangesToDatabaseAsync(Token);
        await _services.GetRequiredService<ITackleStore>().ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        _primary.Dispose();
        _ancillary.Dispose();
    }

    /// <summary>A high-water row whose agent last cycled long ago, in one file only.</summary>
    private async Task StallTheDaemonInAsync(TemporaryDatabase database)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "insert into fi_event_progression (name, last_seq_id, last_updated) "
            + "values ('HighWaterMark', 1, '2020-01-01T00:00:00.000Z')";

        await command.ExecuteNonQueryAsync(Token);
    }

    private Task<HealthReport> RunAsync()
        => _services.GetRequiredService<HealthCheckService>().CheckHealthAsync(Token);

    private const string PrimaryCheck = nameof(HighWaterHealthCheckExtensions.HighWaterHealthCheck);
    private const string AncillaryCheck = PrimaryCheck + ":" + nameof(ITackleStore);

    [Fact]
    public async Task each_store_gets_a_check_of_its_own()
    {
        var report = await RunAsync();

        report.Entries.Keys.ShouldBe([PrimaryCheck, AncillaryCheck], ignoreOrder: true);
        report.Status.ShouldBe(HealthStatus.Healthy);
    }

    /// <summary>Stalled in one file only, so each check has to be reading its own.</summary>
    [Fact]
    public async Task the_ancillary_check_reads_the_ancillary_store()
    {
        await StallTheDaemonInAsync(_ancillary);

        var report = await RunAsync();

        report.Entries[AncillaryCheck].Status.ShouldBe(HealthStatus.Unhealthy, report.Entries[AncillaryCheck].Description);
        report.Entries[PrimaryCheck].Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task the_primary_check_still_reads_the_primary_store()
    {
        await StallTheDaemonInAsync(_primary);

        var report = await RunAsync();

        report.Entries[PrimaryCheck].Status.ShouldBe(HealthStatus.Unhealthy);
        report.Entries[AncillaryCheck].Status.ShouldBe(HealthStatus.Healthy);
    }
}
