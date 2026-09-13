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

/// <summary>
///     fisher#257 — a store where two tenants share a file and nothing in that file can tell them
///     apart is refused, rather than silently storing them as one tenant with two names.
/// </summary>
/// <remarks>
///     <para>
///         <b>The trap is silent in the worst direction</b>, which is why it is a refusal rather than a
///         note: every read returns plausible data, each tenant sees a superset that looks like its own
///         plus some, and the first visible symptom is one tenant's row turning up in another's report.
///         Same shape as fisher#51, and the <c>AddFisherStore&lt;T&gt;</c> precedent one layer down —
///         two stores over one file with the same <c>DatabaseSchemaName</c> are refused for exactly
///         this reason (fisher#46).
///     </para>
///     <para>
///         <b>Two checkpoints, because one cannot be complete.</b> Document mappings are created
///         lazily, so a type nothing registered is invisible at configuration time; the second
///         checkpoint is where such a type's table is provisioned, on both the read and the write path.
///         The check deliberately does <em>not</em> live in <c>DocumentSchema.MappingFor</c>, which is
///         the placement that suggests itself: <c>Schema.For&lt;T&gt;().MultiTenanted()</c> creates the
///         mapping and <em>then</em> sets the flag, so a refusal there would fire before the line that
///         satisfies it could run — the trap fisher#218 moved <c>AssertEveryMappingHasIdentity</c> out
///         of a constructor for.
///     </para>
/// </remarks>
public class shared_file_refusal : IDisposable
{
    private readonly TemporaryDatabase _shared = TemporaryDatabase.Create("refusal-shared");
    private readonly TemporaryDatabase _other = TemporaryDatabase.Create("refusal-other");

    public void Dispose()
    {
        _shared.Dispose();
        _other.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    ///     Two tenants on one file with a non-conjoined event store is refused, naming both tenants and
    ///     the file.
    /// </summary>
    /// <remarks>
    ///     The message is asserted rather than merely the throw, because the configuration that
    ///     produces this is usually a loop over a tenant list rather than two visible lines — a reader
    ///     needs to be told <em>which</em> tenants collided and in which file.
    /// </remarks>
    [Fact]
    public void sharing_a_file_without_conjoined_events_is_refused()
    {
        var message = Should.Throw<InvalidOperationException>(() => DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.MultiTenantedDatabases(d => d
                .AddTenant("acme", _shared.ConnectionString)
                .AddTenant("globex", _shared.ConnectionString));
        })).Message;

