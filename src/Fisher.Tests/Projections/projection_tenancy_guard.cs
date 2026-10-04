using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Grouping;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Projections;

/// <summary>
///     A store whose events are conjoined refuses a single-tenant aggregate document at construction
///     (fisher#335), as Marten does (marten#5343).
/// </summary>
/// <remarks>
///     <para>
///         Two tenants may use the same stream id under conjoined events, and a single-tenant document
///         keyed on that id alone holds one row for both — each tenant's projection overwriting the
///         other's, with no error anywhere. <c>aggregate_write_cache</c> had been doing exactly that for
///         a year without noticing, which is the argument for refusing rather than documenting.
///     </para>
/// </remarks>
public class projection_tenancy_guard : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("projection-tenancy-guard");
    private readonly List<DocumentStore> _stores = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }

        _database.Dispose();
    }

    private DocumentStore Build(Action<StoreOptions> configure)
    {
        var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            configure(options);
        });

        _stores.Add(store);
        return store;
    }

    [Theory]
    [InlineData(SnapshotLifecycle.Inline)]
    [InlineData(SnapshotLifecycle.Async)]
    public void conjoined_events_with_a_single_tenant_snapshot_are_refused_by_name(SnapshotLifecycle lifecycle)
    {
        var ex = Should.Throw<InvalidOperationException>(() => Build(options =>
        {
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Projections.Snapshot<Cargo>(lifecycle);
        }));

        ex.Message.ShouldContain("Tenancy storage style mismatch");
        ex.Message.ShouldContain(typeof(Cargo).FullName!);
        ex.Message.ShouldContain("MultiTenanted()");
    }

    /// <remarks>
    ///     The document's tenancy and the projection are configured in either order, which is why the
    ///     guard runs in the store's constructor rather than at registration.
    /// </remarks>
    [Fact]
    public void a_multi_tenanted_snapshot_is_accepted_whichever_order_it_was_configured_in()
    {
        Build(options =>
        {
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Projections.Snapshot<Cargo>(SnapshotLifecycle.Inline);
            options.Schema.For<Cargo>().MultiTenanted();
        });

        Build(options =>
        {
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Schema.For<Cargo>().MultiTenanted();
            options.Projections.Snapshot<Cargo>(SnapshotLifecycle.Inline);
        });
    }

    [Fact]
    public void the_all_documents_policy_satisfies_the_guard()
    {
        Build(options =>
        {
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Policies.AllDocumentsAreMultiTenanted();
            options.Projections.Snapshot<Cargo>(SnapshotLifecycle.Inline);
        });
    }

    /// <summary>
    ///     A vector projection over a single-tenant document is refused too (fisher#391, marten#5420).
    /// </summary>
    /// <remarks>
    ///     It is a bare <see cref="IProjection" /> rather than an aggregation, so the guard skipped it and
    ///     two tenants' <c>MemoWritten("m1", ...)</c> wrote one row that both tenants then read as their
    ///     own. <c>conjoined_tenancy_projections</c> only passed because its store marks every document
    ///     multi-tenanted.
    /// </remarks>
    [Fact]
    public void a_vector_projection_over_a_single_tenant_document_is_refused_by_name()
    {
        var ex = Should.Throw<InvalidOperationException>(() => Build(options =>
        {
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Events.StreamIdentity = StreamIdentity.AsString;
            options.Schema.For<MemoEmbedding>().VectorIndex(x => x.Embedding, dimensions: 3);
            options.Projections.Add(new MemoVectors(new RecordingEmbeddings()), ProjectionLifecycle.Async);
        }));

        ex.Message.ShouldContain("Tenancy storage style mismatch");
        ex.Message.ShouldContain(typeof(MemoEmbedding).FullName!);
    }

    [Fact]
    public void a_vector_projection_over_a_multi_tenanted_document_is_accepted()
    {
        Build(options =>
        {
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Events.StreamIdentity = StreamIdentity.AsString;
            options.Schema.For<MemoEmbedding>().MultiTenanted().VectorIndex(x => x.Embedding, dimensions: 3);
            options.Projections.Add(new MemoVectors(new RecordingEmbeddings()), ProjectionLifecycle.Async);
        });
    }

    [Fact]
    public void a_single_tenant_store_is_untouched()
    {
        Build(options => options.Projections.Snapshot<Cargo>(SnapshotLifecycle.Inline));
    }

    [Fact]
    public void a_multi_stream_projection_respecting_tenants_is_refused()
    {
        var ex = Should.Throw<InvalidOperationException>(() => Build(options =>
        {
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Projections.Add(new ManifestProjection(), ProjectionLifecycle.Inline);
        }));

        ex.Message.ShouldContain(typeof(Manifest).FullName!);
    }

    /// <remarks>Marten's exemption: grouping across tenants says the document is not per tenant.</remarks>
    [Fact]
    public void a_multi_stream_projection_grouping_across_tenants_is_accepted()
    {
        Build(options =>
        {
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Projections.Add(new ManifestProjection { TenancyGrouping = TenancyGrouping.AcrossTenants },
                ProjectionLifecycle.Inline);
        });
    }

    /// <remarks>
    ///     The positive half of the hypothesis fisher#335 opened with: once the document is conjoined,
    ///     the same stream id in two tenants is two rows, and each tenant reads its own.
    /// </remarks>
    [Fact]
    public async Task a_shared_stream_id_in_two_tenants_keeps_two_isolated_snapshots()
    {
        var store = Build(options =>
        {
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Schema.For<Cargo>().MultiTenanted();
            options.Projections.Snapshot<Cargo>(SnapshotLifecycle.Inline);
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var id = Guid.NewGuid();

        foreach (var tenant in new[] { "north", "south" })
        {
            await using var session = store.LightweightSession(tenant);
            session.Events.StartStream<Cargo>(id, new CargoLoaded(tenant));
            await session.SaveChangesAsync(Token);
        }

        foreach (var tenant in new[] { "north", "south" })
        {
            await using var query = store.QuerySession(tenant);
            (await query.LoadAsync<Cargo>(id, Token))!.Contents.ShouldBe(tenant);
        }
    }
}

public record CargoLoaded(string Contents);

public record ManifestEntry(string Ship);

public class Cargo
{
    public Guid Id { get; set; }
    public string Contents { get; set; } = "";

    public static Cargo Create(CargoLoaded loaded) => new() { Contents = loaded.Contents };
}

public class Manifest
{
    public string Id { get; set; } = "";
    public int Entries { get; set; }
}

public partial class ManifestProjection : Fisher.Projections.MultiStreamProjection<Manifest, string>
{
    public ManifestProjection()
    {
        Identity<ManifestEntry>(x => x.Ship);
    }

    public void Apply(ManifestEntry _, Manifest manifest) => manifest.Entries++;
}
