using Fisher.Linq;
using Fisher.Tests.Events;
using JasperFx;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Documents;

/// <summary>
///     fisher#33 — <c>session.ForTenant(id)</c>, so one <c>SaveChangesAsync</c> writes for several
///     tenants in one transaction.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is the case where SQLite's single-writer model is the advantage rather than the
///         constraint.</b> The alternative is a session and a transaction per tenant, which on one
///         database file means taking the write lock N times in sequence and leaves a part-written
///         admin operation if the process dies between two of them. Here the cross-tenant write is
///         trivially atomic.
///     </para>
///     <para>
///         Every assertion checks <em>both</em> directions, which is the discipline
///         <c>ConjoinedEventTenancyCompliance</c> established: a store that leaks across tenants still
///         answers correctly for the tenant owning the data and misbehaves only for the other one.
///     </para>
/// </remarks>
public class cross_tenant_writes : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("cross-tenant");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Schema.For<Boat>().MultiTenanted();
            options.Schema.For<Harbour>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- documents ----

    [Fact]
    public async Task two_tenants_documents_commit_in_one_unit_of_work()
    {
        var northId = Guid.NewGuid();
        var southId = Guid.NewGuid();

        await using (var session = _store.LightweightSession("north"))
        {
            session.Store(new Boat { Id = northId, Name = "Northern Star" });
            session.ForTenant("south").Store(new Boat { Id = southId, Name = "Southern Cross" });

            await session.SaveChangesAsync(Token);
        }

        await using var north = _store.LightweightSession("north");
        await using var south = _store.LightweightSession("south");

        (await north.LoadAsync<Boat>(northId, Token))!.Name.ShouldBe("Northern Star");
        (await north.LoadAsync<Boat>(southId, Token)).ShouldBeNull();

        (await south.LoadAsync<Boat>(southId, Token))!.Name.ShouldBe("Southern Cross");
        (await south.LoadAsync<Boat>(northId, Token)).ShouldBeNull();
    }

    /// <remarks>
    ///     The same identity in two tenants is two documents, which is what conjoined tenancy means
    ///     and what a shared identity map would get wrong.
    /// </remarks>
    [Fact]
    public async Task one_identity_can_belong_to_both_tenants()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.IdentitySession("north"))
        {
            session.Store(new Boat { Id = id, Name = "North's" });
            session.ForTenant("south").Store(new Boat { Id = id, Name = "South's" });

            await session.SaveChangesAsync(Token);
        }

        await using var north = _store.LightweightSession("north");
        await using var south = _store.LightweightSession("south");

        (await north.LoadAsync<Boat>(id, Token))!.Name.ShouldBe("North's");
        (await south.LoadAsync<Boat>(id, Token))!.Name.ShouldBe("South's");
    }

    [Fact]
    public async Task a_read_through_the_scope_is_scoped_to_its_tenant()
    {
        var id = Guid.NewGuid();

        await using (var seed = _store.LightweightSession("south"))
        {
            seed.Store(new Boat { Id = id, Name = "Southern Cross" });
            await seed.SaveChangesAsync(Token);
        }

        await using var session = _store.LightweightSession("north");

        (await session.LoadAsync<Boat>(id, Token)).ShouldBeNull();
        (await session.ForTenant("south").LoadAsync<Boat>(id, Token))!.Name.ShouldBe("Southern Cross");

        (await session.Query<Boat>().ToListAsync(Token)).ShouldBeEmpty();
        (await session.ForTenant("south").Query<Boat>().ToListAsync(Token)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task a_deletion_through_the_scope_reaches_only_that_tenant()
    {
        var id = Guid.NewGuid();

        await using (var seed = _store.LightweightSession("north"))
        {
            seed.Store(new Boat { Id = id, Name = "North's" });
            seed.ForTenant("south").Store(new Boat { Id = id, Name = "South's" });
            await seed.SaveChangesAsync(Token);
        }

        await using (var session = _store.LightweightSession("north"))
        {
            session.ForTenant("south").Delete<Boat>(id);
            await session.SaveChangesAsync(Token);
        }

        await using var north = _store.LightweightSession("north");
        await using var south = _store.LightweightSession("south");

        (await north.LoadAsync<Boat>(id, Token)).ShouldNotBeNull();
        (await south.LoadAsync<Boat>(id, Token)).ShouldBeNull();
    }

    // ---- events ----

    [Fact]
    public async Task two_tenants_streams_commit_in_one_unit_of_work()
    {
        // Deliberately the same stream id in both tenants: under conjoined tenancy that is two
        // streams, and it is the case that fails loudly (ExistingStreamIdCollisionException) if the
        // tenancy plumbing is wrong.
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession("north"))
        {
            session.Events.StartStream(streamId, new QuestStarted("North"));
            session.ForTenant("south").Events.StartStream(streamId, new QuestStarted("South"));

            await session.SaveChangesAsync(Token);
        }

        await using var north = _store.LightweightSession("north");
        await using var south = _store.LightweightSession("south");

        var northEvents = await north.Events.FetchStreamAsync(streamId, token: Token);
        var southEvents = await south.Events.FetchStreamAsync(streamId, token: Token);

        northEvents.Single().Data.ShouldBeOfType<QuestStarted>().Name.ShouldBe("North");
        southEvents.Single().Data.ShouldBeOfType<QuestStarted>().Name.ShouldBe("South");
    }

    // ---- atomicity ----

    /// <remarks>
    ///     The property the whole feature exists for. The failure is planted in the <em>parent's</em>
    ///     half so that the scope's write is the one that has to be rolled back — the other way round
    ///     would pass even if the two were separate transactions committed in order.
    /// </remarks>
    [Fact]
    public async Task a_failure_leaves_neither_tenants_rows()
    {
        var existing = Guid.NewGuid();
        var southId = Guid.NewGuid();

        await using (var seed = _store.LightweightSession("north"))
        {
            seed.Store(new Boat { Id = existing, Name = "Already here" });
            await seed.SaveChangesAsync(Token);
        }

        await using (var session = _store.LightweightSession("north"))
        {
            session.ForTenant("south").Store(new Boat { Id = southId, Name = "Should not survive" });

            // An insert onto an id that already exists in this tenant: the batch fails.
            session.Insert(new Boat { Id = existing, Name = "Duplicate" });

            await Should.ThrowAsync<Exception>(async () => await session.SaveChangesAsync(Token));
        }

        await using var south = _store.LightweightSession("south");
        (await south.LoadAsync<Boat>(southId, Token)).ShouldBeNull();

        await using var north = _store.LightweightSession("north");
        (await north.LoadAsync<Boat>(existing, Token))!.Name.ShouldBe("Already here");
    }

    // ---- what a scope refuses ----

    [Fact]
    public async Task a_single_tenant_document_type_is_refused_by_name()
    {
        await using var session = _store.LightweightSession("north");

        var ex = Should.Throw<InvalidOperationException>(()
            => session.ForTenant("south").Store(new Harbour { Id = Guid.NewGuid(), Name = "Falmouth" }));

        ex.Message.ShouldContain("Harbour");
        ex.Message.ShouldContain("MultiTenanted");

        await Task.CompletedTask;
    }

    /// <remarks>
    ///     A second store, because the event store's tenancy style is a schema decision taken before
    ///     the tables are created and cannot be changed on this one.
    /// </remarks>
    [Fact]
    public async Task a_single_tenant_event_store_is_refused_by_name()
    {
        await using var database = TemporaryDatabase.Create("cross-tenant-single");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = store.LightweightSession("north");

        var ex = Should.Throw<InvalidOperationException>(()
            => session.ForTenant("south").Events.StartStream(Guid.NewGuid(), new QuestStarted("no")));

        ex.Message.ShouldContain("not multi-tenanted");
        ex.Message.ShouldContain("Conjoined");
    }

    [Fact]
    public async Task a_scope_cannot_commit()
    {
        await using var session = _store.LightweightSession("north");
        var scope = session.ForTenant("south");

        var ex = await Should.ThrowAsync<InvalidOperationException>(async ()
            => await ((IDocumentSession)scope).SaveChangesAsync(Token));

        ex.Message.ShouldContain("Parent");
    }

    // ---- the shape of a scope ----

    [Fact]
    public void a_scope_is_stable_flattened_and_knows_its_parent()
    {
        using var session = _store.LightweightSession("north");

        var south = session.ForTenant("south");

        south.TenantId.ShouldBe("south");
        south.Parent.ShouldBeSameAs(session);

        // Asked for twice, the same scope — so its queued events are collected once.
        session.ForTenant("south").ShouldBeSameAs(south);

        // A scope of a scope is a scope of the session, not a chain. ITenantOperations deliberately
        // does not offer ForTenant — this is reachable only through the cast, and is flattened so
        // that whoever finds the cast does not build a chain the parent never walks.
        ((IDocumentSession)south).ForTenant("east").Parent.ShouldBeSameAs(session);

        // And the session's own tenant is the session.
        session.ForTenant("north").ShouldBeSameAs(session);
    }

    /// <remarks>
    ///     A scope owns no connection, so disposing one — which is what writing <c>await using</c> out
    ///     of habit does — must not end the unit of work it exists to join.
    /// </remarks>
    [Fact]
    public async Task disposing_a_scope_leaves_the_session_usable()
    {
        var id = Guid.NewGuid();

        await using var session = _store.LightweightSession("north");

        await using (var scope = session.ForTenant("south"))
        {
            scope.Store(new Boat { Id = id, Name = "Southern Cross" });
        }

        await session.SaveChangesAsync(Token);

        await using var south = _store.LightweightSession("south");
        (await south.LoadAsync<Boat>(id, Token)).ShouldNotBeNull();
    }

    /// <remarks>
    ///     Correlation, causation, user and headers describe the unit of work rather than the tenant,
    ///     and there is one unit of work — so a scope reads and writes the session's.
    /// </remarks>
    [Fact]
    public async Task session_metadata_is_shared_with_the_scope()
    {
        await using var session = _store.LightweightSession("north");
        session.CorrelationId = "trace-1";

        var scope = session.ForTenant("south");
        scope.CorrelationId.ShouldBe("trace-1");

        scope.CausationId = "set-through-the-scope";
        session.CausationId.ShouldBe("set-through-the-scope");

        scope.SetHeader("who", "admin");
        session.Headers!["who"].ShouldBe("admin");

        await Task.CompletedTask;
    }
}

public class Boat
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class Harbour
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
