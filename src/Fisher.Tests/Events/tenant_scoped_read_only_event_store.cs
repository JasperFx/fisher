using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Documents;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Events;

/// <summary>
///     <c>IEventStore.OpenReadOnlyEventStore(tenantId)</c> (jasperfx#885) — the read-only tier opened
///     <em>in</em> one tenant's scope, which is what makes its tenant-less members usable under
///     tenancy at all.
/// </summary>
/// <remarks>
///     <para>
///         <c>ConjoinedEventTenancyCompliance.read_only_event_store_opened_for_a_tenant_scopes_the_tenant_less_reads</c>
///         covers the two <c>Fetch*</c> pairs and is green. What it cannot reach is
///         <c>QueryStreamStates</c>, and that is the member where the tier's tenant and the read's tenant
///         can disagree — the shared fact never calls it, so both halves below are Fisher's to pin.
///     </para>
///     <para>
///         <b>The hazard is that a tenant scope has two mechanisms and only one of them is a predicate.</b>
///         Under conjoined tenancy there is one file and the <c>tenant_id</c> term is the whole scope;
///         under database-per-tenant there is no term to add and the <em>file</em> is the scope. A reader
///         that got one right and the other wrong is silent in both directions: a predicate against the
///         wrong file matches nothing, and the right file read with no predicate reads every tenant in it.
///         So <c>QueryStreamStates()</c> on a tier opened for a tenant has to inherit that tenant, and the
///         provider has to open its session for it.
///     </para>
///     <para>
///         Every fact writes the <b>same stream id in both tenants</b>, following the shared suite's own
///         discipline: a reader that ignored the tenant still answers correctly for whichever tenant owns
///         the row it found, so distinct ids per tenant would pass against exactly the reader under test.
///     </para>
/// </remarks>
public class tenant_scoped_read_only_event_store
{
    private const string North = "north";
    private const string South = "south";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(TemporaryDatabase database, DocumentStore store)> ConjoinedStoreAsync()
    {
        var database = TemporaryDatabase.Create("readonly-tier-conjoined");

        var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            // A schema decision rather than a runtime one: StreamsTable and EventsTable read this when
            // they build their columns and their primary key.
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        return (database, store);
    }

    private static async Task AppendAsync(DocumentStore store, string tenantId, Guid streamId,
        params object[] events)
    {
        await using var session = store.LightweightSession(tenantId);

        session.Events.StartStream<Quest>(streamId, events);
        await session.SaveChangesAsync(Token);
    }

    /// <summary>
    ///     The tenant-less reads answer about the tenant the tier was opened for, in both directions.
    /// </summary>
    /// <remarks>
    ///     The Fisher-local twin of the shared fact, kept because it is the premise the
    ///     <c>QueryStreamStates</c> facts below rest on — if this regressed, those would fail for a reason
    ///     that had nothing to do with them.
    /// </remarks>
    [Fact]
    public async Task the_tenant_less_reads_answer_about_the_tier_s_tenant()
    {
        var (database, store) = await ConjoinedStoreAsync();

        try
        {
            var shared = Guid.NewGuid();

            await AppendAsync(store, North, shared,
                new QuestStarted("Find the ring"), new MemberJoined("Frodo"));
            await AppendAsync(store, South, shared, new QuestStarted("Guard the shire"));

            var forNorth = ((IEventStore)store).OpenReadOnlyEventStore(North);
            var forSouth = ((IEventStore)store).OpenReadOnlyEventStore(South);

            (await forNorth.FetchStreamStateAsync(shared, Token)).ShouldNotBeNull().Version.ShouldBe(2);
            (await forSouth.FetchStreamStateAsync(shared, Token)).ShouldNotBeNull().Version.ShouldBe(1);

            (await forNorth.FetchStreamAsync(shared, token: Token)).Count.ShouldBe(2);
            (await forSouth.FetchStreamAsync(shared, token: Token))
                .ShouldHaveSingleItem().Data.ShouldBeOfType<QuestStarted>().Name.ShouldBe("Guard the shire");
        }
        finally
        {
            await store.DisposeAsync();
            database.Dispose();
        }
    }

    /// <summary>
    ///     <c>QueryStreamStates()</c> with no argument inherits the tier's tenant rather than falling back
    ///     to the default one.
    /// </summary>
    /// <remarks>
    ///     This is the fact the whole class exists for. Before jasperfx#885's adoption the provider took
    ///     its tenant only from this member's own argument, so a tier opened for <c>north</c> answered
    ///     about <c>*DEFAULT*</c> here — a reader whose two halves disagreed about whose streams they
    ///     were reading, with nothing to say so. Asserted on the stream's <em>contents</em> as well as the
    ///     count, because a store holding one row per id would answer both counts alike.
    /// </remarks>
    [Fact]
    public async Task query_stream_states_inherits_the_tier_s_tenant()
    {
        var (database, store) = await ConjoinedStoreAsync();

        try
        {
            var shared = Guid.NewGuid();

            await AppendAsync(store, North, shared,
                new QuestStarted("Find the ring"), new MemberJoined("Frodo"));
            await AppendAsync(store, South, shared, new QuestStarted("Guard the shire"));

            // A second stream in north alone, so a reader that ignored the tenant reports three rather
            // than a plausible two.
            await AppendAsync(store, North, Guid.NewGuid(), new QuestStarted("Guard the road"));

            var north = await ((IEventStore)store).OpenReadOnlyEventStore(North)
                .QueryStreamStates()
                .ToListAsync(Token);

            north.Count.ShouldBe(2);
            north.Single(x => x.Id == shared).Version.ShouldBe(2);

            var south = await ((IEventStore)store).OpenReadOnlyEventStore(South)
                .QueryStreamStates()
                .ToListAsync(Token);

            south.ShouldHaveSingleItem().Version.ShouldBe(1);

            // And the store-global tier is unscoped, which is what the default overload has always meant.
            (await ((IEventStore)store).OpenReadOnlyEventStore().QueryStreamStates("north")
                .ToListAsync(Token)).Count.ShouldBe(2);
        }
        finally
        {
            await store.DisposeAsync();
            database.Dispose();
        }
    }

