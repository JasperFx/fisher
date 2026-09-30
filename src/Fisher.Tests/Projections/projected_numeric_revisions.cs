using Fisher.Linq;
using Fisher.Tests.Events;
using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Projections;

/// <summary>
///     fisher#369 — a projected <see cref="IRevisioned" /> document's revision is the stream version it
///     was folded to, however the document is read.
/// </summary>
/// <remarks>
///     <para>
///         The projection used to store its snapshot with revision 0, "auto", so the <c>revision</c>
///         column counted writes: one save of two events stored 1. <c>LoadAsync</c> projects that column
///         back onto <c>Version</c> and a LINQ <c>Select</c> reads the body, which the aggregation had
///         stamped with the stream version — so the two reads of one document disagreed, and Fisher
///         disagreed with Marten and Polecat on the loaded value.
///     </para>
///     <para>
///         Every fact compares the two reads against the stream's own version rather than against a
///         literal, because agreement between them is the property a consumer gating a reload on
///         <c>(Id, Version)</c> needs.
///     </para>
/// </remarks>
public class projected_numeric_revisions : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("projected-revisions");
    private DocumentStore _store = null!;
    private IProjectionDaemon? _daemon;

    private CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<RevisionedRoster>(SnapshotLifecycle.Inline);
            options.Projections.Snapshot<AsyncRevisionedRoster>(SnapshotLifecycle.Async);
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

    private async Task<Guid> StartAsync<T>(params object[] events) where T : class
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Events.StartStream<T>(streamId, events);
        await session.SaveChangesAsync(Token);

        return streamId;
    }

    private async Task AppendAsync(Guid streamId, params object[] events)
    {
        await using var session = _store.LightweightSession();
        session.Events.Append(streamId, events);
        await session.SaveChangesAsync(Token);
    }

    private async Task<(int Loaded, int Projected, long Stream)> VersionsAsync<T>(Guid streamId)
        where T : class, IRevisioned
    {
        await using var session = _store.QuerySession();

        var loaded = await session.LoadAsync<T>(streamId, Token);
        var projected = await session.Query<T>()
            .Where(x => x.Version > 0)
            .Select(x => x.Version)
            .ToListAsync(Token);
        var stream = await session.Events.FetchStreamStateAsync(streamId, Token);

        return (loaded.ShouldNotBeNull().Version, projected.ShouldHaveSingleItem(), stream.ShouldNotBeNull().Version);
    }

    [Fact]
    public async Task an_inline_snapshot_loads_the_stream_version_not_a_write_count()
    {
        // The issue's shape exactly: one session, two events, one write.
        var streamId = await StartAsync<RevisionedRoster>(new MemberJoined("Frodo"), new MemberJoined("Sam"));

        var (loaded, projected, stream) = await VersionsAsync<RevisionedRoster>(streamId);

        stream.ShouldBe(2);
        loaded.ShouldBe(2);
        projected.ShouldBe(2);
    }

    [Fact]
    public async Task the_loaded_version_follows_the_stream_across_further_appends()
    {
        var streamId = await StartAsync<RevisionedRoster>(new MemberJoined("Frodo"));
        await AppendAsync(streamId, new MemberJoined("Sam"), new MemberJoined("Merry"));
        await AppendAsync(streamId, new MonsterSlain("Balrog"));

        var (loaded, projected, stream) = await VersionsAsync<RevisionedRoster>(streamId);

        stream.ShouldBe(4);
        loaded.ShouldBe(4);
        projected.ShouldBe(4);
    }

    [Fact]
    public async Task an_async_snapshot_loads_the_stream_version_not_a_write_count()
    {
        var streamId = await StartAsync<AsyncRevisionedRoster>(new MemberJoined("Frodo"), new MemberJoined("Sam"),
            new MonsterSlain("Balrog"));

        _daemon = await _store.BuildProjectionDaemonAsync();
        await _daemon.StartAllAsync();
        await _store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

        var (loaded, projected, stream) = await VersionsAsync<AsyncRevisionedRoster>(streamId);

        stream.ShouldBe(3);
        loaded.ShouldBe(3);
        projected.ShouldBe(3);
    }

    /// <remarks>
    ///     The revision column now holds the stream version, so a repair that stores the rebuilt aggregate
    ///     carries a revision EQUAL to the stored one — which the numeric upsert's strictly-greater guard
    ///     refuses. A rebuild writes what the events say, as the projection does, so it must not be
    ///     guarded against the row it is repairing.
    /// </remarks>
    [Fact]
    public async Task rebuilding_a_single_stream_keeps_the_stream_version()
    {
        var streamId = await StartAsync<RevisionedRoster>(new MemberJoined("Frodo"), new MemberJoined("Sam"));

        await _store.Advanced.RebuildSingleStreamAsync<RevisionedRoster>(streamId, token: Token);

        var (loaded, projected, stream) = await VersionsAsync<RevisionedRoster>(streamId);

        stream.ShouldBe(2);
        loaded.ShouldBe(2);
        projected.ShouldBe(2);
    }
}

public class RevisionedRoster : IRevisioned
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public int Members { get; set; }
    public int MonstersSlain { get; set; }

    public void Apply(MemberJoined joined) => Members++;

    public void Apply(MonsterSlain slain) => MonstersSlain++;
}

public class AsyncRevisionedRoster : IRevisioned
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public int Members { get; set; }
    public int MonstersSlain { get; set; }

    public void Apply(MemberJoined joined) => Members++;

    public void Apply(MonsterSlain slain) => MonstersSlain++;
}
