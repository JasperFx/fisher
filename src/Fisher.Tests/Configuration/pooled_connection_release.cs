using Fisher.Storage;
using JasperFx;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#59 — returning a database's pooled connections when Fisher is finished with it.
/// </summary>
/// <remarks>
///     <para>
///         <b>The issue was filed about caching <c>FisherDatabase</c> objects and the measurement said
///         that was the wrong thing to worry about.</b> 200 tenant databases resolved but never used
///         cost no measurable memory and no file handles at all — a <c>SqliteDataSource</c> is a factory
///         rather than a pool, building a fresh connection on every open. What costs is a tenant that
///         has been <em>used</em>: Microsoft.Data.Sqlite keeps a pooled connection per connection
///         string, worth three file handles (<c>.db</c>, <c>-wal</c>, <c>-shm</c>), in a
///         <b>process-wide</b> registry that nothing Fisher disposed ever touched.
///     </para>
///     <para>
///         So the fix is not eviction. It is that disposal releases what it opened — which was leaking
///         for <em>every</em> store, not only a multi-tenant one.
///     </para>
///     <para>
///         <b>Measured through SQLite's own <c>-wal</c> and <c>-shm</c> sidecars rather than by counting
///         file descriptors.</b> SQLite creates them when a connection opens the database in WAL mode
///         and <em>deletes them when the last connection closes</em>, so their presence is an exact,
///         file-local statement about whether anything still holds this database open. Counting
///         <c>/dev/fd</c> gives the same answer and is process-wide, which under xUnit's parallel
///         collections is the intermittent that tracing already learned to avoid.
///     </para>
/// </remarks>
public class pooled_connection_release : IAsyncLifetime
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fisher-pool-" + Guid.NewGuid().ToString("n")[..8]);

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
            }
        }

        return ValueTask.CompletedTask;
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    ///     Whether anything still holds this tenant's database open.
    /// </summary>
    private bool IsOpen(string tenantId) => File.Exists(Path.Combine(_directory, $"{tenantId}.db-wal"));

    private DocumentStore StoreFor()
        => DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Sighting>();
            options.MultiTenantedDatabasesInDirectory(_directory);
        });

    private async Task UseAsync(DocumentStore store, string tenantId)
    {
        await using var session = store.LightweightSession(tenantId);
        session.Store(new Sighting { Id = Guid.NewGuid(), Species = "probe" });
        await session.SaveChangesAsync(Token);
    }

    /// <summary>
    ///     A tenant resolved but never used costs nothing, which is why there is no eviction here.
    /// </summary>
    /// <remarks>
    ///     The measurement that reframed the issue, kept as a test so the premise cannot quietly change
    ///     under a provider upgrade. If resolving a database ever starts opening one, this is where it
    ///     shows up — and eviction would then be worth revisiting.
    /// </remarks>
    [Fact]
    public void resolving_a_tenant_opens_nothing()
    {
        using var store = StoreFor();

        for (var i = 0; i < 50; i++)
        {
            store.Tenancy.DatabaseFor($"never-used-{i}");
        }

        // Not one connection between them, so not one file: a database is created on first use, not on
        // resolution. Fifty of them cost nothing at all, which is why there is no eviction here.
        Directory.GetFiles(_directory, "never-used-*").ShouldBeEmpty();
    }

    /// <summary>
    ///     Disposing a store returns its pooled connections.
    /// </summary>
    /// <remarks>
    ///     <b>This was leaking for every Fisher store, not only a multi-tenant one.</b> Disposing the
    ///     data source does not touch Microsoft.Data.Sqlite's pool, because that pool is keyed by
    ///     connection string and lives in a process-wide registry.
    /// </remarks>
    [Fact]
    public async Task disposing_a_store_releases_its_pooled_connections()
    {
        var store = StoreFor();

        await UseAsync(store, "one");
        await UseAsync(store, "two");

        // The session closed, but its connection went back to the pool rather than to the operating
        // system — which is what makes the database still open here.
        IsOpen("one").ShouldBeTrue();
        IsOpen("two").ShouldBeTrue();

        await store.DisposeAsync();

        IsOpen("one").ShouldBeFalse();
        IsOpen("two").ShouldBeFalse();
    }

    /// <summary>
    ///     Forgetting a tenant releases that tenant's connections and nobody else's.
    /// </summary>
    [Fact]
    public async Task forgetting_a_tenant_releases_it_and_leaves_the_others_alone()
    {
        await using var store = StoreFor();

        await UseAsync(store, "going");
        await UseAsync(store, "staying");

        var tenancy = store.Tenancy.ShouldBeOfType<DynamicTenancy>();

        IsOpen("going").ShouldBeTrue();

        (await tenancy.ForgetTenantAsync("going")).ShouldBeTrue();

        tenancy.AllDatabases().Select(x => x.Identifier).ShouldNotContain("going");
        IsOpen("going").ShouldBeFalse();

        // And nobody else's connections went with it.
        IsOpen("staying").ShouldBeTrue();

        // Forgetting one this store never resolved is a no-op rather than an error.
        (await tenancy.ForgetTenantAsync("never-seen")).ShouldBeFalse();

        // The other tenant is untouched and still works.
        await UseAsync(store, "staying");
    }

    /// <summary>
    ///     A forgotten tenant's data is still there, and it resolves again on the next request.
    /// </summary>
    /// <remarks>
    ///     Forgetting is about returning resources, not about removing a tenant — the same rule
    ///     everything else in this tenancy follows: Fisher never deletes a tenant's data.
    /// </remarks>
    [Fact]
    public async Task a_forgotten_tenant_comes_back_with_its_data()
    {
        await using var store = StoreFor();

        var id = Guid.NewGuid();

        await using (var session = store.LightweightSession("returning"))
        {
            session.Store(new Sighting { Id = id, Species = "Gannet" });
            await session.SaveChangesAsync(Token);
        }

        await store.Tenancy.ShouldBeOfType<DynamicTenancy>().ForgetTenantAsync("returning");

        await using var query = store.LightweightSession("returning");
        (await query.LoadAsync<Sighting>(id, Token)).ShouldNotBeNull();
    }

    /// <summary>
    ///     A session already holding a connection is unharmed when its database is disposed underneath
    ///     it.
    /// </summary>
    /// <remarks>
    ///     <b>The property that makes this safe rather than an <c>ObjectDisposedException</c>
    ///     generator</b>, and the reason it is <c>ClearPool</c> and not the banned <c>ClearAllPools</c>.
    ///     Verified against Microsoft.Data.Sqlite 10.0.9: clearing a pool leaves a connection that is
    ///     currently checked out working, and merely stops it being re-pooled when it closes. Without
    ///     that, forgetting a tenant while anything was mid-request would be a race with data loss on
    ///     the other side of it.
    /// </remarks>
    [Fact]
    public async Task a_session_in_flight_survives_its_tenant_being_forgotten()
    {
        await using var store = StoreFor();

        await UseAsync(store, "busy");

        await using var session = store.LightweightSession("busy");

        // Force the session to take its connection before the pool is cleared.
        session.Store(new Sighting { Id = Guid.NewGuid(), Species = "first" });
        await session.SaveChangesAsync(Token);

        await store.Tenancy.ShouldBeOfType<DynamicTenancy>().ForgetTenantAsync("busy");

        // Still writes, on the connection it was already holding.
        var id = Guid.NewGuid();
        session.Store(new Sighting { Id = id, Species = "after" });
        await session.SaveChangesAsync(Token);

        await using var query = store.LightweightSession("busy");
        (await query.LoadAsync<Sighting>(id, Token)).ShouldNotBeNull();
    }

    /// <remarks>
    ///     Two stores over one file share the pool, so disposing one releases the other's <em>idle</em>
    ///     connections. Harmless — they reopen on demand — and worth pinning because "one store's
    ///     cleanup takes out another" is exactly what makes <c>ClearAllPools</c> forbidden, and this is
    ///     the bounded version of the same thing.
    /// </remarks>
    [Fact]
    public async Task a_second_store_over_the_same_file_keeps_working()
    {
        using var database = TemporaryDatabase.Create("pool-sharing");

        var first = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Sighting>();
        });

        await first.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var second = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Sighting>();
        });

        var id = Guid.NewGuid();

        await using (var session = second.LightweightSession())
        {
            session.Store(new Sighting { Id = id, Species = "shared" });
            await session.SaveChangesAsync(Token);
        }

        await first.DisposeAsync();

        await using var query = second.LightweightSession();
        (await query.LoadAsync<Sighting>(id, Token)).ShouldNotBeNull();
    }
}
