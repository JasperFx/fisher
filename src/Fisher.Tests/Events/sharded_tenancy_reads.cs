using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Events;

/// <summary>
///     fisher#252 — <b>sharded</b> tenancy: several tenants co-located in one file, conjoined, with
///     more than one file. The cell where Fisher's two tenancy axes meet, and the one neither of its
///     existing test classes covered.
/// </summary>
/// <remarks>
///     <para>
///         <c>explorer_reads_across_databases</c> covers database-per-tenant (a file each, no
///         co-location) and <c>explorer_reads_under_conjoined_tenancy</c> covers one file with several
///         tenants. <b>Sharding is both at once</b>, and it is expressible — <c>MultiTenantedDatabases</c>
///         plus <c>TenancyStyle.Conjoined</c>, with two tenants naming one connection string — but
///         nothing in Fisher had ever been pointed at it, because fisher#47's whole framing is "a
///         tenant is a file".
///     </para>
///     <para>
///         <b>It was wrong in two ways at once, and enrolling jasperfx#810's sharded arm is what
///         found it</b> — though not by failing: neither arm exercises a <em>store-global</em> listing
///         on a sharded store, so both were green over the defect. <c>SeparateDatabaseTenancy</c> built
///         one <c>FisherDatabase</c> per <em>tenant</em>, so a shared file appeared once per tenant in
///         <c>AllDatabases()</c> and the explorer's fan-out read it that many times; and each of those
///         instances claimed the file belonged to <em>its</em> tenant, so fisher#240's attribution
///         stamped every row with whichever tenant's instance happened to read it.
///     </para>
///     <para>
///         The result was the shape a cross-tenant bug always has here: <b>every stream reported once
///         per co-located tenant, each copy attributed to the wrong one</b> — two streams coming back
///         as four rows, half of them lying about whose they were.
///     </para>
///     <para>
///         <b>The explorer needed no change.</b> <c>GetRecentStreamsAsync</c>'s
///         <c>database.TenantId is null</c> branch — keep the row's own <c>tenant_id</c> rather than
///         stamping the file's tenant — was correct all along and was simply unreachable, because
///         nothing ever built a database with a null tenant. One <c>FisherDatabase</c> per file makes
///         it reachable, which is why the fix is entirely in the tenancy's construction.
///     </para>
/// </remarks>
public class sharded_tenancy_reads : IAsyncLifetime
{
    private readonly TemporaryDatabase _shardOne = TemporaryDatabase.Create("sharded-one");
    private readonly TemporaryDatabase _shardTwo = TemporaryDatabase.Create("sharded-two");

    private DocumentStore _store = null!;

    private readonly Guid _acme = Guid.NewGuid();
    private readonly Guid _globex = Guid.NewGuid();
    private readonly Guid _initech = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;

            // Two tenants in shard one, one alone in shard two — which is what makes the store
            // genuinely multi-database *and* genuinely co-located at the same time.
            options.MultiTenantedDatabases(databases => databases
                .AddTenant("acme", _shardOne.ConnectionString)
                .AddTenant("globex", _shardOne.ConnectionString)
                .AddTenant("initech", _shardTwo.ConnectionString));
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await AppendAsync("acme", _acme, "Chart the Minch");
        await AppendAsync("globex", _globex, "Chart the Solent");
        await AppendAsync("initech", _initech, "Chart the Sound");
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _shardOne.Dispose();
        _shardTwo.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private IEventStore TheExplorer => _store;

    private async Task AppendAsync(string tenantId, Guid streamId, string title)
    {
        await using var session = _store.LightweightSession(tenantId);
        session.Events.StartStream<Quest>(streamId, new QuestStarted(title));
        await session.SaveChangesAsync(Token);
    }

    /// <summary>
    ///     There are two files, not three databases.
    /// </summary>
    /// <remarks>
    ///     The precondition every fact below rests on, and the half of the defect that is visible
    ///     without reading a single row. Three tenants over two files used to report three databases —
    ///     one of them the same file twice, with two connection pools over it.
    /// </remarks>
    [Fact]
    public async Task the_databases_are_the_files_rather_than_the_tenants()
    {
        var databases = await TheExplorer.AllDatabases();

        databases.Count.ShouldBe(2);
        TheExplorer.DatabaseCardinality.ShouldBe(DatabaseCardinality.StaticMultiple);
    }

