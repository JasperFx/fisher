using Fisher.Storage;
using JasperFx;
using JasperFx.Events;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#437 — <c>IEventStore.FindDatabaseForTenantAsync</c> answers through Fisher's own tenancy.
/// </summary>
/// <remarks>
///     <para>
///         JasperFx's default answers only for a single-database store, so a database-per-tenant store
///         said null and Bobcat's <c>AfterAsyncDaemonsCatchUpAsync(tenantId)</c> waited on every
///         tenant's file. The override names the tenant's own file.
///     </para>
///     <para>
///         <b>The unknown-tenant facts assert the absence of the file as well as the null</b>, for the
///         reason fisher#390's facts do: under the directory convention resolving a tenant is enough to
///         create it, and a lookup that provisioned before answering would still return something.
///     </para>
/// </remarks>
public class finding_the_database_for_a_tenant : IAsyncLifetime
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fisher-find-tenant-" + Guid.NewGuid().ToString("n")[..8]);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_directory))
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A tenant file still held by a pooled connection; the directory is under temp.
            }
        }

        return ValueTask.CompletedTask;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private DocumentStore StaticTenants()
        => DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Sighting>();
            options.MultiTenantedDatabases(databases => databases.InDirectory(_directory).AddTenants("north", "south"));
        });

    private DocumentStore DirectoryTenants()
        => DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Sighting>();
            options.MultiTenantedDatabasesInDirectory(_directory);
        });

    // ---- database-per-tenant ----

    [Fact]
    public async Task a_configured_tenant_resolves_to_its_own_database()
    {
        await using var store = StaticTenants();
        IEventStore events = store;

        var north = await events.FindDatabaseForTenantAsync("north", Token);

        north.ShouldBeSameAs(store.Tenancy.DatabaseFor("north"));
        ((FisherDatabase)north!).TenantId.ShouldBe("north");

        // Resolved case-insensitively, as every other tenant lookup is.
        (await events.FindDatabaseForTenantAsync("NORTH", Token)).ShouldBeSameAs(north);
        (await events.FindDatabaseForTenantAsync("south", Token)).ShouldNotBeSameAs(north);
    }

    [Fact]
    public async Task an_unconfigured_tenant_is_null()
    {
        await using var store = StaticTenants();
        IEventStore events = store;

        (await events.FindDatabaseForTenantAsync("never-seen", Token)).ShouldBeNull();

        File.Exists(Path.Combine(_directory, "never-seen.db")).ShouldBeFalse();
    }

    /// <remarks>
    ///     The case that needs the lookup-only path: the directory convention answers <c>DatabaseFor</c>
    ///     for any valid id, so resolving through it would have created <c>never-seen.db</c>.
    /// </remarks>
    [Fact]
    public async Task an_unknown_runtime_tenant_is_null_and_provisions_nothing()
    {
        await using var store = DirectoryTenants();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        IEventStore events = store;

        (await events.FindDatabaseForTenantAsync("never-seen", Token)).ShouldBeNull();

        File.Exists(Path.Combine(_directory, "never-seen.db")).ShouldBeFalse();
        store.Tenancy.AllDatabases().Select(x => x.TenantId).ShouldNotContain("never-seen");
    }

    /// <remarks>
    ///     A second store instance, so the tenant is found by its file rather than from the cache of
    ///     the store that created it.
    /// </remarks>
    [Fact]
    public async Task a_runtime_tenant_whose_file_exists_resolves_to_it()
    {
        await using (var writer = DirectoryTenants())
        {
            await using var session = writer.LightweightSession("existing");
            session.Events.StartStream(Guid.NewGuid(), new SightingLogged("Manta"));
            await session.SaveChangesAsync(Token);
        }

        await using var store = DirectoryTenants();
        IEventStore events = store;

        var database = await events.FindDatabaseForTenantAsync("existing", Token);

        database.ShouldNotBeNull();
        ((FisherDatabase)database).TenantId.ShouldBe("existing");
    }

    // ---- conjoined ----

    [Fact]
    public async Task conjoined_tenancy_answers_every_tenant_with_the_one_database()
    {
        using var file = TemporaryDatabase.Create("find-tenant-conjoined");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = file.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Schema.For<Sighting>().MultiTenanted();
        });
        IEventStore events = store;

        foreach (var tenantId in new[] { "north", "south", StorageConstants.DefaultTenantId })
        {
            (await events.FindDatabaseForTenantAsync(tenantId, Token)).ShouldBeSameAs(store.Database);
        }
    }
}
