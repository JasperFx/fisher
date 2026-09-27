using Fisher.Linq;
using Fisher.Linq.SoftDeletes;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Tags;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Configuration;

/// <summary>
///     The conjoined-tenancy scenarios fisher#335 found with no test, one fact each.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every fact reuses one identity across two tenants and checks both directions</b>, the two
///         habits <c>DocumentConjoinedTenancyCompliance</c> made standard (jasperfx#898). A store keying on
///         the id alone does not fail loudly — it folds the two writes into one row — so distinct ids per
///         tenant pass on exactly the broken store; and a leaking store still answers correctly for
///         whichever tenant owns the data, so one direction proves nothing.
///     </para>
///     <para>
///         These are the operations the shared suite deliberately leaves out of scope (bulk insert,
///         patching, soft deletes, full-text search, DCB tags), so they are Fisher's to pin.
///     </para>
/// </remarks>
public class conjoined_tenancy_coverage : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("conjoined-coverage");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string North = "north";
    private const string South = "south";

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Events.RegisterTagType<HarbourId>("harbour");

            options.Schema.For<Berth>().MultiTenanted().SoftDeleted();
            options.Schema.For<Logbook>().MultiTenanted().FullTextIndex(x => x.Entry);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task StoreBothAsync(Guid id, Func<string, Berth> berth)
    {
        foreach (var tenant in new[] { North, South })
        {
            await using var session = _store.LightweightSession(tenant);
            session.Store(berth(tenant));
            await session.SaveChangesAsync(Token);
        }
    }

    private async Task<Berth?> LoadAsync(string tenant, Guid id)
    {
        await using var query = _store.QuerySession(tenant);
        return await query.LoadAsync<Berth>(id, Token);
    }

    // ---- bulk insert ----

    [Fact]
    public async Task bulk_insert_writes_under_the_named_tenant_only()
    {
        var id = Guid.NewGuid();

        await _store.Advanced.BulkInsertAsync([new Berth { Id = id, Vessel = "Pequod" }], tenantId: North,
            token: Token);

        (await LoadAsync(North, id))!.Vessel.ShouldBe("Pequod");
        (await LoadAsync(South, id)).ShouldBeNull();
    }

    /// <remarks>
    ///     The duplicate probe scopes by tenant because a conjoined table keys on <c>(tenant_id, id)</c>
    ///     (fisher#53) — so an id another tenant holds is not a duplicate, and skipping it would silently
    ///     drop this tenant's document.
    /// </remarks>
    [Fact]
    public async Task ignore_duplicates_does_not_treat_another_tenants_id_as_a_duplicate()
    {
        var id = Guid.NewGuid();

        await _store.Advanced.BulkInsertAsync([new Berth { Id = id, Vessel = "south's" }], tenantId: South,
            token: Token);

        await _store.Advanced.BulkInsertAsync([new Berth { Id = id, Vessel = "north's" }],
            BulkInsertMode.IgnoreDuplicates, tenantId: North, token: Token);

        (await LoadAsync(North, id))!.Vessel.ShouldBe("north's");
        (await LoadAsync(South, id))!.Vessel.ShouldBe("south's");
    }

    // ---- patching, soft deletes, DeleteWhere ----

    [Fact]
    public async Task a_patch_reaches_only_the_session_tenants_row()
    {
        var id = Guid.NewGuid();
        await StoreBothAsync(id, tenant => new Berth { Id = id, Vessel = tenant });

        await using (var session = _store.LightweightSession(North))
        {
            session.Patch<Berth>(id).Set(x => x.Vessel, "patched");
            await session.SaveChangesAsync(Token);
        }

        (await LoadAsync(North, id))!.Vessel.ShouldBe("patched");
        (await LoadAsync(South, id))!.Vessel.ShouldBe(South);
    }

    [Fact]
    public async Task a_soft_delete_reaches_only_the_session_tenants_row()
    {
        var id = Guid.NewGuid();
        await StoreBothAsync(id, tenant => new Berth { Id = id, Vessel = tenant });

        await using (var session = _store.LightweightSession(North))
        {
            session.Delete<Berth>(id);
            await session.SaveChangesAsync(Token);
        }

        (await LoadAsync(North, id)).ShouldBeNull();
        (await LoadAsync(South, id))!.Vessel.ShouldBe(South);

        await using var north = _store.QuerySession(North);
        (await north.Query<Berth>().IsDeleted().ToListAsync(Token)).ShouldHaveSingleItem().Id.ShouldBe(id);

        await using var south = _store.QuerySession(South);
        (await south.Query<Berth>().IsDeleted().ToListAsync(Token)).ShouldBeEmpty();
    }

    [Fact]
    public async Task delete_where_reaches_only_the_session_tenants_rows()
    {
        var id = Guid.NewGuid();
        await StoreBothAsync(id, tenant => new Berth { Id = id, Vessel = "Pequod" });

        await using (var session = _store.LightweightSession(North))
        {
            session.HardDeleteWhere<Berth>(x => x.Vessel == "Pequod");
            await session.SaveChangesAsync(Token);
        }

        await using var north = _store.QuerySession(North);
        (await north.Query<Berth>().MaybeDeleted().CountAsync(Token)).ShouldBe(0);

        (await LoadAsync(South, id)).ShouldNotBeNull();
    }

    // ---- full-text search ----

    /// <remarks>
    ///     The polecat#625 shape: the index lives in a table of its own, so the tenant has to reach the
    ///     match through the join back to the document rather than being assumed from it. Every search
    ///     style is a different MATCH expression over that one join, so each is checked.
    /// </remarks>
    [Theory]
    [InlineData("plain")]
    [InlineData("phrase")]
    [InlineData("prefix")]
    public async Task a_full_text_search_answers_only_for_the_session_tenant(string style)
    {
        var id = Guid.NewGuid();

        foreach (var tenant in new[] { North, South })
        {
            await using var session = _store.LightweightSession(tenant);
            session.Store(new Logbook { Id = id, Entry = $"whale sighted off the {tenant} cape" });
            await session.SaveChangesAsync(Token);
        }

        foreach (var tenant in new[] { North, South })
        {
            await using var query = _store.QuerySession(tenant);

            var found = style switch
            {
                "plain" => await query.Query<Logbook>().Where(x => x.PlainTextSearch("whale sighted"))
                    .ToListAsync(Token),
                "phrase" => await query.Query<Logbook>().Where(x => x.PhraseSearch("whale sighted"))
                    .ToListAsync(Token),
                _ => await query.Query<Logbook>().Where(x => x.PrefixSearch("wha")).ToListAsync(Token)
            };

            found.ShouldHaveSingleItem().Entry.ShouldContain(tenant);
        }
    }

    // ---- DCB tags ----

    /// <remarks>
    ///     The local replacement for the gated-off <c>DcbHasTagLinqCompliance</c> tenancy fact
    ///     (<c>SupportsHasTagLinqPredicates</c> is false — Fisher has no event queryable). One tag value,
    ///     two tenants, and each tenant's read sees its own event only.
    /// </remarks>
    [Fact]
    public async Task a_tag_query_answers_only_for_the_session_tenant()
    {
        var harbour = new HarbourId(Guid.NewGuid());
        var streamId = Guid.NewGuid();

        foreach (var tenant in new[] { North, South })
        {
            await using var session = _store.LightweightSession(tenant);
            var docked = session.Events.BuildEvent(new VesselDocked(tenant));
            docked.WithTag(harbour);
            session.Events.StartStream(streamId, docked);
            await session.SaveChangesAsync(Token);
        }

        foreach (var tenant in new[] { North, South })
        {
            await using var query = _store.LightweightSession(tenant);
            var events = await query.Events.QueryByTagsAsync(new EventTagQuery().Or<HarbourId>(harbour), Token);

            events.ShouldHaveSingleItem().Data.ShouldBe(new VesselDocked(tenant));
        }

        await using var elsewhere = _store.LightweightSession("west");
        (await elsewhere.Events.EventsExistAsync(new EventTagQuery().Or<HarbourId>(harbour), Token))
            .ShouldBeFalse();
    }
}

public class Berth
{
    public Guid Id { get; set; }
    public string Vessel { get; set; } = "";
}

public class Logbook
{
    public Guid Id { get; set; }
    public string Entry { get; set; } = "";
}

public record HarbourId(Guid Value);

public record VesselDocked(string Tenant);
