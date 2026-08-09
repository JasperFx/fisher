using Fisher.Linq;
using Fisher.Storage;
using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#47 stage 1 — <c>ITenancy</c>, and a SQLite file per tenant.
/// </summary>
/// <remarks>
///     <para>
///         <b>Arguably SQLite's best tenancy story rather than its worst.</b> The usual objection to
///         database-per-tenant — provisioning is heavyweight — inverts here: a tenant is a file.
///         Creating one is a file plus a migration, deleting one is deleting a file, and one tenant's
///         data cannot leak into another's because there is no shared table to leak through.
///     </para>
///     <para>
///         And it answers the sharpest structural constraint: under conjoined tenancy every tenant
///         contends for one write lock. Under file-per-tenant they write concurrently, which
///         <c>two_tenants_write_at_the_same_time</c> pins directly — that is the property that makes
///         this a performance feature and not only an isolation one.
///     </para>
/// </remarks>
public class database_per_tenant : IAsyncLifetime
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fisher-tenants-" + Guid.NewGuid().ToString("n")[..8]);

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
                // A tenant file still held by a pooled connection. The directory is under the temp
                // path and the test has said what it means to say.
            }
        }

        return ValueTask.CompletedTask;
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private DocumentStore StoreFor(Action<TenantDatabases>? tenants = null)
        => DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Sighting>();

            options.MultiTenantedDatabases(databases =>
            {
                if (tenants is null)
                {
                    databases.InDirectory(_directory).AddTenants("north", "south");
                }
                else
                {
                    tenants(databases);
                }
            });
        });

    // ---- the default is unchanged ----

    /// <remarks>
    ///     Conjoined tenancy stays and stays the default; the two are alternatives, as on both
    ///     siblings. A store that says nothing gets exactly what it got before.
    /// </remarks>
    [Fact]
    public void a_store_that_says_nothing_has_one_database_for_every_tenant()
    {
        using var database = TemporaryDatabase.Create("tenancy-default");
        using var store = DocumentStore.For(database.ConnectionString);

        store.Tenancy.ShouldBeOfType<DefaultTenancy>();
        store.Tenancy.Cardinality.ShouldBe(DatabaseCardinality.Single);
        store.Tenancy.DatabaseFor("north").ShouldBeSameAs(store.Database);
        store.Tenancy.DatabaseFor("south").ShouldBeSameAs(store.Database);
    }

    // ---- a file per tenant ----

    [Fact]
    public async Task each_tenant_gets_its_own_file()
    {
        await using var store = StoreFor();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        store.Tenancy.ShouldBeOfType<SeparateDatabaseTenancy>();
        store.Tenancy.Cardinality.ShouldBe(DatabaseCardinality.StaticMultiple);
        store.Tenancy.AllDatabases().Count.ShouldBe(2);

        File.Exists(Path.Combine(_directory, "north.db")).ShouldBeTrue();
        File.Exists(Path.Combine(_directory, "south.db")).ShouldBeTrue();
    }

    /// <remarks>
    ///     Isolation checked in both directions, which is the discipline
    ///     <c>ConjoinedEventTenancyCompliance</c> established — a store that leaked would still answer
    ///     correctly for the tenant that owns the data.
    /// </remarks>
    [Fact]
    public async Task a_tenants_data_is_in_its_own_file_and_nowhere_else()
    {
        await using var store = StoreFor();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var id = Guid.NewGuid();

        await using (var session = store.LightweightSession("north"))
        {
            session.Store(new Sighting { Id = id, Species = "Basking shark" });
            session.Events.StartStream(id, new SightingLogged("Basking shark"));
            await session.SaveChangesAsync(Token);
        }

        await using (var north = store.LightweightSession("north"))
        {
            (await north.LoadAsync<Sighting>(id, Token)).ShouldNotBeNull();
            (await north.Events.FetchStreamAsync(id, token: Token)).Count.ShouldBe(1);
        }

        await using (var south = store.LightweightSession("south"))
        {
            (await south.LoadAsync<Sighting>(id, Token)).ShouldBeNull();
            (await south.Events.FetchStreamAsync(id, token: Token)).ShouldBeEmpty();
        }

        // And there is no shared table to leak through, which is the isolation an operator can check
        // with a file browser rather than by trusting a predicate.
        (await CountAsync(Path.Combine(_directory, "north.db"), "fi_doc_sighting")).ShouldBe(1);
        (await CountAsync(Path.Combine(_directory, "south.db"), "fi_doc_sighting")).ShouldBe(0);
    }

    /// <summary>
    ///     The property that makes this a performance feature: two tenants write at the same moment.
    /// </summary>
    /// <remarks>
    ///     Under conjoined tenancy the second writer would wait for the first's write lock, because
    ///     there is one file. Here they hold two locks and neither waits — which is the only way a
    ///     multi-tenant Fisher application scales write throughput at all. Pinned by holding one
    ///     tenant's transaction open across the other tenant's whole commit.
    /// </remarks>
    [Fact]
    public async Task two_tenants_write_at_the_same_time()
    {
        await using var store = StoreFor();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var north = store.LightweightSession("north");
        north.Store(new Sighting { Id = Guid.NewGuid(), Species = "Minke" });

        // North's write lock, taken and held.
        var northConnection = await ((Fisher.Internal.FisherSession)north).ConnectionAsync(Token);
        await using var held = (SqliteTransaction)await northConnection.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, Token);

        // South commits while it is held, and does not wait.
        await using (var south = store.LightweightSession("south"))
        {
            south.Store(new Sighting { Id = Guid.NewGuid(), Species = "Orca" });
            await south.SaveChangesAsync(Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
        }

        await held.RollbackAsync(Token);

        (await CountAsync(Path.Combine(_directory, "south.db"), "fi_doc_sighting")).ShouldBe(1);
    }

    /// <remarks>
    ///     Falling back to the default file would write one tenant's data into another's, which is the
    ///     one failure this tenancy exists to make impossible — and it would be silent.
    /// </remarks>
    [Fact]
    public async Task an_unknown_tenant_is_refused_by_name()
    {
        await using var store = StoreFor();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var ex = Should.Throw<UnknownTenantException>(() => store.LightweightSession("east"));

        ex.TenantId.ShouldBe("east");
        ex.Message.ShouldContain("north");
        ex.Message.ShouldContain("south");
    }

    // ---- configuration ----

    [Fact]
    public async Task a_tenant_can_name_its_own_file()
    {
        var explicitPath = Path.Combine(_directory, "somewhere-else.db");
        Directory.CreateDirectory(_directory);

        await using var store = StoreFor(databases => databases
            .InDirectory(_directory)
            .AddTenants("north")
            .AddTenant("south", new SqliteConnectionStringBuilder { DataSource = explicitPath }.ToString()));

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        File.Exists(explicitPath).ShouldBeTrue();
        File.Exists(Path.Combine(_directory, "south.db")).ShouldBeFalse();
    }

    [Fact]
    public void naming_tenants_with_no_directory_is_refused_by_name()
    {
        var ex = Should.Throw<InvalidOperationException>(()
            => StoreFor(databases => databases.AddTenants("north")));

        ex.Message.ShouldContain("InDirectory");
    }

    [Fact]
    public void a_tenancy_with_no_tenants_is_refused_by_name()
    {
        Should.Throw<InvalidOperationException>(() => StoreFor(_ => { }))
            .Message.ShouldContain("no tenants");
    }

    // ---- migration and tooling ----

    /// <remarks>
    ///     Migrating N files and failing at the fortieth leaves a store in mixed versions whatever the
    ///     exception says. What is reported is <em>which</em> are current, rather than one exception
    ///     naming whichever tenant happened to fail first.
    /// </remarks>
    [Fact]
    public async Task a_partial_migration_reports_which_tenants_are_current()
    {
        Directory.CreateDirectory(_directory);

        // A tenant whose file cannot be written, standing in for the fortieth of a hundred.
        var unwritable = Path.Combine(_directory, "sub", "broken.db");

        await using var store = StoreFor(databases => databases
            .InDirectory(_directory)
            .AddTenants("north")
            .AddTenant("broken", new SqliteConnectionStringBuilder { DataSource = unwritable }.ToString()));

        var ex = await Should.ThrowAsync<TenantMigrationException>(async ()
            => await store.ApplyAllConfiguredChangesToDatabaseAsync(Token));

        ex.Migrated.ShouldBe(["north"]);
        ex.Failures.Keys.ShouldBe(["broken"]);
        ex.Message.ShouldContain("mixed versions");
    }

    /// <remarks>
    ///     A monitoring console reads <c>AllDatabases()</c> to show progress. Under this tenancy it has
    ///     to see every tenant, or it shows one file's progress and calls it the store's.
    /// </remarks>
    [Fact]
    public async Task tooling_sees_every_tenants_database()
    {
        await using var store = StoreFor();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var explorer = (IEventStore)store;

        explorer.DatabaseCardinality.ShouldBe(DatabaseCardinality.StaticMultiple);
        (await explorer.AllDatabases()).Count.ShouldBe(2);
    }

    /// <summary>
    ///     The daemon refuses rather than projecting one tenant's events into every tenant's documents.
    /// </summary>
    /// <remarks>
    ///     Stage 2 (fisher#57) is what routes it per database. Until then, ignoring the
    ///     <c>IEventDatabase</c> parameter — which every one of those methods still does — would read
    ///     the default tenant's events and write documents from them, silently and to the one place
    ///     this tenancy exists to keep separate.
    /// </remarks>
    [Fact]
    public async Task the_daemon_refuses_under_database_per_tenant()
    {
        await using var store = StoreFor();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var ex = await Should.ThrowAsync<NotSupportedException>(async ()
            => await store.BuildProjectionDaemonAsync());

        ex.Message.ShouldContain("fisher#57");
    }

    private async Task<long> CountAsync(string path, string table)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString());

        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = $"select count(*) from {table}";

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token));
    }
}

public class Sighting
{
    public Guid Id { get; set; }
    public string Species { get; set; } = string.Empty;
}

public record SightingLogged(string Species);