    /// <summary>
    ///     An explicit tenant on the member still wins over the tier's.
    /// </summary>
    /// <remarks>
    ///     <c>QueryStreamStates</c> is one of the two members on the tier that has somewhere to name a
    ///     tenant, so inheriting must not become overriding. Without this, the fix above could have been
    ///     written as "always use the tier's tenant" and nothing would have noticed.
    /// </remarks>
    [Fact]
    public async Task an_explicit_tenant_outranks_the_tier_s()
    {
        var (database, store) = await ConjoinedStoreAsync();

        try
        {
            await AppendAsync(store, North, Guid.NewGuid(), new QuestStarted("Find the ring"));
            await AppendAsync(store, South, Guid.NewGuid(), new QuestStarted("Guard the shire"));
            await AppendAsync(store, South, Guid.NewGuid(), new QuestStarted("Guard the road"));

            var southThroughNorthsTier = await ((IEventStore)store).OpenReadOnlyEventStore(North)
                .QueryStreamStates(South)
                .ToListAsync(Token);

            southThroughNorthsTier.Count.ShouldBe(2);
        }
        finally
        {
            await store.DisposeAsync();
            database.Dispose();
        }
    }

    /// <summary>
    ///     Under database-per-tenant the tier's tenant selects the <em>file</em>, and
    ///     <c>QueryStreamStates()</c> has to follow it there.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The other half of the hazard, and the one a conjoined test cannot see. There is no
    ///         <c>tenant_id</c> predicate to add here — every row in <c>north.db</c> is north's — so the
    ///         only thing that scopes the read is which database the session opens. The provider opened
    ///         its session for the <em>default</em> tenant until jasperfx#885's adoption, so a tier opened
    ///         for north read the default file: not a wrong row set but a wrong <em>database</em>, and on
    ///         a fresh store an empty one, which reads as "this tenant has no streams".
    ///     </para>
    ///     <para>
    ///         ⚠️ <b>This path is newly reachable.</b> <c>QueryStreamStates</c> used to refuse any tenant
    ///         on a store that was not conjoined, which refused it here too — where a tenant is the most
    ///         meaningful thing a caller can name. <c>DocumentStore.IsTenanted()</c> is the one predicate
    ///         both refusals now read, so the two cannot drift back apart.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task query_stream_states_follows_the_tier_s_tenant_to_its_own_database()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fisher-tier-tenants-" + Guid.NewGuid().ToString("n")[..8]);

        await using var store = DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.MultiTenantedDatabases(databases =>
                databases.InDirectory(directory).AddTenants(North, South));
        });

        try
        {
            await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

            // Asymmetric counts, so a reader answering from the wrong file cannot be right by luck.
            await AppendAsync(store, North, Guid.NewGuid(), new QuestStarted("Find the ring"));
            await AppendAsync(store, North, Guid.NewGuid(), new QuestStarted("Guard the road"));
            await AppendAsync(store, South, Guid.NewGuid(), new QuestStarted("Guard the shire"));

            var north = await ((IEventStore)store).OpenReadOnlyEventStore(North)
                .QueryStreamStates()
                .ToListAsync(Token);

            north.Count.ShouldBe(2);

            var south = await ((IEventStore)store).OpenReadOnlyEventStore(South)
                .QueryStreamStates()
                .ToListAsync(Token);

            south.ShouldHaveSingleItem();

            // The tenant-less reads agree with it, which is the premise the whole tier rests on.
            var southOnly = south.Single().Id;
            (await ((IEventStore)store).OpenReadOnlyEventStore(South)
                .FetchStreamStateAsync(southOnly, Token)).ShouldNotBeNull();
            (await ((IEventStore)store).OpenReadOnlyEventStore(North)
                .FetchStreamStateAsync(southOnly, Token)).ShouldBeNull();
        }
        finally
        {
            await store.DisposeAsync();

            if (Directory.Exists(directory))
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException)
                {
                    // A tenant file still held by a pooled connection. The test has said what it means
                    // to say; the directory is under the temp path.
                }
            }
        }
    }

    /// <summary>
    ///     A tenant on a store with no tenant dimension at all is refused by name, at the point the tier
    ///     is opened.
    /// </summary>
    /// <remarks>
    ///     The same rule <c>QueryStreamStates</c> already carried, moved up to the factory so a caller
    ///     learns at the opening rather than at the first read. Seeds data first, so it would fail against
    ///     a tier that answered from the unscoped tables even with the throw assertion removed — the
    ///     discipline the rest of <c>stream_state_queries</c> follows.
    /// </remarks>
    [Fact]
    public async Task a_tenant_on_a_tenantless_store_is_refused_not_unscoped()
    {
        using var database = TemporaryDatabase.Create("readonly-tier-tenantless");

        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Quest>(Guid.NewGuid(), new QuestStarted("Find the ring"));
            await session.SaveChangesAsync(Token);
        }

        var exception = Should.Throw<NotSupportedException>(
            () => ((IEventStore)store).OpenReadOnlyEventStore("acme"));

        exception.Message.ShouldContain("acme");
        exception.Message.ShouldContain("Conjoined");

        // A null tenant is the store-global tier and is unaffected.
        (await ((IEventStore)store).OpenReadOnlyEventStore(null).QueryStreamStates()
            .ToListAsync(Token)).ShouldHaveSingleItem();
    }
}
