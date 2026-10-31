using JasperFx;
using Microsoft.Data.Sqlite;
using Weasel.Core;

namespace Fisher.Tests.Documents;

public class Warehouse
{
    public Guid Id { get; set; }
}

public class Pallet
{
    public Guid Id { get; set; }
    public Guid? WarehouseId { get; set; }
}

/// <summary>
///     fisher#488, found checking marten#5672. A conjoined table's primary key is
///     <c>(tenant_id, id)</c>, so a key referencing <c>id</c> alone made every write fail with
///     <c>foreign key mismatch</c>.
/// </summary>
public class foreign_keys_between_tenanted_documents : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("tenanted-keys");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Warehouse>().MultiTenanted();
            options.Schema.For<Pallet>().MultiTenanted()
                .ForeignKey<Warehouse>(x => x.WarehouseId, CascadeAction.Cascade);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task WriteAsync<T>(string tenant, T document) where T : notnull
    {
        await using var session = _store.LightweightSession(tenant);
        session.Store(document);
        await session.SaveChangesAsync(Token);
    }

    private static bool IsForeignKeyFailure(SqliteException e)
        => e.SqliteErrorCode == 19 && e.Message.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task a_child_writes_against_a_parent_in_its_own_tenant()
    {
        var warehouse = new Warehouse { Id = Guid.NewGuid() };
        await WriteAsync("north", warehouse);

        var pallet = new Pallet { Id = Guid.NewGuid(), WarehouseId = warehouse.Id };
        await WriteAsync("north", pallet);

        await using var session = _store.LightweightSession("north");
        (await session.LoadAsync<Pallet>(pallet.Id)).ShouldNotBeNull();
    }

    [Fact]
    public async Task an_orphan_is_refused()
    {
        var ex = await Should.ThrowAsync<SqliteException>(() =>
            WriteAsync("north", new Pallet { Id = Guid.NewGuid(), WarehouseId = Guid.NewGuid() }));

        IsForeignKeyFailure(ex).ShouldBeTrue(ex.Message);
    }

    /// <summary>What a key on <c>id</c> alone could never have said, even if SQLite had allowed it.</summary>
    [Fact]
    public async Task a_child_cannot_reference_another_tenants_parent()
    {
        var warehouse = new Warehouse { Id = Guid.NewGuid() };
        await WriteAsync("north", warehouse);

        var ex = await Should.ThrowAsync<SqliteException>(() =>
            WriteAsync("south", new Pallet { Id = Guid.NewGuid(), WarehouseId = warehouse.Id }));

        IsForeignKeyFailure(ex).ShouldBeTrue(ex.Message);
    }

    [Fact]
    public async Task two_tenants_can_share_a_parent_id_and_a_cascade_stays_in_its_tenant()
    {
        var shared = Guid.NewGuid();
        var northPallet = Guid.NewGuid();
        var southPallet = Guid.NewGuid();

        await WriteAsync("north", new Warehouse { Id = shared });
        await WriteAsync("south", new Warehouse { Id = shared });
        await WriteAsync("north", new Pallet { Id = northPallet, WarehouseId = shared });
        await WriteAsync("south", new Pallet { Id = southPallet, WarehouseId = shared });

        await using (var session = _store.LightweightSession("north"))
        {
            session.Delete<Warehouse>(shared);
            await session.SaveChangesAsync(Token);
        }

        await using var north = _store.LightweightSession("north");
        (await north.LoadAsync<Pallet>(northPallet)).ShouldBeNull();

        await using var south = _store.LightweightSession("south");
        (await south.LoadAsync<Pallet>(southPallet)).ShouldNotBeNull();
    }

    [Fact]
    public async Task an_absent_key_is_unconstrained()
        => await WriteAsync("north", new Pallet { Id = Guid.NewGuid() });

    /// <summary>Weasel has to read the composite key back the way it was declared, or this never converges.</summary>
    [Fact]
    public async Task nothing_is_outstanding_after_applying_it()
        => (await _store.Advanced.CreateMigrationAsync(Token)).Difference.ShouldBe(SchemaPatchDifference.None);

    [Fact]
    public async Task cleaning_clears_both_tables_in_key_order()
    {
        var warehouse = new Warehouse { Id = Guid.NewGuid() };
        await WriteAsync("north", warehouse);
        await WriteAsync("north", new Pallet { Id = Guid.NewGuid(), WarehouseId = warehouse.Id });

        await _store.Advanced.Clean.DeleteAllDocumentsAsync(Token);

        await using var session = _store.LightweightSession("north");
        (await session.LoadAsync<Warehouse>(warehouse.Id)).ShouldBeNull();
    }

    [Fact]
    public void a_conjoined_parent_with_a_single_tenant_child_is_refused_by_name()
    {
        var ex = Should.Throw<InvalidOperationException>(() => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.Schema.For<Warehouse>().MultiTenanted();
            options.Schema.For<Pallet>().ForeignKey<Warehouse>(x => x.WarehouseId);
        }));

        ex.Message.ShouldContain("Pallet.WarehouseId");
        ex.Message.ShouldContain("MultiTenanted()");
    }

    [Fact]
    public void set_null_would_clear_the_tenant_and_is_refused_by_name()
    {
        var ex = Should.Throw<InvalidOperationException>(() => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.Schema.For<Warehouse>().MultiTenanted();
            options.Schema.For<Pallet>().MultiTenanted()
                .ForeignKey<Warehouse>(x => x.WarehouseId, CascadeAction.SetNull);
        }));

        ex.Message.ShouldContain("tenant_id");
    }

    /// <summary>The one mixed shape that has always been sound: every tenant shares the parent.</summary>
    [Fact]
    public async Task a_conjoined_child_of_a_single_tenant_parent_keeps_working()
    {
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.DatabaseSchemaName = "mixed";
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Warehouse>();
            options.Schema.For<Pallet>().MultiTenanted().ForeignKey<Warehouse>(x => x.WarehouseId);
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var warehouse = new Warehouse { Id = Guid.NewGuid() };

        await using (var session = store.LightweightSession())
        {
            session.Store(warehouse);
            await session.SaveChangesAsync(Token);
        }

        await using var tenant = store.LightweightSession("north");
        tenant.Store(new Pallet { Id = Guid.NewGuid(), WarehouseId = warehouse.Id });
        await tenant.SaveChangesAsync(Token);
    }
}
