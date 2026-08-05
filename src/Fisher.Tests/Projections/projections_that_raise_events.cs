using Fisher.Tests.Events;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Projections;

/// <summary>
///     An async projection that appends events of its own — fisher#3.
/// </summary>
/// <remarks>
///     <para>
///         JasperFx's <c>EventSlice.BuildOperations</c> drives three synchronous members on the
///         projection batch. Fisher cannot build an append operation without reading the target
///         stream's current version under the write lock, so all three record the
///         <c>StreamAction</c> and the batch plans it inside its own transaction — the same
///         <c>AppendPlanner</c> a session's <c>SaveChangesAsync</c> uses.
///     </para>
///     <para>
///         What that buys, beyond the events landing at all: the version comes from the authoritative
///         read rather than from the slice's client-side count, the optimistic guard still runs, and
///         the raised events go through the append's trailing sequence read-back, which is the only
///         thing that could ever give a raised event a tag row.
///     </para>
/// </remarks>
public class projections_that_raise_events : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("raised-events");
    private DocumentStore _store = null!;
    private IProjectionDaemon? _daemon;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.AddEventType<QuestAudited>();
            options.Projections.Add(new AuditingProjection(), ProjectionLifecycle.Async);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
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

    private async Task RunDaemonAsync()
    {
        _daemon = await _store.BuildProjectionDaemonAsync();
        await _daemon.StartAllAsync();
        await _store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task a_raised_event_is_appended_to_its_stream()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream<AuditedQuest>(streamId, new QuestStarted("Destroy the ring"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await RunDaemonAsync();

        await using var query = _store.LightweightSession();
        var events = await query.Events.FetchStreamAsync(streamId, token: TestContext.Current.CancellationToken);

        // The original, plus the one the projection raised.
        events.Count.ShouldBe(2);
        events[1].Data.ShouldBeOfType<QuestAudited>().Quest.ShouldBe("Destroy the ring");
    }

    /// <summary>
    ///     The raised event is numbered from the stream's real version, not from the slice's count.
    /// </summary>
    /// <remarks>
    ///     This is what routing through the planner buys over queueing the pre-versioned operations
    ///     JasperFx's slice builds: the version comes from a read under the write lock. Numbering it
    ///     from the slice's own event count would give the same answer only when the projection has
    ///     seen every event on the stream.
    /// </remarks>
    [Fact]
    public async Task a_raised_event_continues_the_streams_version_sequence()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream<AuditedQuest>(streamId,
                new QuestStarted("Destroy the ring"), new MonsterSlain("Balrog"), new MonsterSlain("Shelob"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await RunDaemonAsync();

        await using var query = _store.LightweightSession();
        var events = await query.Events.FetchStreamAsync(streamId, token: TestContext.Current.CancellationToken);

        events.Select(x => x.Version).ShouldBe([1, 2, 3, 4]);
        events[3].Data.ShouldBeOfType<QuestAudited>();

        var state = await query.Events.FetchStreamStateAsync(streamId, TestContext.Current.CancellationToken);
        state.ShouldNotBeNull();
        state.Version.ShouldBe(4);
    }

    /// <summary>
    ///     A raised event gets a sequence like any other, so the daemon sees it too.
    /// </summary>
    /// <remarks>
    ///     It has to: an event written outside the sequence, or below the high-water mark, would be
    ///     invisible to every async projection — the failure mode <c>AUTOINCREMENT</c> on
    ///     <c>fi_events.seq_id</c> exists to prevent.
    /// </remarks>
    [Fact]
    public async Task a_raised_event_takes_the_next_sequence()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream<AuditedQuest>(Guid.NewGuid(), new QuestStarted("Destroy the ring"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var before = await _store.Database.FetchHighestEventSequenceNumber(TestContext.Current.CancellationToken);
        before.ShouldBe(1);

        await RunDaemonAsync();

        (await _store.Database.FetchHighestEventSequenceNumber(TestContext.Current.CancellationToken))
            .ShouldBe(2);
    }
}

public record QuestAudited(string Quest);

public class AuditedQuest
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int MonstersSlain { get; set; }

    public void Apply(QuestStarted started) => Name = started.Name;

    public void Apply(MonsterSlain slain) => MonstersSlain++;
}

/// <summary>
///     Appends one audit event back onto the stream it just projected.
/// </summary>
/// <remarks>
///     Guarded on the snapshot not already carrying the audit, because the raised event lands in
///     <c>fi_events</c> and the shard will read it back on the next pass — an unguarded raise is an
///     append loop.
/// </remarks>
public class AuditingProjection : Fisher.Projections.SingleStreamProjection<AuditedQuest, Guid>
{
    public override ValueTask RaiseSideEffects(IDocumentSession operations, IEventSlice<AuditedQuest> slice)
    {
        if (slice.Events().Any(x => x.Data is QuestAudited))
        {
            return ValueTask.CompletedTask;
        }

        slice.AppendEvent(new QuestAudited(slice.Snapshot?.Name ?? "unknown"));
        return ValueTask.CompletedTask;
    }
}
