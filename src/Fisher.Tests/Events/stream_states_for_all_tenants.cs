using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Documents;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Events;

/// <summary>
///     jasperfx#1079: <c>IEventStore.QueryStreamStatesForAllTenants()</c> and <c>StreamState.TenantId</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>What the issue left unproven, settled first.</b> fisher#475 pointed at the read-only tier's
///         fallback — a null tenant falls back to the tier's tenant — and asked whether the existing
///         store-wide read returns only the default tenant's rows on a conjoined store. It does:
///         <c>OpenReadOnlyEventStore().QueryStreamStates()</c> carries <c>tenant_id = '*DEFAULT*'</c>, so a
///         console wanting every tenant's streams had no member that gave them.
///         <c>the_store_wide_tier_reads_only_the_default_tenant</c> pins that, as the behaviour of a
///         default-tenant read rather than as a bug: the tier is the default tenant's, and that stays true.
///     </para>
///     <para>
///         Every fact reuses one stream id across two tenants, the shared suite's discipline: a reader
///         ignoring the tenant still answers correctly for whichever tenant owns the row it found.
///     </para>
/// </remarks>
public class stream_states_for_all_tenants
{
    private const string North = "north";
    private const string South = "south";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(TemporaryDatabase database, DocumentStore store)> StoreAsync(TenancyStyle style)
    {
        var database = TemporaryDatabase.Create("all-tenant-streams");

        var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = style;
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        return (database, store);
    }

    private static async Task AppendAsync(DocumentStore store, string? tenantId, Guid streamId, params object[] events)
    {
        await using var session = store.LightweightSession(tenantId);
        session.Events.StartStream<Quest>(streamId, events);
        await session.SaveChangesAsync(Token);
    }

    [Fact]
    public async Task the_store_wide_tier_reads_only_the_default_tenant()
    {
        var (database, store) = await StoreAsync(TenancyStyle.Conjoined);

        try
        {
            var shared = Guid.NewGuid();
            await AppendAsync(store, North, shared, new QuestStarted("Find the ring"));
            await AppendAsync(store, South, shared, new QuestStarted("Guard the shire"));
            await AppendAsync(store, null, Guid.NewGuid(), new QuestStarted("Default's own"));

            var tier = await ((IEventStore)store).OpenReadOnlyEventStore().QueryStreamStates().ToListAsync(Token);

            tier.ShouldHaveSingleItem().TenantId.ShouldBe(StorageConstants.DefaultTenantId);
        }
        finally
        {
            await store.DisposeAsync();
            database.Dispose();
        }
    }

    [Fact]
    public async Task every_tenants_streams_on_a_conjoined_store_each_naming_its_tenant()
    {
        var (database, store) = await StoreAsync(TenancyStyle.Conjoined);

        try
        {
            var shared = Guid.NewGuid();
            await AppendAsync(store, North, shared, new QuestStarted("Find the ring"), new MemberJoined("Frodo"));
            await AppendAsync(store, South, shared, new QuestStarted("Guard the shire"));
            await AppendAsync(store, null, Guid.NewGuid(), new QuestStarted("Default's own"));

            var all = await ((IEventStore)store).QueryStreamStatesForAllTenants().ToListAsync(Token);

            all.Count.ShouldBe(3);
            all.Select(x => x.TenantId).OrderBy(x => x, StringComparer.Ordinal)
                .ShouldBe([StorageConstants.DefaultTenantId, North, South]);
            all.Single(x => x.Id == shared && x.TenantId == North).Version.ShouldBe(2);
            all.Single(x => x.Id == shared && x.TenantId == South).Version.ShouldBe(1);
        }
        finally
        {
            await store.DisposeAsync();
            database.Dispose();
        }
    }

    [Fact]
    public async Task the_tenant_is_translatable_in_where_and_order_by()
    {
        var (database, store) = await StoreAsync(TenancyStyle.Conjoined);

        try
        {
            var shared = Guid.NewGuid();
            await AppendAsync(store, North, shared, new QuestStarted("Find the ring"));
            await AppendAsync(store, South, shared, new QuestStarted("Guard the shire"));
            await AppendAsync(store, South, Guid.NewGuid(), new QuestStarted("Guard the road"));

            var all = ((IEventStore)store).QueryStreamStatesForAllTenants();

            (await all.Where(x => x.TenantId == South).ToListAsync(Token)).Count.ShouldBe(2);
            (await all.Where(x => x.TenantId == North && x.Id == shared).CountAsync(Token)).ShouldBe(1);

            var ordered = await all.OrderByDescending(x => x.TenantId).ThenBy(x => x.Version).ToListAsync(Token);
            ordered.Select(x => x.TenantId).ShouldBe([South, South, North]);

            var page = await all.OrderBy(x => x.TenantId).Skip(1).Take(1).ToListAsync(Token);
            page.ShouldHaveSingleItem().TenantId.ShouldBe(South);
        }
        finally
        {
            await store.DisposeAsync();
            database.Dispose();
        }
    }

