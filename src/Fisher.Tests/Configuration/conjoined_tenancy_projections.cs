using Fisher.Tests.Projections;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Configuration;

/// <summary>
///     Projections under conjoined tenancy, with one identity shared by two tenants (fisher#335).
/// </summary>
/// <remarks>
///     <para>
///         The shared stream-id snapshot is fisher#343's guard and its own test; these are the projection
///         shapes the issue found with no tenancy test beside it — a multi-stream projection, a rebuild,
///         and a <c>VectorProjection</c>, each checked in both directions over one shared identity.
///     </para>
///     <para>
///         The rebuild matters separately from the first run because it reconstructs every row from the
///         events, so the tenant each row lands under comes from the event rather than from a session
///         that already knew it — the async half of the shared snapshot fact, from the other end.
///     </para>
/// </remarks>
public class conjoined_tenancy_projections : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("conjoined-projections");
    private readonly RecordingEmbeddings _embeddings = new();
    private DocumentStore _store = null!;
    private IProjectionDaemon? _daemon;

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
            options.Events.StreamIdentity = StreamIdentity.AsString;
            options.Policies.AllDocumentsAreMultiTenanted();

            options.Schema.For<MemoEmbedding>().VectorIndex(x => x.Embedding, dimensions: 3);
            options.Projections.Add(new FleetTallyProjection(), ProjectionLifecycle.Inline);
            options.Projections.Snapshot<Voyage>(SnapshotLifecycle.Async);
            options.Projections.Add(new MemoVectors(_embeddings), ProjectionLifecycle.Async);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_daemon is not null)
        {
            await _daemon.StopAllAsync();
            _daemon.Dispose();
        }

        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task AppendAsync(string tenant, string streamKey, params object[] events)
    {
        await using var session = _store.LightweightSession(tenant);
        session.Events.Append(streamKey, events);
        await session.SaveChangesAsync(Token);
    }

    private async Task CatchUpAsync()
    {
        if (_daemon is null)
        {
            _daemon = await _store.BuildProjectionDaemonAsync();
            await _daemon.StartAllAsync();
        }

        await _store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));
    }

    private async Task<T?> LoadAsync<T>(string tenant, string id) where T : notnull
    {
        await using var query = _store.QuerySession(tenant);
        return await query.LoadAsync<T>(id, Token);
    }

    /// <remarks>
    ///     Both tenants' events group to the same identity. <c>RespectTenant</c> — the default — means
    ///     two documents, one per tenant, each counting only its own events.
    /// </remarks>
    [Fact]
    public async Task a_multi_stream_projection_keeps_one_document_per_tenant_for_a_shared_identity()
    {
        await AppendAsync(North, "ship-1", new Sailed("pacific"), new Sailed("pacific"));
        await AppendAsync(South, "ship-2", new Sailed("pacific"));

        (await LoadAsync<FleetTally>(North, "pacific"))!.Voyages.ShouldBe(2);
        (await LoadAsync<FleetTally>(South, "pacific"))!.Voyages.ShouldBe(1);
    }

    [Fact]
    public async Task a_rebuild_puts_each_tenants_row_back_under_that_tenant()
    {
        await AppendAsync(North, "voyage-1", new Sailed("north sea"));
        await AppendAsync(South, "voyage-1", new Sailed("south sea"), new Sailed("south sea"));

        await CatchUpAsync();
        await _daemon!.RebuildProjectionAsync<Voyage>(Token);
        await _store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));

        var north = await LoadAsync<Voyage>(North, "voyage-1");
        north!.Legs.ShouldBe(1);
        north.Sea.ShouldBe("north sea");

        var south = await LoadAsync<Voyage>(South, "voyage-1");
        south!.Legs.ShouldBe(2);
        south.Sea.ShouldBe("south sea");
    }

    /// <remarks>
    ///     Async only (fisher#287), so the marten#5439 inline shape cannot occur — but nothing asserted
    ///     that the daemon writes each tenant's embedding under that tenant. The memo id is the document
    ///     id and both tenants use it.
    /// </remarks>
    [Fact]
    public async Task a_vector_projection_writes_each_tenants_embedding_under_that_tenant()
    {
        await AppendAsync(North, "memo-stream-n", new MemoWritten("m1", "north memo"));
        await AppendAsync(South, "memo-stream-s", new MemoWritten("m1", "south memo"));

        await CatchUpAsync();

        (await LoadAsync<MemoEmbedding>(North, "m1"))!.Content.ShouldBe("north memo");
        (await LoadAsync<MemoEmbedding>(South, "m1"))!.Content.ShouldBe("south memo");
    }
}

public record Sailed(string Sea);

public class FleetTally
{
    public string Id { get; set; } = "";
    public int Voyages { get; set; }
}

public partial class FleetTallyProjection : Fisher.Projections.MultiStreamProjection<FleetTally, string>
{
    public FleetTallyProjection()
    {
        Identity<Sailed>(x => x.Sea);
    }

    public void Apply(Sailed _, FleetTally tally) => tally.Voyages++;
}

public class Voyage
{
    public string Id { get; set; } = "";
    public string Sea { get; set; } = "";
    public int Legs { get; set; }

    public void Apply(Sailed sailed)
    {
        Sea = sailed.Sea;
        Legs++;
    }
}
