using Fisher.Linq;
using JasperFx;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Documents;

/// <summary>
///     The read half of <c>ForTenant</c> under a <em>tracking</em> session — the shape polecat#554
///     turned out to be, ported here as a standing guard rather than as a fix.
/// </summary>
/// <remarks>
///     <para>
///         <b>What the Polecat bug was.</b> Every read member of its <c>NestedTenantSession</c>
///         delegated to the parent session with no tenant argument, so
///         <c>session.ForTenant("b").LoadAsync&lt;Doc&gt;(id)</c> answered with tenant <c>a</c>'s
///         document. It stayed quiet because the <em>writes</em> were already tenant-correct, and the
///         existing tenancy tests only ever asserted the write side. Fixing the queries was only half
///         the repair: both of Polecat's identity maps are keyed <c>(type, id)</c> with no tenant
///         component, so a shared map let one tenant's instance answer for another even once the SQL
///         was right — marten#4801 arrived at by a second route.
///     </para>
///     <para>
///         <b>Why Fisher does not have it.</b> A tenant scope here is a whole second
///         <see cref="Fisher.Internal.FisherSession" />, not a delegating facade: it shares the
///         parent's connection and operation queue and nothing else. The identity map, the change
///         trackers and the version tracker are all instance fields on the scope, so there is no
///         per-member delegation to forget a tenant argument in and no map keyed without a tenant.
///         That is a claim about a design, though, and <c>cross_tenant_writes</c> only exercises it on
///         a <c>LightweightSession</c> — where <c>DocumentTracking.None</c> means no identity map is
///         resolved at all, so the half of polecat#554 that needed the second fix is precisely the
///         half those tests cannot see. Everything here runs on <c>IdentitySession</c> and
///         <c>DirtyTrackedSession</c> for that reason.
///     </para>
///     <para>
///         <b>The order of operations in each test is load-bearing.</b> A cache that answers for the
///         wrong tenant only does so once something has populated it, so every test reads or writes
///         through one tenant <em>first</em> and then asks the other. Reversing them would pass
///         against a completely broken map.
///     </para>
///     <para>
///         marten#4947 is respected from the other side: Fisher refuses a non-multi-tenanted type
///         through <c>ForTenant</c> by name rather than isolating state for it, so the
///         over-isolation regression #4947 fixed has no way to arise. That refusal is pinned by
///         <c>cross_tenant_writes.a_single_tenant_document_type_is_refused_by_name</c>.
///     </para>
/// </remarks>
public class tenant_scope_read_isolation : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("tenant-scope-reads");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Schema.For<Boat>().MultiTenanted();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    ///     Seed the same identity into both tenants with different names, which is what makes every
    ///     assertion below able to tell the two apart.
    /// </summary>
    private async Task SeedBothAsync(Guid id)
    {
        await using var seed = _store.LightweightSession("north");

        seed.Store(new Boat { Id = id, Name = "North's" });
        seed.ForTenant("south").Store(new Boat { Id = id, Name = "South's" });

        await seed.SaveChangesAsync(Token);
    }

    [Fact]
    public async Task the_parents_cached_document_does_not_answer_for_the_scope()
    {
        var id = Guid.NewGuid();
        await SeedBothAsync(id);

        await using var session = _store.IdentitySession("north");

        // Populates the parent's identity map under this id first. This is the step that made
        // polecat#554's second half fail after its queries were already fixed.
        (await session.LoadAsync<Boat>(id, Token))!.Name.ShouldBe("North's");

        (await session.ForTenant("south").LoadAsync<Boat>(id, Token))!.Name.ShouldBe("South's");
    }

    [Fact]
    public async Task the_scopes_cached_document_does_not_answer_for_the_parent()
    {
        var id = Guid.NewGuid();
        await SeedBothAsync(id);

        await using var session = _store.IdentitySession("north");

        // The other direction. A shared map poisoned by the scope is just as wrong, and is the way
        // round an application hits when an admin operation reads another tenant before its own.
        (await session.ForTenant("south").LoadAsync<Boat>(id, Token))!.Name.ShouldBe("South's");

        (await session.LoadAsync<Boat>(id, Token))!.Name.ShouldBe("North's");
    }

    [Fact]
    public async Task two_scopes_do_not_share_an_identity_map_entry()
    {
        var id = Guid.NewGuid();
        await SeedBothAsync(id);

        // A third tenant, so neither scope is the session's own and the parent is not involved.
        await using var third = _store.LightweightSession("east");
        third.Store(new Boat { Id = id, Name = "East's" });
        await third.SaveChangesAsync(Token);

        await using var session = _store.IdentitySession("north");

        (await session.ForTenant("south").LoadAsync<Boat>(id, Token))!.Name.ShouldBe("South's");
        (await session.ForTenant("east").LoadAsync<Boat>(id, Token))!.Name.ShouldBe("East's");

        // And back, because a map that is merely last-writer-wins would pass the two above.
        (await session.ForTenant("south").LoadAsync<Boat>(id, Token))!.Name.ShouldBe("South's");
    }

    [Fact]
    public async Task a_scope_read_of_a_missing_document_does_not_fall_through_to_the_parent()
    {
        var id = Guid.NewGuid();

        await using (var seed = _store.LightweightSession("north"))
        {
            seed.Store(new Boat { Id = id, Name = "North's" });
            await seed.SaveChangesAsync(Token);
        }

        await using var session = _store.IdentitySession("north");

        (await session.LoadAsync<Boat>(id, Token))!.Name.ShouldBe("North's");

        // Nothing is stored for "south" at all. Every read shape has to say so rather than handing
        // back what the parent has — CheckExistsAsync worst of all, since "does this tenant own that
        // id?" is the natural spelling of an authorization gate.
        (await session.ForTenant("south").LoadAsync<Boat>(id, Token)).ShouldBeNull();
        (await session.ForTenant("south").CheckExistsAsync<Boat>(id, Token)).ShouldBeFalse();
        (await session.ForTenant("south").LoadManyAsync<Boat>(Token, id)).ShouldBeEmpty();
        (await session.ForTenant("south").Query<Boat>().ToListAsync(Token)).ShouldBeEmpty();
    }

    [Fact]
    public async Task every_read_shape_through_a_scope_answers_for_the_scopes_tenant()
    {
        var id = Guid.NewGuid();
        await SeedBothAsync(id);

        await using var session = _store.IdentitySession("north");

        // Warm the parent on every shape first, so a delegating read has something wrong to return.
        (await session.LoadAsync<Boat>(id, Token))!.Name.ShouldBe("North's");
        (await session.Query<Boat>().ToListAsync(Token)).Single().Name.ShouldBe("North's");

        var scope = session.ForTenant("south");

        (await scope.LoadAsync<Boat>(id, Token))!.Name.ShouldBe("South's");
        (await scope.LoadManyAsync<Boat>(Token, id)).Single().Name.ShouldBe("South's");
        (await scope.CheckExistsAsync<Boat>(id, Token)).ShouldBeTrue();
        (await scope.Query<Boat>().ToListAsync(Token)).Single().Name.ShouldBe("South's");

        // Substrings rather than the whole name: the serializer escapes the apostrophe, so
        // "South's" is not literally present in the JSON.
        var json = await scope.LoadJsonAsync<Boat>(id, Token);
        json.ShouldNotBeNull();
        json.ShouldContain("South");
        json.ShouldNotContain("North");

        // And the parent still answers for itself afterwards, which is the half a per-member tenant
        // argument gets right and a shared cache gets wrong.
        (await session.LoadAsync<Boat>(id, Token))!.Name.ShouldBe("North's");
    }

    [Fact]
    public async Task an_uncommitted_write_through_a_scope_is_visible_only_to_that_scope()
    {
        var id = Guid.NewGuid();
        await SeedBothAsync(id);

        await using var session = _store.IdentitySession("north");

        session.ForTenant("south").Store(new Boat { Id = id, Name = "South's, amended" });

        // Not yet committed. The scope must see its own pending write and the parent must not — the
        // property marten#4947 protects, checked here where isolation is correct rather than where
        // it would be over-isolation.
        (await session.ForTenant("south").LoadAsync<Boat>(id, Token))!.Name.ShouldBe("South's, amended");
        (await session.LoadAsync<Boat>(id, Token))!.Name.ShouldBe("North's");
    }

    [Fact]
    public async Task a_dirty_tracked_scope_does_not_track_the_parents_document()
    {
        var id = Guid.NewGuid();
        await SeedBothAsync(id);

        await using (var session = _store.DirtyTrackedSession("north"))
        {
            var north = await session.LoadAsync<Boat>(id, Token);
            var south = await session.ForTenant("south").LoadAsync<Boat>(id, Token);

            // Two distinct instances, or dirty tracking would flush one tenant's edits onto the
            // other's row.
            ReferenceEquals(north, south).ShouldBeFalse();

            south!.Name = "South's, renamed";

            await session.SaveChangesAsync(Token);
        }

        await using var north2 = _store.LightweightSession("north");
        await using var south2 = _store.LightweightSession("south");

        (await north2.LoadAsync<Boat>(id, Token))!.Name.ShouldBe("North's");
        (await south2.LoadAsync<Boat>(id, Token))!.Name.ShouldBe("South's, renamed");
    }

    /// <remarks>
    ///     The marten#4801 guard rail. Its first cut isolated <c>ForTenant(ownTenant)</c> from the
    ///     session as well, which broke the case where the two must agree — including seeing the
    ///     session's own uncommitted writes.
    /// </remarks>
    [Fact]
    public async Task a_scope_of_the_sessions_own_tenant_is_the_session()
    {
        var id = Guid.NewGuid();

        await using var session = _store.IdentitySession("north");

        session.ForTenant("north").ShouldBeSameAs(session);

        session.Store(new Boat { Id = id, Name = "North's" });

        (await session.ForTenant("north").LoadAsync<Boat>(id, Token))!.Name.ShouldBe("North's");
    }
}