    /// <summary>
    ///     The tenant-scoped read names its tenant too, and filtering on it agrees with what it reports —
    ///     under conjoined tenancy from the column, and on a single-tenanted store as the default tenant.
    /// </summary>
    [Fact]
    public async Task tenant_scoped_and_single_tenanted_reads_report_their_tenant()
    {
        var (conjoinedFile, conjoined) = await StoreAsync(TenancyStyle.Conjoined);
        var (singleFile, single) = await StoreAsync(TenancyStyle.Single);

        try
        {
            var shared = Guid.NewGuid();
            await AppendAsync(conjoined, North, shared, new QuestStarted("Find the ring"));

            var north = await ((IEventStore)conjoined).OpenReadOnlyEventStore(North).QueryStreamStates()
                .Where(x => x.TenantId == North).ToListAsync(Token);
            north.ShouldHaveSingleItem().TenantId.ShouldBe(North);

            await AppendAsync(single, null, shared, new QuestStarted("Alone"));

            var tier = await ((IEventStore)single).OpenReadOnlyEventStore().QueryStreamStates().ToListAsync(Token);
            tier.ShouldHaveSingleItem().TenantId.ShouldBe(StorageConstants.DefaultTenantId);

            // On a single-tenanted store the all-tenants form is exactly the tenant-less one.
            var all = await ((IEventStore)single).QueryStreamStatesForAllTenants()
                .Where(x => x.TenantId == StorageConstants.DefaultTenantId).ToListAsync(Token);
            all.ShouldHaveSingleItem().Id.ShouldBe(shared);
        }
        finally
        {
            await conjoined.DisposeAsync();
            await single.DisposeAsync();
            conjoinedFile.Dispose();
            singleFile.Dispose();
        }
    }

    /// <summary>
    ///     Under database-per-tenant the stored <c>tenant_id</c> column holds the default for every row,
    ///     because the file is the tenant. The tenant reported — and the one a predicate compares — has to
    ///     be the file's, or <c>Where(x =&gt; x.TenantId == "north")</c> would match nothing in north's own
    ///     file.
    /// </summary>
    [Fact]
    public async Task under_database_per_tenant_a_tenant_scoped_read_reports_the_files_tenant()
    {
        using var northFile = TemporaryDatabase.Create("all-tenant-streams-north");
        using var southFile = TemporaryDatabase.Create("all-tenant-streams-south");
        using var defaultFile = TemporaryDatabase.Create("all-tenant-streams-default");

        await using var store = DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.MultiTenantedDatabases(databases => databases
                .AddTenant(StorageConstants.DefaultTenantId, defaultFile.ConnectionString)
                .AddTenant(North, northFile.ConnectionString)
                .AddTenant(South, southFile.ConnectionString));
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var shared = Guid.NewGuid();
        await AppendAsync(store, North, shared, new QuestStarted("Find the ring"));
        await AppendAsync(store, South, shared, new QuestStarted("Guard the shire"));

        var north = await ((IEventStore)store).OpenReadOnlyEventStore(North).QueryStreamStates()
            .Where(x => x.TenantId == North).ToListAsync(Token);

        north.ShouldHaveSingleItem().TenantId.ShouldBe(North);

        (await ((IEventStore)store).OpenReadOnlyEventStore(North).QueryStreamStates()
            .Where(x => x.TenantId == South).ToListAsync(Token)).ShouldBeEmpty();
    }

    /// <summary>
    ///     A file per tenant cannot be answered from one file, and Fisher does not fan out: refused, naming
    ///     the per-tenant opener — never the default file's streams presented as everyone's.
    /// </summary>
    [Fact]
    public async Task under_database_per_tenant_the_all_tenants_form_is_refused_by_name()
    {
        using var northFile = TemporaryDatabase.Create("all-tenant-streams-north");
        using var defaultFile = TemporaryDatabase.Create("all-tenant-streams-default");

        await using var store = DocumentStore.For(options =>
        {
            options.MultiTenantedDatabases(databases => databases
                .AddTenant(StorageConstants.DefaultTenantId, defaultFile.ConnectionString)
                .AddTenant(North, northFile.ConnectionString));
        });

        var ex = Should.Throw<NotSupportedException>(() => ((IEventStore)store).QueryStreamStatesForAllTenants());

        ex.Message.ShouldContain(nameof(IEventStore.QueryStreamStatesForAllTenants));
        ex.Message.ShouldContain("OpenReadOnlyEventStore(tenantId)");
    }
}
