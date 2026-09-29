using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fisher.EntityFrameworkCore.Tests;

/// <summary>
///     An EF-backed projection under conjoined tenancy writes the tenant and keeps tenants apart
///     (fisher#334).
/// </summary>
/// <remarks>
///     <para>
///         The storage used to carry its tenant and write it nowhere, so under conjoined events every
///         tenant's same-id stream folded into one entity — the fisher#335 shape reached through EF, and
///         with nothing refusing the model that could not carry a tenant. Marten and Polecat both refuse
///         an entity that does not implement <see cref="ITenanted" /> and both test that the tenant
///         lands; Fisher had neither.
///     </para>
///     <para>
///         The entity is keyed on <c>(TenantId, Id)</c>, which is what lets two tenants share a stream
///         id — the key a Fisher document table uses for the same reason.
///     </para>
/// </remarks>
public class ef_core_tenancy : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("ef-tenancy");
    private readonly List<DocumentStore> _stores = [];
    private IProjectionDaemon? _daemon;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "create table Ledgers (TenantId text not null, Id text not null, Deposits integer not null, "
            + "primary key (TenantId, Id))";
        await command.ExecuteNonQueryAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_daemon is not null)
        {
            await _daemon.StopAllAsync();
            _daemon.Dispose();
        }

        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }

        _database.Dispose();
    }

    private TenantLedgerContext Context()
        => new(new DbContextOptionsBuilder<TenantLedgerContext>().UseSqlite(_database.ConnectionString).Options);

    private async Task<DocumentStore> StoreAsync(SnapshotLifecycle lifecycle)
    {
        var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;

            options.ProjectToEfCore<TenantLedger, Guid, TenantLedgerContext>("Ledgers", Context);
            options.Projections.Snapshot<TenantLedger>(lifecycle);
        });

        _stores.Add(store);
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        return store;
    }

    private static async Task DepositAsync(DocumentStore store, string tenant, Guid id, int count)
    {
        await using var session = store.LightweightSession(tenant);
        session.Events.Append(id, Enumerable.Range(0, count).Select(_ => (object)new Deposited()).ToArray());
        await session.SaveChangesAsync(Token);
    }

    private async Task<List<TenantLedger>> LedgersAsync()
    {
        await using var context = Context();
        return await context.Ledgers.AsNoTracking().OrderBy(x => x.TenantId).ToListAsync(Token);
    }

    [Fact]
    public async Task the_tenant_id_is_written_to_the_ef_table()
    {
        var store = await StoreAsync(SnapshotLifecycle.Inline);
        var id = Guid.NewGuid();

        await DepositAsync(store, "north", id, 2);

        var ledger = (await LedgersAsync()).ShouldHaveSingleItem();
        ledger.TenantId.ShouldBe("north");
        ledger.Id.ShouldBe(id);
        ledger.Deposits.ShouldBe(2);
    }

    /// <remarks>
    ///     The discriminating fact: one stream id, two tenants, two rows. An entity keyed on the id alone
    ///     with no tenant written is exactly what used to fold them into one.
    /// </remarks>
    [Fact]
    public async Task the_same_stream_id_in_two_tenants_keeps_two_isolated_rows()
    {
        var store = await StoreAsync(SnapshotLifecycle.Inline);
        var id = Guid.NewGuid();

        await DepositAsync(store, "north", id, 1);
        await DepositAsync(store, "south", id, 3);
        await DepositAsync(store, "north", id, 1);

        var ledgers = await LedgersAsync();
        ledgers.Select(x => (x.TenantId, x.Id, x.Deposits))
            .ShouldBe([("north", id, 2), ("south", id, 3)]);
    }

    /// <remarks>
    ///     The daemon builds the storage per tenant from the slice, where an inline write takes the
    ///     session's — two routes to the tenant, so both are worth a fact.
    /// </remarks>
    [Fact]
    public async Task the_daemon_keeps_two_tenants_sharing_a_stream_id_apart()
    {
        var store = await StoreAsync(SnapshotLifecycle.Async);
        var id = Guid.NewGuid();

        await DepositAsync(store, "north", id, 2);
        await DepositAsync(store, "south", id, 5);

        _daemon = await store.BuildProjectionDaemonAsync();
        await _daemon.StartAllAsync();
        await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

        var ledgers = await LedgersAsync();
        ledgers.Select(x => (x.TenantId, x.Deposits)).ShouldBe([("north", 2), ("south", 5)]);
    }

    [Fact]
    public void an_entity_that_cannot_carry_a_tenant_is_refused_under_conjoined_events()
    {
        var ex = Should.Throw<InvalidOperationException>(() => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;

            options.ProjectToEfCore<TallyEntity, Guid, TallyContext>("Tallies",
                () => new TallyContext(new DbContextOptionsBuilder<TallyContext>()
                    .UseSqlite(_database.ConnectionString).Options));
            options.Projections.Snapshot<TallyEntity>(SnapshotLifecycle.Inline);
        }));

        ex.Message.ShouldContain(typeof(TallyEntity).FullName!);
        ex.Message.ShouldContain("ITenanted");
    }
}

public class TenantLedgerContext : DbContext
{
    public TenantLedgerContext(DbContextOptions<TenantLedgerContext> options) : base(options)
    {
    }

    public DbSet<TenantLedger> Ledgers => Set<TenantLedger>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantLedger>(entity =>
        {
            entity.ToTable("Ledgers");
            entity.HasKey(x => new { x.TenantId, x.Id });
        });
    }
}

public class TenantLedger : ITenanted
{
    public Guid Id { get; set; }
    public string? TenantId { get; set; }
    public int Deposits { get; set; }

    public void Apply(Deposited _) => Deposits++;
}

public record Deposited;
