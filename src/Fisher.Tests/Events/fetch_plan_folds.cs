using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Events;

/// <summary>
///     fisher#683 — Marten's #6147/#6148 fetch-plan bugs (fixed in marten#6156) and the #6145 shape, ported as
///     tests so Fisher is shown clear of them rather than read clear.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             marten#6147: reading back with <c>FetchLatest</c> or <c>ProjectLatest</c> after a
///             <c>FetchForWriting</c> and an append folded the pending events onto the instance the write cache
///             held, so the next <c>FetchForWriting</c> applied them again. Fisher's <c>FetchLatest</c> loads the
///             document or rebuilds live, and <c>ProjectLatest</c> folds onto a fresh rebuild, never the cached
///             instance.
///         </item>
///         <item>
///             marten#6148: an Inline aggregate with a strong-typed id read by the stream's Guid. Fisher's
///             <c>FetchLatest</c> loads through <c>LoadAsync&lt;T&gt;(object)</c> (fisher#89), which takes a raw or
///             a wrapped id, and <c>ProjectLatest</c> stores nothing.
///         </item>
///         <item>
///             marten#6145: <c>ProjectLatest</c> stored the folded Async aggregate and the daemon then applied the
///             same events to it again. Fisher's <c>ProjectLatest</c> returns without storing.
///         </item>
///     </list>
///     <c>EventGraph.UseIdentityMapForAggregates</c> is declared and read nowhere, so an identity session is the
///     closest Fisher has to Marten's identity-map variants; if Fisher ever wires the setting up, these are the
///     tests that say whether it brought the #6147 shape with it.
/// </remarks>
public class fetch_plan_folds
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static DocumentStore StoreFor(TemporaryDatabase database, Action<StoreOptions> configure)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            configure(options);
        });

    [Theory]
    [InlineData(ProjectionLifecycle.Async, false)]
    [InlineData(ProjectionLifecycle.Live, false)]
    [InlineData(ProjectionLifecycle.Inline, false)]
    [InlineData(ProjectionLifecycle.Async, true)]
    [InlineData(ProjectionLifecycle.Live, true)]
    [InlineData(ProjectionLifecycle.Inline, true)]
    public async Task reading_back_after_an_append_does_not_double_count_the_next_fetch_for_writing(
        ProjectionLifecycle lifecycle, bool projectLatest)
    {
        using var database = TemporaryDatabase.Create("fetch-plan-6147");
        await using var store = StoreFor(database, options =>
        {
            switch (lifecycle)
            {
                case ProjectionLifecycle.Live:
                    options.Projections.LiveStreamAggregation<FoldCounter, Guid>();
                    break;
                case ProjectionLifecycle.Inline:
                    options.Projections.Snapshot<FoldCounter>(SnapshotLifecycle.Inline);
                    break;
                default:
                    options.Projections.Snapshot<FoldCounter>(SnapshotLifecycle.Async);
                    break;
            }

            options.Events.CacheAggregatesForWriting<FoldCounter>();
        });

        var id = Guid.NewGuid();
        await using (var start = store.LightweightSession())
        {
            start.Events.StartStream<FoldCounter>(id, new FoldBumped());
            await start.SaveChangesAsync(Token);
        }

        // Twice over, so the second round fetches from a cache the first round wrote back
        for (var round = 2; round <= 3; round++)
        {
            await using (var session = store.LightweightSession())
            {
                var stream = await session.Events.FetchForWriting<FoldCounter>(id, Token);
                stream.Aggregate!.Count.ShouldBe(round - 1);
                stream.AppendOne(new FoldBumped());

                if (projectLatest)
                {
                    (await session.Events.ProjectLatest<FoldCounter>(id, Token))!.Count.ShouldBe(round);
                }
                else
                {
                    await session.Events.FetchLatest<FoldCounter>(id, Token);
                }

                await session.SaveChangesAsync(Token);
            }
        }

        await using var later = store.LightweightSession();
        var again = await later.Events.FetchForWriting<FoldCounter>(id, Token);
        again.Aggregate!.Count.ShouldBe(3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task project_latest_on_an_inline_strong_typed_id_aggregate_stores_the_committed_count(
        bool identitySession)
    {
        using var database = TemporaryDatabase.Create("fetch-plan-6148");
        await using var store = StoreFor(database,
            options => options.Projections.Snapshot<TypedFoldCounter>(SnapshotLifecycle.Inline));

        var id = Guid.NewGuid();
        await using (var start = store.LightweightSession())
        {
            start.Events.StartStream<TypedFoldCounter>(id, new FoldTick());
            await start.SaveChangesAsync(Token);
        }

        await using (var session = identitySession ? store.IdentitySession() : store.LightweightSession())
        {
            var stream = await session.Events.FetchForWriting<TypedFoldCounter>(id, Token);
            stream.AppendOne(new FoldTick());

            (await session.Events.ProjectLatest<TypedFoldCounter>(id, Token))!.Count.ShouldBe(2);

            await session.SaveChangesAsync(Token);
        }

        await using var query = store.QuerySession();
        (await query.LoadAsync<TypedFoldCounter, FoldCounterId>(new FoldCounterId(id), Token))!.Count.ShouldBe(2);
        (await query.Events.AggregateStreamAsync<TypedFoldCounter>(id, token: Token))!.Count.ShouldBe(2);
    }

    [Fact]
    public async Task fetch_latest_on_an_inline_strong_typed_id_aggregate()
    {
        using var database = TemporaryDatabase.Create("fetch-plan-6148-latest");
        await using var store = StoreFor(database,
            options => options.Projections.Snapshot<TypedFoldCounter>(SnapshotLifecycle.Inline));

        var id = Guid.NewGuid();
        await using (var start = store.LightweightSession())
        {
            start.Events.StartStream<TypedFoldCounter>(id, new FoldTick());
            await start.SaveChangesAsync(Token);
        }

        await using var session = store.LightweightSession();
        (await session.Events.FetchLatest<TypedFoldCounter>(id, Token))!.Count.ShouldBe(1);
        (await session.Events.FetchLatest<TypedFoldCounter, FoldCounterId>(new FoldCounterId(id), Token))!
            .Count.ShouldBe(1);

        session.Events.Append(id, new FoldTick());
        (await session.Events.ProjectLatest<TypedFoldCounter>(id, Token))!.Count.ShouldBe(2);
    }

    /// <summary>marten#6145: <c>ProjectLatest</c> on an Async aggregate leaves the snapshot to the daemon.</summary>
    [Fact]
    public async Task project_latest_on_an_async_aggregate_leaves_the_snapshot_to_the_daemon()
    {
        using var database = TemporaryDatabase.Create("fetch-plan-6145");
        await using var store = StoreFor(database,
            options => options.Projections.Snapshot<FoldCounter>(SnapshotLifecycle.Async));
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        var id = Guid.NewGuid();
        await using (var start = store.LightweightSession())
        {
            start.Events.StartStream<FoldCounter>(id, new FoldBumped());
            await start.SaveChangesAsync(Token);
        }

        await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

        await using (var session = store.LightweightSession())
        {
            var stream = await session.Events.FetchForWriting<FoldCounter>(id, Token);
            stream.AppendOne(new FoldBumped());

            (await session.Events.ProjectLatest<FoldCounter>(id, Token))!.Count.ShouldBe(2);

            await session.SaveChangesAsync(Token);
        }

        await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);
        await daemon.StopAllAsync();

        await using var query = store.QuerySession();
        (await query.LoadAsync<FoldCounter>(id, Token))!.Count.ShouldBe(2);
        (await query.Events.AggregateStreamAsync<FoldCounter>(id, token: Token))!.Count.ShouldBe(2);
    }
}

public record FoldBumped;

public record FoldTick;

public class FoldCounter
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Apply(FoldBumped _) => Count++;
}

public readonly record struct FoldCounterId(Guid Value);

public class TypedFoldCounter
{
    public FoldCounterId Id { get; set; }
    public int Count { get; set; }

    public void Apply(FoldTick _) => Count++;
}
