using Fisher.Linq;
using Fisher.Storage;
using JasperFx;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#415 — <c>session.ForTenant(id)</c> on a store with more than one database file.
/// </summary>
/// <remarks>
///     <para>
///         <b>A tenant scope shares its session's connection, so it can only ever write to its session's
///         file.</b> fisher#33 built it for conjoined tenancy, where every tenant is in one file and the
///         shared connection is the whole point. Under database-per-tenant nothing asked which file the
///         scope's tenant lived in, so from a <c>north</c> session <c>ForTenant("south").Store(doc)</c>
///         wrote a <c>tenant_id = 'south'</c> row into <b>north's</b> file — where no <c>south</c> session
///         looks and every <c>north</c> session filters it out. Silent in both directions.
///     </para>
///     <para>
///         The ruling is the issue's first option: <b>refuse a tenant in another file, keep co-located
///         tenants working</b>. Making it work would need a transaction across two SQLite files.
///     </para>
/// </remarks>
public class tenant_scopes_across_databases : IAsyncLifetime
{
    private readonly TemporaryDatabase _north = TemporaryDatabase.Create("scope-north");
    private readonly TemporaryDatabase _south = TemporaryDatabase.Create("scope-south");

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fisher-scopes-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly List<DocumentStore> _stores = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }

        _north.Dispose();
        _south.Dispose();

        if (Directory.Exists(_directory))
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A tenant file still held by a pooled connection; it is under the temp path.
            }
        }
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<DocumentStore> StoreAsync(Action<StoreOptions> tenancy)
    {
        var store = DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Sighting>().MultiTenanted();
            tenancy(options);
        });

        _stores.Add(store);
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        return store;
    }

    /// <summary>A file each — database-per-tenant.</summary>
    private Task<DocumentStore> SeparateFilesAsync()
        => StoreAsync(options => options.MultiTenantedDatabases(databases => databases
            .AddTenant("north", _north.ConnectionString)
            .AddTenant("south", _south.ConnectionString)));

    /// <summary>Two tenants co-located in north's file and one alone in south's — sharded.</summary>
    private Task<DocumentStore> ShardedAsync()
        => StoreAsync(options =>
        {
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.MultiTenantedDatabases(databases => databases
                .AddTenant("north", _north.ConnectionString)
                .AddTenant("alder", _north.ConnectionString)
                .AddTenant("south", _south.ConnectionString));
        });

    private async Task<int> RowsInFileAsync(DocumentStore store, string tenantId)
    {
        await using var session = store.QuerySession(tenantId);
        return await session.Query<Sighting>().AnyTenant().CountAsync(Token);
    }

    // ---- a tenant in another file ----

    /// <remarks>
    ///     The discriminating fact. Before the fix the scope and the commit both succeeded and the row
    ///     was in north's file under south's id — which is why the file counts are asserted as well as
    ///     the throw.
    /// </remarks>
    [Fact]
    public async Task a_scope_for_a_tenant_in_another_file_is_refused()
    {
        var store = await SeparateFilesAsync();

        await using (var session = store.LightweightSession("north"))
        {
            var refusal = Should.Throw<InvalidOperationException>(() => session.ForTenant("south"));

            refusal.Message.ShouldContain("ForTenant(\"south\")");
            refusal.Message.ShouldContain("'north'");
            refusal.Message.ShouldContain("different database files");
            refusal.Message.ShouldContain("LightweightSession(\"south\")");

            session.Store(new Sighting { Id = Guid.NewGuid(), Species = "Osprey" });
            await session.SaveChangesAsync(Token);
        }

        (await RowsInFileAsync(store, "north")).ShouldBe(1);
        (await RowsInFileAsync(store, "south")).ShouldBe(0);
    }

    /// <remarks>
    ///     An id the tenancy does not know used to be stamped onto this file's rows like any other, since
    ///     nothing resolved it. It is now refused by the tenancy's own exception, as a session for it is.
    /// </remarks>
    [Fact]
    public async Task an_unknown_tenant_is_refused_rather_than_stamped_onto_this_file()
    {
        var store = await SeparateFilesAsync();

        await using var session = store.LightweightSession("north");

        Should.Throw<UnknownTenantException>(() => session.ForTenant("west"));
    }

    /// <remarks>
    ///     <c>ExistingDatabaseFor</c>, not <c>DatabaseFor</c>: under directory tenancy the latter registers
    ///     any id and its first connection creates the file, and a refusal must not leave a tenant behind.
    ///     Once south's file exists the refusal is the different-file one.
    /// </remarks>
    [Fact]
    public async Task a_refusal_under_directory_tenancy_provisions_nothing()
    {
        var store = await StoreAsync(options => options.MultiTenantedDatabasesInDirectory(_directory));

        await using (var north = store.LightweightSession("north"))
        {
            north.Store(new Sighting { Id = Guid.NewGuid(), Species = "Osprey" });
            await north.SaveChangesAsync(Token);

            Should.Throw<UnknownTenantException>(() => north.ForTenant("south"));
        }

        File.Exists(Path.Combine(_directory, "south.db")).ShouldBeFalse();

        await using (var south = store.LightweightSession("south"))
        {
            south.Store(new Sighting { Id = Guid.NewGuid(), Species = "Kite" });
            await south.SaveChangesAsync(Token);
        }

        await using (var north = store.LightweightSession("north"))
        {
            Should.Throw<InvalidOperationException>(() => north.ForTenant("south"))
                .Message.ShouldContain("different database files");
        }
    }

    // ---- co-located tenants keep fisher#33's one-transaction write ----

    [Fact]
    public async Task a_scope_for_a_co_located_tenant_still_writes_in_the_one_transaction()
    {
        var store = await ShardedAsync();
        var id = Guid.NewGuid();

        await using (var session = store.LightweightSession("north"))
        {
            session.ForTenant("alder").Store(new Sighting { Id = id, Species = "Heron" });
            await session.SaveChangesAsync(Token);
        }

        await using var alder = store.QuerySession("alder");
        await using var north = store.QuerySession("north");

        (await alder.LoadAsync<Sighting>(id, Token)).ShouldNotBeNull();
        (await north.LoadAsync<Sighting>(id, Token)).ShouldBeNull();
    }

    [Fact]
    public async Task a_scope_in_a_sharded_store_still_refuses_the_other_shard()
    {
        var store = await ShardedAsync();

        await using var session = store.LightweightSession("north");

        Should.Throw<InvalidOperationException>(() => session.ForTenant("south"));
    }

    // ---- the configured spelling (fisher#393, reached from a scope) ----

    /// <remarks>
    ///     A scope used to stamp whatever casing the caller passed, so <c>ForTenant("ALDER")</c> made an
    ///     <c>ALDER</c> tenant inside the shared file that no <c>alder</c> session could see — the extra
    ///     tenant #393 removed from <c>OpenSession</c>, one entry point over.
    /// </remarks>
    [Fact]
    public async Task a_scope_stamps_the_configured_spelling()
    {
        var store = await ShardedAsync();
        var id = Guid.NewGuid();

        await using (var session = store.LightweightSession("north"))
        {
            session.ForTenant("ALDER").ShouldBeSameAs(session.ForTenant("alder"));

            session.ForTenant("ALDER").Store(new Sighting { Id = id, Species = "Heron" });
            await session.SaveChangesAsync(Token);
        }

        await using var alder = store.QuerySession("alder");
        (await alder.LoadAsync<Sighting>(id, Token)).ShouldNotBeNull();
    }

    [Fact]
    public async Task a_scope_for_the_sessions_own_tenant_in_another_casing_is_the_session()
    {
        var store = await ShardedAsync();

        await using var session = store.LightweightSession("north");

        session.ForTenant("NORTH").ShouldBeSameAs(session);
    }
}
