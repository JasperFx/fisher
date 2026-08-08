using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Events;

/// <summary>
///     Composite projections — fisher#19.
/// </summary>
/// <remarks>
///     The point of a composite is a later stage seeing what an earlier one wrote, in one rebuild pass
///     rather than two. So the tests that matter are that the stages actually run in order and that a
///     rebuild reproduces the whole thing.
/// </remarks>
public class composite_projections : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("composite");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;

            // A bare IProjection has no mapping of its own, and Fisher only creates tables for types
            // the schema has mapped — so the document a stage stores has to be registered.
            o.Schema.For<Tally>();

            o.Projections.CompositeProjectionFor("catches", composite =>
            {
                composite.Snapshot<Trip>();
                composite.Add(new TripTally(), stageNumber: 2);
            });
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task Append(Guid id, params object[] events)
    {
        await using var session = _store.LightweightSession();
        session.Events.StartStream<Trip>(id, events);
        await session.SaveChangesAsync(Token);
    }

    [Fact]
    public void the_composite_is_registered_as_one_async_projection()
    {
        var shards = _store.Options.Projections.AllShards();

        // One shard for the composite, not one per child — the stages are its business.
        shards.Count(x => x.Name.Name == "catches").ShouldBe(1);
    }

    /// <summary>
    ///     Every stage runs, in one batch, off one shard.
    /// </summary>
    /// <remarks>
    ///     <b>Note what this does not assert.</b> A later stage cannot read an earlier stage's writes
    ///     with <c>LoadAsync</c> — the whole composite commits as one batch, so nothing an earlier
    ///     stage queued is in the database yet. JasperFx's mechanism for sharing across stages is the
    ///     aggregate cache, which aggregation projections participate in and a bare
    ///     <c>IProjection</c> does not. What a composite buys is ordered execution in one batch and
    ///     one rebuild pass, not database-visible cross-stage reads.
    /// </remarks>
    [Fact]
    public async Task every_stage_runs_in_one_batch()
    {
        var trip = Guid.NewGuid();
        await Append(trip, new Landed("Trout"), new Landed("Pike"));

        using var daemon = await _store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();
        await daemon.WaitForNonStaleData(TimeSpan.FromSeconds(20));

        await using var session = _store.LightweightSession();

        (await session.LoadAsync<Trip>(trip, Token))!.Catches.ShouldBe(2);
        (await session.LoadAsync<Tally>(trip, Token))!.Doubled.ShouldBe(4);
    }

    [Fact]
    public async Task a_rebuild_reproduces_every_stage()
    {
        var trip = Guid.NewGuid();
        await Append(trip, new Landed("Trout"), new Landed("Pike"), new Landed("Chub"));

        using var daemon = await _store.BuildProjectionDaemonAsync();
        await daemon.RebuildProjectionAsync("catches", TimeSpan.FromSeconds(30), Token);

        await using var session = _store.LightweightSession();
        (await session.LoadAsync<Trip>(trip, Token))!.Catches.ShouldBe(3);
        (await session.LoadAsync<Tally>(trip, Token))!.Doubled.ShouldBe(6);
    }

    public record Landed(string Species);

    public class Trip
    {
        public Guid Id { get; set; }
        public int Catches { get; set; }

        public void Apply(Landed _) => Catches++;
    }

    public class Tally
    {
        public Guid Id { get; set; }
        public int Doubled { get; set; }
    }

    /// <summary>
    ///     Stage 2, as a bare <c>IProjection</c> — which is what exercises
    ///     <c>CompositeIProjectionSource</c>.
    /// </summary>
    /// <remarks>
    ///     It derives from the events rather than reading stage 1's document, because an earlier
    ///     stage's writes are not in the database until the composite's batch commits.
    /// </remarks>
    public class TripTally : Fisher.Projections.IProjection
    {
        public Task ApplyAsync(IDocumentSession operations, IReadOnlyList<IEvent> events,
            CancellationToken cancellation)
        {
            foreach (var group in events.GroupBy(x => x.StreamId))
            {
                operations.Store(new Tally { Id = group.Key, Doubled = group.Count() * 2 });
            }

            return Task.CompletedTask;
        }
    }
}
