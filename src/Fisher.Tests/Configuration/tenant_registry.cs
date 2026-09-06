using Fisher.Storage;
using JasperFx;
using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using Microsoft.Data.Sqlite;
using Weasel.Core.MultiTenancy;
using Weasel.Sqlite;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#213 — the tenant registry: Marten's master-table tenancy, over the runtime Weasel 9.31.0
///     lifted out of Marten and Polecat (weasel#567).
/// </summary>
/// <remarks>
///     <para>
///         The property that distinguishes this from Fisher's other three tenant sources is that the
///         set is <em>a record</em>: durable across restarts, shared between processes, and editable
///         while the store runs. <c>DirectoryTenantSource</c> resolves any tenant id at all and so can
///         never refuse one; <c>InMemoryTenantSource</c> forgets everything on restart.
///     </para>
///     <para>
///         <b>The deletion stance is what these tests are most careful about.</b> Fisher deprovisions
///         and never deletes: suspending and forgetting both leave the tenant's <c>.db</c> file exactly
///         where it was, and every lifecycle test here asserts the file is still on disk afterwards.
///     </para>
/// </remarks>
public class tenant_registry : IAsyncLifetime
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fisher-registry-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly TemporaryDatabase _registry = TemporaryDatabase.Create("registry");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _registry.Dispose();

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

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private string PathFor(string tenantId) => Path.Combine(_directory, $"{tenantId}.db");

    private string ConnectionStringFor(string tenantId)
        => new SqliteConnectionStringBuilder { DataSource = PathFor(tenantId) }.ToString();

    private DocumentStore StoreFor(out MasterTableTenantSource source,
        Action<MasterTableTenancyOptions<SqliteDataSource>>? configure = null)
    {
        MasterTableTenantSource? built = null;

        var store = DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Sighting>();

            built = options.MultiTenantedDatabasesInRegistry(x =>
            {
                x.ConnectionString = _registry.ConnectionString;

                // A registry store has no store-level file, so the default tenant has to come from
                // somewhere the store can read synchronously while it is being built. See
                // MasterTableTenantSource.TryFind.
                x.SeedDatabases.RegisterDefault(ConnectionStringFor("default"));

                configure?.Invoke(x);
            });
        });

        source = built!;

        return store;
    }

    // ---- the control table ----

    [Fact]
    public async Task the_control_table_is_provisioned_on_first_use_and_is_idempotent()
    {
        await using var store = StoreFor(out var source);

        (await TableExistsAsync("fi_tenants")).ShouldBeFalse();

        await source.AddTenantAsync("one", ConnectionStringFor("one"), Token);

        (await TableExistsAsync("fi_tenants")).ShouldBeTrue();

        // A second instance over the same registry file provisions again, and must not fail — the
        // base's semaphore guards one process and a deployment has several, so the DDL itself has to
        // be idempotent.
        await using var second = StoreFor(out var other);
        await other.RefreshAsync(Token);

        (await TenantRowCountAsync()).ShouldBe(1);
    }

    /// <remarks>
    ///     SQLite has no schemas, so a logical schema name folds into the table prefix as it does for
    ///     every other Fisher table — which is what keeps two logical stores sharing one registry file
    ///     from sharing one tenant list.
    /// </remarks>
    [Fact]
    public async Task the_logical_schema_folds_into_the_table_name()
    {
        await using var store = StoreFor(out var source, x => x.SchemaName = "reporting");
        await source.AddTenantAsync("one", ConnectionStringFor("one"), Token);

        (await TableExistsAsync("reporting_fi_tenants")).ShouldBeTrue();
        (await TableExistsAsync("fi_tenants")).ShouldBeFalse();
    }

    /// <remarks>
    ///     The cache compares tenant ids with <c>OrdinalIgnoreCase</c>, matching Fisher's other two
    ///     tenancies. SQLite's default collation is case-sensitive, so the control table's
    ///     <c>tenant_id</c> is declared <c>collate nocase</c> — without it the cache and the table
    ///     disagree, and a tenant resolves through one and not the other.
    /// </remarks>
    [Fact]
    public async Task tenant_ids_are_case_insensitive_in_the_table_as_well_as_the_cache()
    {
        await using var store = StoreFor(out var source);

        await source.AddTenantAsync("Acme", ConnectionStringFor("acme"), Token);
        await source.AddTenantAsync("ACME", ConnectionStringFor("acme"), Token);

        // One tenant, not two — which is only true if the upsert's conflict target matched.
        (await TenantRowCountAsync()).ShouldBe(1);

        source.TryFind("acme", out _).ShouldBeTrue();

        // And a fresh process reading the table back agrees.
        await using var second = StoreFor(out var other);
        await other.RefreshAsync(Token);
        other.TryFind("aCmE", out _).ShouldBeTrue();
    }

    // ---- resolution ----

    [Fact]
    public async Task a_registered_tenant_resolves_and_its_file_is_migrated_on_first_use()
    {
        await using var store = StoreFor(out var source);
        await source.AddTenantAsync("acme", ConnectionStringFor("acme"), Token);

        store.Tenancy.ShouldBeOfType<DynamicTenancy>().Source.ShouldBeSameAs(source);
        store.Tenancy.Cardinality.ShouldBe(DatabaseCardinality.DynamicMultiple);

        var id = Guid.NewGuid();

        await using (var session = store.LightweightSession("acme"))
        {
            session.Store(new Sighting { Id = id, Species = "Manta" });
            await session.SaveChangesAsync(Token);
        }

        await using var query = store.LightweightSession("acme");
        (await query.LoadAsync<Sighting>(id, Token)).ShouldNotBeNull();

        File.Exists(PathFor("acme")).ShouldBeTrue();
    }

    /// <remarks>
    ///     The difference from <c>DirectoryTenantSource</c>, and the reason a registry is worth having:
    ///     a convention source answers for every tenant id there could ever be, so it cannot tell an
    ///     unknown tenant from a real one. A record can.
    /// </remarks>
    [Fact]
    public async Task an_unregistered_tenant_is_refused()
    {
        await using var store = StoreFor(out var source);
        await source.AddTenantAsync("acme", ConnectionStringFor("acme"), Token);

        Should.Throw<UnknownTenantException>(() => store.LightweightSession("nobody"));
    }

    /// <remarks>
    ///     Two tenants, two files, and neither can see the other's rows — the property
    ///     database-per-tenant exists for, checked here because the registry is a new way of arriving
    ///     at it.
    /// </remarks>
    [Fact]
    public async Task two_registered_tenants_keep_their_data_apart()
    {
        await using var store = StoreFor(out var source);
        await source.AddTenantAsync("one", ConnectionStringFor("one"), Token);
        await source.AddTenantAsync("two", ConnectionStringFor("two"), Token);

        var id = Guid.NewGuid();

        await using (var first = store.LightweightSession("one"))
        {
            first.Store(new Sighting { Id = id, Species = "Manta" });
            await first.SaveChangesAsync(Token);
        }

        await using var second = store.LightweightSession("two");
        (await second.LoadAsync<Sighting>(id, Token)).ShouldBeNull();
    }

    /// <remarks>
    ///     The seed list writes tenants that should exist from the first start, upserted rather than
    ///     inserted so a changed connection string is corrected on the next boot.
    /// </remarks>
    [Fact]
    public async Task seeded_tenants_are_written_on_the_first_read()
    {
        await using var store = StoreFor(out var source, x =>
        {
            x.SeedDatabases.Register("seeded-one", ConnectionStringFor("seeded-one"));
            x.SeedDatabases.Register("seeded-two", ConnectionStringFor("seeded-two"));
        });

        var all = await source.AllAsync(Token);

        all.Select(x => x.TenantId).Where(x => x != StorageConstants.DefaultTenantId)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ShouldBe(["seeded-one", "seeded-two"]);
    }

    // ---- the runtime lifecycle, and the deletion stance ----

    /// <remarks>
    ///     The headline: a tenant registered while the store is running is usable by the very next
    ///     session, with no refresh and no restart, because <c>AddTenantAsync</c> caches as it writes.
    /// </remarks>
    [Fact]
    public async Task a_tenant_added_at_runtime_is_usable_immediately()
    {
        await using var store = StoreFor(out var source);

        Should.Throw<UnknownTenantException>(() => store.LightweightSession("late"));

        await source.AddTenantAsync("late", ConnectionStringFor("late"), Token);

        await using var session = store.LightweightSession("late");
        session.Store(new Sighting { Id = Guid.NewGuid(), Species = "Wahoo" });
        await session.SaveChangesAsync(Token);
    }

    /// <remarks>
    ///     <b>Suspension, never deletion.</b> The tenant stops resolving and its file is untouched —
    ///     which is the whole of Fisher's deprovisioning stance, and the reason the last assertion is
    ///     here rather than implied.
    /// </remarks>
    [Fact]
    public async Task suspending_a_tenant_refuses_sessions_and_leaves_its_file_alone()
    {
        await using var store = StoreFor(out var source);
        await source.AddTenantAsync("acme", ConnectionStringFor("acme"), Token);

        await using (var session = store.LightweightSession("acme"))
        {
            session.Store(new Sighting { Id = Guid.NewGuid(), Species = "Manta" });
            await session.SaveChangesAsync(Token);
        }

        await source.SuspendTenantAsync("acme", Token);

        // Suspended, not forgotten: a distinct exception, because "switched off" and "never heard of
        // it" are different operational situations.
        Should.Throw<DisabledTenantException>(() => store.LightweightSession("acme"));

        File.Exists(PathFor("acme")).ShouldBeTrue();
        (await TenantRowCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task resuming_a_suspended_tenant_brings_it_back_with_its_data()
    {
        await using var store = StoreFor(out var source);
        await source.AddTenantAsync("acme", ConnectionStringFor("acme"), Token);

        var id = Guid.NewGuid();

        await using (var session = store.LightweightSession("acme"))
        {
            session.Store(new Sighting { Id = id, Species = "Manta" });
            await session.SaveChangesAsync(Token);
        }

        await source.SuspendTenantAsync("acme", Token);
        (await source.AllSuspendedAsync(Token)).ShouldBe(["acme"]);

        await source.ResumeTenantAsync("acme", Token);
        (await source.AllSuspendedAsync(Token)).ShouldBeEmpty();

        await using var query = store.LightweightSession("acme");
        (await query.LoadAsync<Sighting>(id, Token)).ShouldNotBeNull();
    }

    /// <remarks>
    ///     ⚠️ <b>The decision the dialect contract says the two shipped stores disagree about.</b>
    ///     Polecat's <c>MERGE</c> re-enables on upsert; Marten's <c>on conflict</c> does not, and
    ///     Fisher follows Marten — re-enabling as a side effect of correcting a connection string
    ///     would silently undo a deliberate suspension.
    /// </remarks>
    [Fact]
    public async Task re_adding_a_suspended_tenant_does_not_resume_it()
    {
        await using var store = StoreFor(out var source);
        await source.AddTenantAsync("acme", ConnectionStringFor("acme"), Token);
        await source.SuspendTenantAsync("acme", Token);

        await source.AddTenantAsync("acme", ConnectionStringFor("acme"), Token);

        Should.Throw<DisabledTenantException>(() => store.LightweightSession("acme"));
        (await source.AllSuspendedAsync(Token)).ShouldBe(["acme"]);
    }

    /// <remarks>
    ///     <b>Forgetting removes the registry row and never the file.</b> The lifted Weasel base already
    ///     draws the line in the same place — its <c>DeleteDatabaseRecordAsync</c> leaves the tenant's
    ///     own database completely alone — so Fisher's standing rule cost no guard. The data is still on
    ///     disk for an operator to archive, and re-registering the tenant finds it.
    /// </remarks>
    [Fact]
    public async Task forgetting_a_tenant_removes_its_record_and_leaves_its_data_on_disk()
    {
        await using var store = StoreFor(out var source);
        await source.AddTenantAsync("acme", ConnectionStringFor("acme"), Token);

        var id = Guid.NewGuid();

        await using (var session = store.LightweightSession("acme"))
        {
            session.Store(new Sighting { Id = id, Species = "Manta" });
            await session.SaveChangesAsync(Token);
        }

        await source.ForgetTenantAsync("acme", Token);

        (await TenantRowCountAsync()).ShouldBe(0);
        Should.Throw<UnknownTenantException>(() => store.LightweightSession("acme"));

        // The file is there, and so is everything in it.
        File.Exists(PathFor("acme")).ShouldBeTrue();

        await source.AddTenantAsync("acme", ConnectionStringFor("acme"), Token);

        await using var query = store.LightweightSession("acme");
        (await query.LoadAsync<Sighting>(id, Token)).ShouldNotBeNull();
    }

    /// <remarks>
    ///     There is deliberately no member on the source that deletes a tenant's database file, and
    ///     nothing about deprovisioning one goes near it. Pinned by reflection rather than by prose,
    ///     because "we do not delete" is exactly the kind of rule a later convenience method breaks
    ///     without anybody noticing.
    /// </remarks>
    [Fact]
    public void the_source_offers_no_way_to_delete_a_tenants_database()
    {
        var suspicious = typeof(MasterTableTenantSource)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(x => x.Name)
            .Where(x => x.Contains("Drop", StringComparison.OrdinalIgnoreCase)
                        || (x.Contains("Delete", StringComparison.OrdinalIgnoreCase)
                            && !x.Contains("Record", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        suspicious.ShouldBeEmpty();
    }

    // ---- across processes ----

    /// <remarks>
    ///     <b>The trade the synchronous session path imposes, stated rather than hidden.</b>
    ///     <c>ITenancy.DatabaseFor</c> is reached from <c>OpenSession</c> and has no <c>await</c> to
    ///     offer, so a tenant this instance has never read does not resolve — Marten answers the same
    ///     question with <c>GetAwaiter().GetResult()</c> and Fisher will not. A refresh is what makes it
    ///     visible, and the daemon does one every minute.
    /// </remarks>
    [Fact]
    public async Task a_tenant_added_by_another_process_appears_on_the_next_refresh()
    {
        await using var writer = StoreFor(out var writing);
        await using var reader = StoreFor(out var reading);

        await writing.AddTenantAsync("elsewhere", ConnectionStringFor("elsewhere"), Token);

        // The second store has not read the table since, so it does not know.
        reading.TryFind("elsewhere", out _).ShouldBeFalse();

        await reading.RefreshAsync(Token);

        reading.TryFind("elsewhere", out var found).ShouldBeTrue();
        found.IsActive.ShouldBeTrue();

        await using var session = reader.LightweightSession("elsewhere");
        session.Store(new Sighting { Id = Guid.NewGuid(), Species = "Opah" });
        await session.SaveChangesAsync(Token);
    }

    /// <remarks>
    ///     A suspension made elsewhere has to arrive as a suspension, not as an absence — otherwise the
    ///     second process reports <c>UnknownTenantException</c> for a tenant that is registered and
    ///     merely switched off. Weasel's base collapses the two on purpose (a disabled tenant is not
    ///     routable, which is what disabling means); Fisher keeps them apart because fisher#58 decided
    ///     they are different operational situations.
    /// </remarks>
    [Fact]
    public async Task a_suspension_made_elsewhere_arrives_as_a_suspension()
    {
        await using var writer = StoreFor(out var writing);
        await using var reader = StoreFor(out var reading);

        await writing.AddTenantAsync("acme", ConnectionStringFor("acme"), Token);
        await reading.RefreshAsync(Token);

        await writing.SuspendTenantAsync("acme", Token);
        await reading.RefreshAsync(Token);

        reading.TryFind("acme", out var found).ShouldBeTrue();
        found.IsActive.ShouldBeFalse();

        Should.Throw<DisabledTenantException>(() => reader.LightweightSession("acme"));
    }

    // ---- the store-level operations ----

    /// <remarks>
    ///     <c>ApplyAllConfiguredChangesToDatabaseAsync</c> refreshes the tenancy first, so it reaches
    ///     every registered tenant's file rather than only the ones a session happened to touch — the
    ///     omission <c>db-apply</c> had to close for the directory source, met again here.
    /// </remarks>
    [Fact]
    public async Task applying_the_schema_reaches_every_registered_tenant()
    {
        await using (var first = StoreFor(out var seeding))
        {
            await seeding.AddTenantAsync("one", ConnectionStringFor("one"), Token);
            await seeding.AddTenantAsync("two", ConnectionStringFor("two"), Token);
        }

        await using var store = StoreFor(out _);
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        store.Tenancy.AllDatabases().Select(x => x.Identifier)
            .Where(x => x != StorageConstants.DefaultTenantId)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ShouldBe(["one", "two"]);

        (await TableExistsInAsync(ConnectionStringFor("one"), "fi_events")).ShouldBeTrue();
        (await TableExistsInAsync(ConnectionStringFor("two"), "fi_events")).ShouldBeTrue();
    }

    /// <remarks>
    ///     A suspended tenant is not migrated, because it is not routable — and refreshing must not
    ///     build a database for it, which would put a file back into rotation the operator switched
    ///     off.
    /// </remarks>
    [Fact]
    public async Task a_suspended_tenant_is_left_out_of_the_stores_databases()
    {
        await using var store = StoreFor(out var source);
        await source.AddTenantAsync("one", ConnectionStringFor("one"), Token);
        await source.AddTenantAsync("two", ConnectionStringFor("two"), Token);
        await source.SuspendTenantAsync("two", Token);

        await using var fresh = StoreFor(out _);
        await fresh.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        fresh.Tenancy.AllDatabases().Select(x => x.Identifier)
            .Where(x => x != StorageConstants.DefaultTenantId)
            .ShouldBe(["one"]);
    }

    // ---- the store-agnostic admin surface, inherited from the lift ----

    /// <remarks>
    ///     <c>IDynamicTenantSource&lt;string&gt;</c> and <c>IMasterTableMultiTenancy</c> come free from
    ///     the lifted base, which is a surface Fisher had no equivalent of before — it is how a
    ///     monitoring console administers tenants without naming the store. Worth pinning because it is
    ///     inherited rather than written, so nothing local would fail if a future base stopped
    ///     declaring it.
    /// </remarks>
    [Fact]
    public async Task the_shared_dynamic_tenant_source_surface_is_implemented()
    {
        await using var store = StoreFor(out var source);

        var dynamicSource = source.ShouldBeAssignableTo<IDynamicTenantSource<string>>()!;

        await dynamicSource.AddTenantAsync("acme", ConnectionStringFor("acme"));
        (await dynamicSource.FindAsync("acme")).ShouldNotBeNullOrEmpty();

        // Deliberately the tenant ids and not the connection strings, which would put credentials on
        // an admin dashboard.
        dynamicSource.AllActive().ShouldContain("acme");

        await dynamicSource.DisableTenantAsync("acme");
        (await dynamicSource.AllDisabledAsync()).ShouldBe(["acme"]);

        // Disabling evicts, and enabling deliberately does not refill — the base loads lazily, which
        // is also what makes enabling a tenant that was never added a no-op rather than a phantom.
        dynamicSource.AllActive().ShouldNotContain("acme");

        await dynamicSource.EnableTenantAsync("acme");
        (await dynamicSource.AllDisabledAsync()).ShouldBeEmpty();

        await dynamicSource.RefreshAsync();
        dynamicSource.AllActive().ShouldContain("acme");
    }

    // ---- helpers ----

    private Task<bool> TableExistsAsync(string name) => TableExistsInAsync(_registry.ConnectionString, name);

    private async Task<bool> TableExistsInAsync(string connectionString, string name)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from sqlite_master where type = 'table' and name = $name";
        command.Parameters.AddWithValue("$name", name);

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token)) > 0;
    }

    /// <remarks>
    ///     Excludes the default tenant, which every store in this class seeds so that it can be built
    ///     at all — see StoreFor.
    /// </remarks>
    private async Task<long> TenantRowCountAsync()
    {
        await using var connection = new SqliteConnection(_registry.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from fi_tenants where tenant_id <> $default";
        command.Parameters.AddWithValue("$default", StorageConstants.DefaultTenantId);

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token));
    }
}