        message.ShouldContain("'acme'");
        message.ShouldContain("'globex'");
        message.ShouldContain("TenancyStyle.Conjoined");
    }

    /// <summary>
    ///     ⚠️ Conjoined event tenancy is required even for a store that never appends an event.
    /// </summary>
    /// <remarks>
    ///     <b>A decision, not an oversight, and this test is what says so.</b> The conditional rule —
    ///     require it only when the store uses events — cannot be decided honestly: the event tables
    ///     are created by every migration whether or not anything writes to them, and an append does
    ///     not need its event type registered, so "does this store use events" has no reliable answer
    ///     at configuration time. A rule that guessed would refuse some safe stores and admit some
    ///     unsafe ones. The unconditional rule costs a documents-only sharded store one line and an
    ///     unused column.
    /// </remarks>
    [Fact]
    public void a_documents_only_store_still_has_to_say_conjoined()
    {
        Should.Throw<InvalidOperationException>(() => DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<ShardedNote>().MultiTenanted();
            options.MultiTenantedDatabases(d => d
                .AddTenant("acme", _shared.ConnectionString)
                .AddTenant("globex", _shared.ConnectionString));
        })).Message.ShouldContain("TenancyStyle.Conjoined");
    }

    /// <summary>
    ///     A document type registered at configuration time and not multi-tenanted is refused there,
    ///     naming the type.
    /// </summary>
    [Fact]
    public void a_registered_single_tenant_document_type_is_refused_at_configuration_time()
    {
        var message = Should.Throw<InvalidOperationException>(() => DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Schema.For<ShardedNote>();
            options.MultiTenantedDatabases(d => d
                .AddTenant("acme", _shared.ConnectionString)
                .AddTenant("globex", _shared.ConnectionString));
        })).Message;

        message.ShouldContain(nameof(ShardedNote));
        message.ShouldContain("MultiTenanted");
    }

    /// <summary>
    ///     The legitimate shape builds, which is the control every refusal above rests on.
    /// </summary>
    [Fact]
    public async Task the_safe_shape_is_accepted()
    {
        await using var store = DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Schema.For<ShardedNote>().MultiTenanted();
            options.MultiTenantedDatabases(d => d
                .AddTenant("acme", _shared.ConnectionString)
                .AddTenant("globex", _shared.ConnectionString)
                .AddTenant("initech", _other.ConnectionString));
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = store.LightweightSession("acme");
        session.Store(new ShardedNote { Id = Guid.NewGuid(), Text = "fine" });
        await session.SaveChangesAsync(Token);
    }

    /// <summary>
    ///     <b>The second checkpoint.</b> A document type nothing registered is refused when its table is
    ///     provisioned, which is the first moment it exists at all.
    /// </summary>
    /// <remarks>
    ///     The store builds cleanly here — the configuration-time sweep has no mapping to look at — so
    ///     this is the half that makes the guard complete rather than merely early. Asserted on both
    ///     the write and the read path, because fisher#74 established they are two entry points and
    ///     the one nobody exercises is the one that would be forgotten.
    /// </remarks>
    [Fact]
    public async Task a_lazily_mapped_single_tenant_document_type_is_refused_on_first_write()
    {
        await using var store = BuildWithNoDocumentTypesRegistered();

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = store.LightweightSession("acme");
        session.Store(new ShardedNote { Id = Guid.NewGuid(), Text = "late" });

        (await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync(Token)))
            .Message.ShouldContain(nameof(ShardedNote));
    }

    /// <inheritdoc cref="a_lazily_mapped_single_tenant_document_type_is_refused_on_first_write" />
    [Fact]
    public async Task a_lazily_mapped_single_tenant_document_type_is_refused_on_first_read()
    {
        await using var store = BuildWithNoDocumentTypesRegistered();

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = store.QuerySession("acme");

        (await Should.ThrowAsync<InvalidOperationException>(
                () => session.LoadAsync<ShardedNote>(Guid.NewGuid(), Token)))
            .Message.ShouldContain(nameof(ShardedNote));
    }

    /// <summary>
    ///     Nothing is refused where nothing is shared.
    /// </summary>
    /// <remarks>
    ///     <b>The fact that stops this guard becoming a regression for ordinary database-per-tenant.</b>
    ///     A tenant with a file to itself needs neither conjoined events nor a multi-tenanted document
    ///     type — the file boundary is the isolation — and a guard that fired here would refuse the
    ///     configuration fisher#47 exists for.
    /// </remarks>
    [Fact]
    public async Task a_file_per_tenant_is_untouched()
    {
        await using var store = DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<ShardedNote>();
            options.MultiTenantedDatabases(d => d
                .AddTenant("acme", _shared.ConnectionString)
                .AddTenant("globex", _other.ConnectionString));
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = store.LightweightSession("acme");
        session.Store(new ShardedNote { Id = Guid.NewGuid(), Text = "mine alone" });
        await session.SaveChangesAsync(Token);
    }

    /// <summary>
    ///     A single-database store has no shared file and is untouched, whatever it stores.
    /// </summary>
    [Fact]
    public async Task a_single_database_store_is_untouched()
    {
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _shared.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<ShardedNote>();
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = store.LightweightSession();
        session.Store(new ShardedNote { Id = Guid.NewGuid(), Text = "one file" });
        await session.SaveChangesAsync(Token);
    }

    private DocumentStore BuildWithNoDocumentTypesRegistered()
        => DocumentStore.For(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.MultiTenantedDatabases(d => d
                .AddTenant("acme", _shared.ConnectionString)
                .AddTenant("globex", _shared.ConnectionString));
        });
}

public class ShardedNote
{
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;
}
