using Fisher.Linq;
using Fisher.Storage;
using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#58, stage 3 — tenants that appear and are suspended while the store is running.
/// </summary>
/// <remarks>
///     <para>
///         <b>Viable on SQLite in a way it is not on either sibling.</b> Provisioning a tenant here is a
///         file plus a migration, cheap enough to do on first use — which is what makes "a tenant
///         appears without a restart" a reasonable offer rather than an operational event. On
///         PostgreSQL or SQL Server the same act is a <c>CREATE DATABASE</c>.
///     </para>
///     <para>
///         The thing worth pinning is that a tenant nobody configured still gets a correctly migrated
///         file, and still cannot see another tenant's data.
///     </para>
/// </remarks>
public class runtime_tenants : IAsyncLifetime
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fisher-runtime-" + Guid.NewGuid().ToString("n")[..8]);

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

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private DocumentStore StoreFor(ITenantSource? source = null)
        => DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Sighting>();
            options.Projections.Snapshot<SightingTally>(SnapshotLifecycle.Async);

            if (source is null)
            {
                options.MultiTenantedDatabasesInDirectory(_directory);
            }
            else
            {
                options.MultiTenantedDatabasesFrom(source);
            }
        });

    // ---- a tenant nobody configured ----

    /// <summary>
    ///     The headline: a tenant named for the first time at runtime gets a file, a schema, and a
    ///     working session.
    /// </summary>
    /// <remarks>
    ///     Stage 1 threw <c>UnknownTenantException</c> here, deliberately — falling back to another
    ///     tenant's file is the one failure this tenancy exists to make impossible, and until there was a
    ///     source to ask, refusing was the only safe answer.
    /// </remarks>
    [Fact]
    public async Task a_tenant_that_was_never_configured_resolves_and_works()
    {
        await using var store = StoreFor();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        store.Tenancy.ShouldBeOfType<DynamicTenancy>();
        store.Tenancy.Cardinality.ShouldBe(DatabaseCardinality.DynamicMultiple);

        var id = Guid.NewGuid();

        await using (var session = store.LightweightSession("arrived-late"))
        {
            session.Store(new Sighting { Id = id, Species = "Manta" });
            await session.SaveChangesAsync(Token);
        }

        await using var query = store.LightweightSession("arrived-late");
        (await query.LoadAsync<Sighting>(id, Token)).ShouldNotBeNull();

        File.Exists(Path.Combine(_directory, "arrived-late.db")).ShouldBeTrue();
    }

    /// <summary>
    ///     The new tenant's file is migrated, not merely created.
    /// </summary>
    /// <remarks>
    ///     <b>The mechanism worth knowing:</b> the migration hangs off <c>OpenConnectionAsync</c> rather
    ///     than off tenant resolution, because resolution is synchronous — <c>DatabaseFor</c> is reached
    ///     from <c>OpenSession</c>, which has no <c>await</c> to offer. Opening a connection is the first
    ///     genuinely asynchronous thing that happens to a new tenant's file and it happens before any
    ///     statement can run against it. An event append is what tells this apart from a document write:
    ///     document tables are created on demand at commit anyway, so only the event store's tables
    ///     prove the whole schema was applied.
    /// </remarks>
    [Fact]
    public async Task a_new_tenants_database_is_migrated_on_first_use()
    {
        await using var store = StoreFor();

        var streamId = Guid.NewGuid();

        await using (var session = store.LightweightSession("fresh"))
        {
            session.Events.StartStream<SightingTally>(streamId, new SightingRecorded("Tern"));
            await session.SaveChangesAsync(Token);
        }

        await using var query = store.LightweightSession("fresh");
        (await query.Events.FetchStreamAsync(streamId, token: Token)).Count.ShouldBe(1);
    }

    /// <remarks>
    ///     Isolation still holds for tenants nobody declared, which is the property the whole tenancy
    ///     exists for — and the one a dynamic source could most easily undermine by handing two tenants
    ///     the same path.
    /// </remarks>
    [Fact]
    public async Task two_runtime_tenants_cannot_see_each_other()
    {
        await using var store = StoreFor();

        var id = Guid.NewGuid();

        await using (var north = store.LightweightSession("n"))
        {
            north.Store(new Sighting { Id = id, Species = "Guillemot" });
            await north.SaveChangesAsync(Token);
        }

        await using var south = store.LightweightSession("s");
        (await south.LoadAsync<Sighting>(id, Token)).ShouldBeNull();
    }

    // ---- suspending a tenant ----

    /// <summary>
    ///     A suspended tenant is refused, and its data is left exactly where it was.
    /// </summary>
    /// <remarks>
    ///     <b>Suspension rather than deletion is the decision fisher#58 asked to be made rather than
    ///     defaulted.</b> Deleting a tenant here means deleting a file — the cheapest deprovisioning of
    ///     any Critter Stack store, and the most irreversible — and Fisher cannot know whether that file
    ///     is backed up. So the API suspends or forgets, and an operator removes the file themselves.
    ///     <c>DisabledTenantException</c> is distinct from <c>UnknownTenantException</c> because
    ///     "switched off" and "never heard of it" are different operational situations.
    /// </remarks>
    [Fact]
    public async Task a_suspended_tenant_is_refused_and_its_data_survives()
    {
        var source = new DirectoryTenantSource(_directory);

        await using var store = StoreFor(source);

        var id = Guid.NewGuid();

        await using (var session = store.LightweightSession("seasonal"))
        {
            session.Store(new Sighting { Id = id, Species = "Puffin" });
            await session.SaveChangesAsync(Token);
        }

        source.Suspend("seasonal");

        // On THIS store, whose cache already holds the tenant's database. That used to need a fresh
        // store — a suspension only reached a tenant nothing had resolved yet, which is the opposite
        // of what switching a tenant off means and made "restart the process" part of the procedure.
        // fisher#213's ITenantSource.OnTenantRevoked is what evicts it.
        Should.Throw<DisabledTenantException>(() => store.LightweightSession("seasonal"))
            .TenantId.ShouldBe("seasonal");

        // And on a fresh store too, since the suspension is the source's rather than the cache's.
        await using var reopened = StoreFor(source);

        Should.Throw<DisabledTenantException>(() => reopened.LightweightSession("seasonal"))
            .TenantId.ShouldBe("seasonal");

        source.Resume("seasonal");

        await using var query = store.LightweightSession("seasonal");

        (await query.LoadAsync<Sighting>(id, Token)).ShouldNotBeNull();
    }

    /// <remarks>
    ///     An application-supplied source is the case the issue expected to be common — the application
    ///     already has a tenants table of its own. Removing a tenant stops it resolving and leaves the
    ///     file alone.
    /// </remarks>
    [Fact]
    public async Task an_application_supplied_source_governs_which_tenants_exist()
    {
        // The default tenant is registered because a store-level operation has to have a database to
        // run against, and this tenancy has no store-level file — the directory convention answers for
        // any id, an explicit source has to be told.
        var source = new InMemoryTenantSource()
            .Add(StorageConstants.DefaultTenantId, PathFor("main"))
            .Add("alpha", PathFor("alpha"));

        await using var store = StoreFor(source);

        await using (var session = store.LightweightSession("alpha"))
        {
            session.Store(new Sighting { Id = Guid.NewGuid(), Species = "Fulmar" });
            await session.SaveChangesAsync(Token);
        }

        // Not registered: refused rather than invented, which is the difference between this source and
        // the directory convention.
        await using var other = StoreFor(source);
        Should.Throw<UnknownTenantException>(() => other.LightweightSession("beta"));

        source.Remove("alpha");

        await using var afterRemoval = StoreFor(source);
        Should.Throw<UnknownTenantException>(() => afterRemoval.LightweightSession("alpha"));

        // The file is untouched — Fisher never deletes a tenant's data.
        File.Exists(Path.Combine(_directory, "alpha.db")).ShouldBeTrue();
    }

    // ---- the daemon ----

    /// <remarks>
    ///     Stage 2 routed the daemon per database; this is the half that finds a database which did not
    ///     exist when the store was built. <c>BuildProjectionDaemonsAsync</c> refreshes first, which is
    ///     what makes a tenant created a moment ago show up with no restart.
    /// </remarks>
    [Fact]
    public async Task the_daemon_picks_up_a_tenant_created_after_the_store_was_built()
    {
        await using var store = StoreFor();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        // Only the default tenant, which the store resolved when it was built.
        (await store.BuildProjectionDaemonsAsync()).Count.ShouldBe(1);

        await using (var session = store.LightweightSession("newcomer"))
        {
            session.Events.StartStream<SightingTally>(Guid.NewGuid(), new SightingRecorded("Skua"));
            await session.SaveChangesAsync(Token);
        }

        // The newcomer's file did not exist a moment ago and is found without a restart.
        var daemons = await store.BuildProjectionDaemonsAsync();
        daemons.Count.ShouldBe(2);

        try
        {
            foreach (var daemon in daemons)
            {
                await daemon.StartAllAsync();
            }
            await store.Tenancy.DatabaseFor("newcomer")
                .WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));

            await using var query = store.LightweightSession("newcomer");
            var tallies = await query.Query<SightingTally>().ToListAsync(Token);

            tallies.ShouldHaveSingleItem().Count.ShouldBe(1);
        }
        finally
        {
            foreach (var daemon in daemons)
            {
                await daemon.StopAllAsync();
                daemon.Dispose();
            }
        }
    }

    private string PathFor(string tenantId)
    {
        Directory.CreateDirectory(_directory);

        return new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, $"{tenantId}.db")
        }.ToString();
    }
}
