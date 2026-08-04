using JasperFx;
using JasperFx.Events;

namespace Fisher.Tests.Events;

/// <summary>
///     The SQLite-specific corners of <see cref="IEventStore" />'s explorer surface that the shared
///     compliance suite cannot reach.
/// </summary>
/// <remarks>
///     <c>EventStoreExplorerCompliance</c> covers the behaviour every Critter Stack store owes. What it
///     does not cover is the two places Fisher's storage decisions could break it silently: Guid ids
///     stored as case-sensitive TEXT, and timestamps stored as sortable ISO-8601 TEXT. Both would pass
///     the shared suite while being wrong for a caller that did not happen to hand in a
///     lowercase-canonical id.
/// </remarks>
public class event_store_explorer : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("explorer");
    private DocumentStore _store = null!;
    private readonly Guid _streamId = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using var session = _store.LightweightSession();
        session.Events.StartStream<Quest>(_streamId,
            new QuestStarted("Find the ring"),
            new MemberJoined("Frodo"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private IEventStore TheExplorer => _store;

    /// <summary>
    ///     The trap this exists for: <c>fi_streams.id</c> holds the lowercase canonical Guid, SQLite's
    ///     default collation is case-sensitive, and <see cref="Guid.ToString" /> on the caller's side can
    ///     just as easily have produced uppercase. Without the normalising parse in
    ///     <c>GetStreamMetadataAsync</c> this returns null for a stream that plainly exists.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task stream_metadata_is_found_regardless_of_guid_casing(bool uppercase)
    {
        var id = uppercase
            ? _streamId.ToString().ToUpperInvariant()
            : _streamId.ToString().ToLowerInvariant();

        var metadata = await TheExplorer.GetStreamMetadataAsync(id, TestContext.Current.CancellationToken);

        metadata.ShouldNotBeNull();
        metadata.Version.ShouldBe(2);
        metadata.IsArchived.ShouldBeFalse();
    }

    /// <summary>
    ///     Archived streams are still streams — the explorer is a diagnostic view, and a stream
    ///     disappearing from it after an archive would be the opposite of useful.
    /// </summary>
    [Fact]
    public async Task stream_metadata_reports_an_archived_stream_as_archived()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Events.ArchiveStream(_streamId);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var metadata = await TheExplorer.GetStreamMetadataAsync(
            _streamId.ToString(), TestContext.Current.CancellationToken);

        metadata.ShouldNotBeNull();
        metadata.IsArchived.ShouldBeTrue();
    }

    /// <summary>
    ///     Ordering is a string sort over ISO-8601 TEXT. It is correct only while
    ///     <c>SqliteTimestamp.Format</c> stays fixed-width, UTC and millisecond-precision — a format
    ///     change that dropped the sub-second component would leave same-second streams in insertion
    ///     order and this is what would catch it.
    /// </summary>
    [Fact]
    public async Task recent_streams_orders_by_timestamp_descending()
    {
        var later = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);

            var id = Guid.NewGuid();
            await using var session = _store.LightweightSession();
            session.Events.StartStream<Quest>(id, new QuestStarted($"Quest {i}"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
            later.Add(id);
        }

        var streams = await TheExplorer.GetRecentStreamsAsync(10, TestContext.Current.CancellationToken);

        streams.Count.ShouldBe(4);
        streams[0].StreamId.ShouldBe(later[^1].ToString());
        streams.Select(x => x.LastUpdatedAt)
            .ShouldBe(streams.Select(x => x.LastUpdatedAt).OrderByDescending(x => x));
    }

    [Fact]
    public async Task recent_streams_honours_the_count()
    {
        var streams = await TheExplorer.GetRecentStreamsAsync(1, TestContext.Current.CancellationToken);
        streams.Count.ShouldBe(1);
    }

    /// <summary>
    ///     A non-positive count short-circuits without touching the database rather than emitting
    ///     <c>limit 0</c> or, worse, <c>limit -1</c> — which SQLite reads as "no limit".
    /// </summary>
    [Fact]
    public async Task asking_for_no_streams_returns_none()
    {
        var streams = await TheExplorer.GetRecentStreamsAsync(0, TestContext.Current.CancellationToken);
        streams.ShouldBeEmpty();
    }

    [Fact]
    public async Task usage_reports_the_event_types_the_store_knows()
    {
        var usage = await TheExplorer.TryCreateUsage(TestContext.Current.CancellationToken);

        usage.ShouldNotBeNull();
        var names = usage.Events.Select(x => x.EventTypeName).ToList();
        names.ShouldContain(_store.Options.EventGraph.EventMappingFor(typeof(QuestStarted)).EventTypeName);
        names.ShouldContain(_store.Options.EventGraph.EventMappingFor(typeof(MemberJoined)).EventTypeName);
    }
}