    /// <summary>
    ///     A shared file reports <b>no</b> tenant of its own; a file with one tenant reports that one.
    /// </summary>
    /// <remarks>
    ///     This is the fact the attribution rests on. <c>FisherDatabase.TenantId</c> means "the tenant
    ///     whose data this file holds", and a file holding two tenants' data cannot answer it — so
    ///     answering null is not a gap but the correct answer, and it is what routes the explorer to
    ///     the row's own <c>tenant_id</c> column instead.
    /// </remarks>
    [Fact]
    public async Task a_shared_file_claims_no_tenant_and_a_dedicated_one_does()
    {
        var databases = (await TheExplorer.AllDatabases()).Cast<Fisher.Storage.FisherDatabase>().ToList();

        var shared = databases.Single(x => x.Identifier.Contains('+'));
        var dedicated = databases.Single(x => x.Identifier == "initech");

        shared.TenantId.ShouldBeNull();
        dedicated.TenantId.ShouldBe("initech");
    }

    /// <summary>
    ///     <b>The headline.</b> A store-global listing reports each stream once, attributed to the
    ///     tenant that actually wrote it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Both halves fail against the old behaviour, and they fail differently: the count is the
    ///         duplicate file being read twice, and the attribution is the file claiming a tenant it
    ///         does not own. Asserting only the count would pass against a store that deduplicated and
    ///         still lied about whose stream it was.
    ///     </para>
    ///     <para>
    ///         <b>Neither compliance arm reaches this</b>, which is why it is here. The sharded arm
    ///         never asks for a store-global listing and the database-per-tenant arm has no co-located
    ///         tenants for a file to misattribute.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_store_global_listing_reports_each_stream_once_under_its_own_tenant()
    {
        var streams = await TheExplorer.GetRecentStreamsAsync(100, Token);

        streams.Count.ShouldBe(3);

        streams.Single(x => x.StreamId == _acme.ToString()).TenantId.ShouldBe("acme");
        streams.Single(x => x.StreamId == _globex.ToString()).TenantId.ShouldBe("globex");
        streams.Single(x => x.StreamId == _initech.ToString()).TenantId.ShouldBe("initech");
    }

    /// <summary>
    ///     A database-scoped listing over the shared file sees both its tenants, each under its own
    ///     name.
    /// </summary>
    /// <remarks>
    ///     The positive control for the attribution: a read that answered nothing, or answered one
    ///     tenant only, would satisfy the isolation facts below while being broken.
    /// </remarks>
    [Fact]
    public async Task a_database_scoped_listing_sees_every_co_located_tenant_under_its_own_name()
    {
        var shard = (await TheExplorer.AllDatabases()).Single(x => x.Identifier.Contains('+'));

        var streams = await TheExplorer.GetRecentStreamsAsync(shard, 100, null, Token);

        streams.Select(x => x.StreamId).ShouldContain(_acme.ToString());
        streams.Select(x => x.StreamId).ShouldContain(_globex.ToString());
        streams.Select(x => x.StreamId).ShouldNotContain(_initech.ToString());

        streams.Single(x => x.StreamId == _globex.ToString()).TenantId.ShouldBe("globex");
    }

    /// <summary>
    ///     A tenant-scoped listing picks the file <em>and</em> filters by tenant.
    /// </summary>
    /// <remarks>
    ///     jasperfx#810's gap 2. Marten decides whether to apply the predicate from <em>cardinality</em>,
    ///     on the premise that every stream in a tenant's database is that tenant's — true for
    ///     database-per-tenant, false here. Fisher's <c>ResolveTenantScope</c> decides from
    ///     <em>tenancy style</em> instead, which is the axis that answers the question.
    /// </remarks>
    [Fact]
    public async Task a_tenant_scoped_listing_does_not_leak_a_co_located_tenant()
    {
        var streams = await TheExplorer.GetRecentStreamsAsync(100, "acme", Token);

        streams.Select(x => x.StreamId).ShouldContain(_acme.ToString());
        streams.Select(x => x.StreamId).ShouldNotContain(_globex.ToString());
        streams.Select(x => x.StreamId).ShouldNotContain(_initech.ToString());
    }

    /// <summary>
    ///     Two tenants sharing a file share its connection pool, and a tenant resolves to the very same
    ///     database instance as the one beside it.
    /// </summary>
    /// <remarks>
    ///     Reference identity rather than equality, because that is the property that makes it one pool
    ///     rather than two over the same file — on SQLite a pool is file handles, and fisher#59 measured
    ///     exactly that cost.
    /// </remarks>
    [Fact]
    public void co_located_tenants_resolve_to_one_database_instance()
    {
        var acme = _store.Tenancy.DatabaseFor("acme");
        var globex = _store.Tenancy.DatabaseFor("globex");
        var initech = _store.Tenancy.DatabaseFor("initech");

        acme.ShouldBeSameAs(globex);
        acme.ShouldNotBeSameAs(initech);
    }
}
