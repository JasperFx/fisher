using Fisher.Storage;
using JasperFx;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#487, from marten#5629. A revocation used to reach only the process that made it, because
///     <c>DatabaseFor</c> answers from its cache before it asks a source. Two stores over one registry
///     stand in for two processes.
/// </summary>
public class tenant_registry_across_processes : IAsyncLifetime
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fisher-registry-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly TemporaryDatabase _registry = TemporaryDatabase.Create("registry-processes");
    private readonly List<DocumentStore> _stores = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }

        _registry.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A tenant file still held by a pooled connection; the directory is under temp.
        }
    }

    private string ConnectionStringFor(string tenantId)
        => new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, $"{tenantId}.db") }.ToString();

    private (DocumentStore Store, MasterTableTenantSource Source) Process()
    {
        MasterTableTenantSource? source = null;

        var store = DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Sighting>();

            source = options.MultiTenantedDatabasesInRegistry(x =>
            {
                x.ConnectionString = _registry.ConnectionString;
                x.SeedDatabases.RegisterDefault(ConnectionStringFor("default"));
            });
        });

        _stores.Add(store);

        return (store, source!);
    }

    private static async Task WriteAsync(DocumentStore store, string tenantId)
    {
        await using var session = store.LightweightSession(tenantId);
        session.Store(new Sighting { Id = Guid.NewGuid() });
        await session.SaveChangesAsync(Token);
    }

    private async Task<((DocumentStore Store, MasterTableTenantSource Source) First,
        (DocumentStore Store, MasterTableTenantSource Source) Second)> BothServingAcmeAsync()
    {
        var first = Process();
        var second = Process();

        await first.Source.AddTenantAsync("acme", ConnectionStringFor("acme"), Token);
        await second.Source.RefreshAsync(Token);
        await WriteAsync(second.Store, "acme");

        return (first, second);
    }

    [Fact]
    public async Task a_tenant_suspended_elsewhere_is_refused_after_a_refresh()
    {
        var (first, second) = await BothServingAcmeAsync();

        await first.Source.SuspendTenantAsync("acme", Token);
        await second.Source.RefreshAsync(Token);

        var ex = Should.Throw<DisabledTenantException>(() => second.Store.LightweightSession("acme"));
        ex.Message.ShouldContain("acme");
    }

    /// <summary>Through the tenancy, which is what the daemon's poll calls.</summary>
    [Fact]
    public async Task the_tenancy_refresh_reaches_the_same_answer()
    {
        var (first, second) = await BothServingAcmeAsync();

        await first.Source.SuspendTenantAsync("acme", Token);
        await ((DynamicTenancy)second.Store.Tenancy).RefreshAsync(Token);

        Should.Throw<DisabledTenantException>(() => second.Store.LightweightSession("acme"));
        second.Store.Tenancy.AllDatabases().ShouldNotContain(x => x.TenantId == "acme");
    }

    [Fact]
    public async Task a_tenant_forgotten_elsewhere_is_unknown_after_a_refresh()
    {
        var (first, second) = await BothServingAcmeAsync();

        await first.Source.ForgetTenantAsync("acme", Token);
        await ((DynamicTenancy)second.Store.Tenancy).RefreshAsync(Token);

        Should.Throw<UnknownTenantException>(() => second.Store.LightweightSession("acme"));
        second.Store.Tenancy.AllDatabases().ShouldNotContain(x => x.TenantId == "acme");
    }

    [Fact]
    public async Task a_tenant_resumed_elsewhere_resolves_again()
    {
        var (first, second) = await BothServingAcmeAsync();

        await first.Source.SuspendTenantAsync("acme", Token);
        await second.Source.RefreshAsync(Token);

        await first.Source.ResumeTenantAsync("acme", Token);
        await second.Source.RefreshAsync(Token);

        await WriteAsync(second.Store, "acme");
    }

    [Fact]
    public async Task a_tenant_re_added_elsewhere_resolves_again()
    {
        var (first, second) = await BothServingAcmeAsync();

        await first.Source.ForgetTenantAsync("acme", Token);
        await second.Source.RefreshAsync(Token);

        await first.Source.AddTenantAsync("acme", ConnectionStringFor("acme"), Token);
        await second.Source.RefreshAsync(Token);

        await WriteAsync(second.Store, "acme");
    }

    /// <summary>The marker a removal leaves behind must not outlast this process adding the tenant back.</summary>
    [Fact]
    public async Task adding_a_removed_tenant_here_needs_no_refresh()
    {
        var (first, second) = await BothServingAcmeAsync();

        await first.Source.ForgetTenantAsync("acme", Token);
        await second.Source.RefreshAsync(Token);

        await second.Source.AddTenantAsync("acme", ConnectionStringFor("acme"), Token);

        await WriteAsync(second.Store, "acme");
    }

    /// <summary>A seeded tenant is in the table too, so it must not read as removed.</summary>
    [Fact]
    public async Task the_seeded_default_tenant_survives_a_refresh()
    {
        var (_, second) = await BothServingAcmeAsync();

        await second.Source.RefreshAsync(Token);

        await WriteAsync(second.Store, StorageConstants.DefaultTenantId);
    }

    /// <summary>The file stays. Fisher deprovisions and never deletes.</summary>
    [Fact]
    public async Task the_tenants_file_is_left_alone()
    {
        var (first, second) = await BothServingAcmeAsync();

        await first.Source.ForgetTenantAsync("acme", Token);
        await second.Source.RefreshAsync(Token);

        File.Exists(Path.Combine(_directory, "acme.db")).ShouldBeTrue();
    }
}
