using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Events;

/// <summary>
///     fisher#426 — a live-aggregated aggregate keyed on a strong-typed id gets the stream's identity.
/// </summary>
/// <remarks>
///     <para>
///         Live aggregation never consults the aggregate's own id, so an aggregate whose <c>Create</c>
///         does not set it is backfilled afterwards (<c>AggregateIdentity.TrySetIdentity</c>), as Marten
///         and Polecat do. That backfill assigned the stream id only when the member's type could hold it
///         as-is, and a <see cref="Guid" /> is never a <see cref="KilnId" />, so a wrapper was skipped and
///         the aggregate came back with a default id. The fold was right and the identity was not, with
///         nothing to say so.
///     </para>
///     <para>
///         Every path that backfills is covered, because they are separate call sites: live aggregation,
///         <c>FetchForWriting</c> by raw id and by wrapper, <c>FetchManyForWriting</c> and
///         <c>ProjectLatest</c>. The inline snapshot is covered too, because the issue asked whether it
///         was stamped, and it goes through the projection's identity setter rather than the backfill.
/// </para>
/// </remarks>
public class strong_typed_aggregate_identity
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static DocumentStore GuidStore(TemporaryDatabase database, Action<StoreOptions>? configure = null)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            configure?.Invoke(options);
        });

    private static async Task<Guid> StartKilnAsync(DocumentStore store)
    {
        var id = Guid.NewGuid();
        await using var session = store.LightweightSession();
        session.Events.StartStream<Kiln>(id, new KilnFired(900), new KilnFired(1100));
        await session.SaveChangesAsync(Token);
        return id;
    }

    [Fact]
    public async Task live_aggregation_stamps_a_guid_backed_wrapper()
    {
        using var database = TemporaryDatabase.Create("strong-live-id");
        await using var store = GuidStore(database);
        var id = await StartKilnAsync(store);

        await using var query = store.QuerySession();
        var kiln = await query.Events.AggregateStreamAsync<Kiln>(id, token: Token);

        kiln.ShouldNotBeNull();
        kiln.Firings.ShouldBe(2);
        kiln.Id.ShouldBe(new KilnId(id));
    }

    [Fact]
    public async Task fetch_for_writing_stamps_a_wrapper_by_raw_id_and_by_wrapper()
    {
        using var database = TemporaryDatabase.Create("strong-fetch-id");
        await using var store = GuidStore(database);
        var id = await StartKilnAsync(store);

        await using var session = store.LightweightSession();

        (await session.Events.FetchForWriting<Kiln>(id, Token)).Aggregate!.Id.ShouldBe(new KilnId(id));
        (await session.Events.FetchForWriting<Kiln, KilnId>(new KilnId(id), Token)).Aggregate!.Id
            .ShouldBe(new KilnId(id));
    }

    [Fact]
    public async Task fetch_many_for_writing_stamps_each_wrapper()
    {
        using var database = TemporaryDatabase.Create("strong-fetch-many-id");
        await using var store = GuidStore(database);
        var first = await StartKilnAsync(store);
        var second = await StartKilnAsync(store);

        await using var session = store.LightweightSession();
        var streams = await session.Events.FetchManyForWriting<Kiln>([first, second], Token);

        streams.Select(x => x.Aggregate!.Id).ShouldBe([new KilnId(first), new KilnId(second)]);
    }

    [Fact]
    public async Task project_latest_stamps_a_wrapper()
    {
        using var database = TemporaryDatabase.Create("strong-project-latest-id");
        await using var store = GuidStore(database);
        var id = await StartKilnAsync(store);

        await using var session = store.LightweightSession();
        session.Events.Append(id, new KilnFired(1200));

        var projected = await session.Events.ProjectLatest<Kiln>(id, Token);

        projected.ShouldNotBeNull();
        projected.Firings.ShouldBe(3);
        projected.Id.ShouldBe(new KilnId(id));
    }

    [Fact]
    public async Task live_aggregation_stamps_a_string_backed_wrapper()
    {
        using var database = TemporaryDatabase.Create("strong-live-key");
        await using var store = GuidStore(database, options => options.Events.StreamIdentity = StreamIdentity.AsString);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Kettle>("kettle-1", new KilnFired(100));
            await session.SaveChangesAsync(Token);
        }

        await using var query = store.QuerySession();
        var kettle = await query.Events.AggregateStreamAsync<Kettle>("kettle-1", token: Token);

        kettle!.Id.ShouldBe(new KettleKey("kettle-1"));
    }

    /// <remarks>
    ///     The inline snapshot never had the bug — the projection's identity setter wraps through the
    ///     storage — and this pins that, since the issue could not say either way.
    /// </remarks>
    [Fact]
    public async Task an_inline_snapshot_carries_the_wrapper()
    {
        using var database = TemporaryDatabase.Create("strong-snapshot-id");
        await using var store = GuidStore(database,
            options => options.Projections.Snapshot<Kiln, KilnId>(SnapshotLifecycle.Inline));
        var id = await StartKilnAsync(store);

        await using var query = store.QuerySession();
        var kiln = await query.LoadAsync<Kiln, KilnId>(new KilnId(id), Token);

        kiln!.Id.ShouldBe(new KilnId(id));
    }

    /// <remarks>
    ///     A wrapper whose identity is set by <c>Create</c> to the stream id ends up with the same value
    ///     either way. What must not happen is a wrapper around the other identity type being "converted":
    ///     the backfill only ever assigns a wrapper around the stream id's own type, as the
    ///     <c>FetchForWriting&lt;T, TId&gt;</c> refusal does.
    /// </remarks>
    [Fact]
    public void a_wrapper_around_another_type_is_left_alone()
    {
        var kettle = new Kettle();

        Fisher.Storage.AggregateIdentity.TrySetIdentity(kettle, Guid.NewGuid());

        kettle.Id.ShouldBe(default);
    }
}
