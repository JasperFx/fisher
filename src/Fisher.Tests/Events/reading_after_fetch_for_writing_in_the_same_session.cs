using JasperFx;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Events;

/// <summary>
///     The parity check for marten#6104 (Marten's fix is marten PR #6105): reading an aggregate in the session that
///     fetched it for writing applied the session's appended events twice on Marten, for Live and Async with
///     <c>UseIdentityMapForAggregates</c> on.
/// </summary>
/// <remarks>
///     <para>
///         Marten's Live and Async <c>FetchForReading</c> took a shortcut when <c>FetchForWriting</c> had left the
///         stream in its aggregate identity map: it applied the stream's pending events to the fetched aggregate,
///         and <c>ProjectLatest</c> then applied them again. Fisher has no such shortcut and no aggregate identity
///         map. <c>FetchLatest</c> of a Live or Async aggregate folds what is committed, an Inline one loads the
///         document, and <c>ProjectLatest</c> folds the pending events once onto a fresh fold of what is committed.
///     </para>
///     <para>
///         These are Marten's seven scenarios, run on Fisher for every lifecycle, both aggregate shapes, both
///         settings of <c>UseIdentityMapForAggregates</c> and both a lightweight and an identity session, so the
///         answer is pinned rather than read from source. A <c>FetchLatest</c> before the commit leaves out the
///         pending events, which is what Marten's fix makes Marten answer too.
///     </para>
/// </remarks>
public class reading_after_fetch_for_writing_in_the_same_session
{
    public static TheoryData<ProjectionLifecycle, bool, bool, bool> Cases()
    {
        var data = new TheoryData<ProjectionLifecycle, bool, bool, bool>();
        foreach (var lifecycle in new[] { ProjectionLifecycle.Live, ProjectionLifecycle.Inline, ProjectionLifecycle.Async })
        foreach (var identityMapForAggregates in new[] { false, true })
        foreach (var identitySession in new[] { false, true })
        foreach (var immutable in new[] { false, true })
        {
            data.Add(lifecycle, identityMapForAggregates, identitySession, immutable);
        }

        return data;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private delegate Task Scenario<T>(DocumentStore store, Guid streamId, IDocumentSession session,
        Func<T, int> count) where T : class;

    private static async Task runAsync(ProjectionLifecycle lifecycle, bool identityMapForAggregates,
        bool identitySession, bool immutable, Scenario<MutableTally> mutable, Scenario<ImmutableTally> immutableScenario)
    {
        if (immutable)
        {
            await runAsync(lifecycle, identityMapForAggregates, identitySession, immutableScenario, x => x.Count);
        }
        else
        {
            await runAsync(lifecycle, identityMapForAggregates, identitySession, mutable, x => x.Count);
        }
    }

    private static async Task runAsync<T>(ProjectionLifecycle lifecycle, bool identityMapForAggregates,
        bool identitySession, Scenario<T> scenario, Func<T, int> count) where T : class
    {
        using var database = TemporaryDatabase.Create("reading-after-fetch-for-writing");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.EventGraph.UseIdentityMapForAggregates = identityMapForAggregates;

            switch (lifecycle)
            {
                case ProjectionLifecycle.Live:
                    options.Projections.LiveStreamAggregation<T, Guid>();
                    break;
                case ProjectionLifecycle.Inline:
                    options.Projections.Snapshot<T>(SnapshotLifecycle.Inline);
                    break;
                case ProjectionLifecycle.Async:
                    options.Projections.Snapshot<T>(SnapshotLifecycle.Async);
                    break;
            }
        });

        // Committed as Started + Bumped: a count of 1.
        var streamId = Guid.NewGuid();
        await using (var setup = store.LightweightSession())
        {
            setup.Events.StartStream<T>(streamId, new TallyStarted(), new TallyBumped());
            await setup.SaveChangesAsync(Token);
        }

        await using var session = identitySession ? store.IdentitySession() : store.LightweightSession();
        await scenario(store, streamId, session, count);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public Task project_latest_applies_the_appended_event_once(ProjectionLifecycle lifecycle,
        bool identityMapForAggregates, bool identitySession, bool immutable)
        => runAsync(lifecycle, identityMapForAggregates, identitySession, immutable,
            projectLatestOnce<MutableTally>, projectLatestOnce<ImmutableTally>);

    private static async Task projectLatestOnce<T>(DocumentStore store, Guid streamId, IDocumentSession session,
        Func<T, int> count) where T : class
    {
        var stream = await session.Events.FetchForWriting<T>(streamId, Token);
        stream.AppendOne(new TallyBumped());

        count((await session.Events.ProjectLatest<T>(streamId, Token))!).ShouldBe(2);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public Task project_latest_answers_the_same_when_called_twice(ProjectionLifecycle lifecycle,
        bool identityMapForAggregates, bool identitySession, bool immutable)
        => runAsync(lifecycle, identityMapForAggregates, identitySession, immutable,
            projectLatestTwice<MutableTally>, projectLatestTwice<ImmutableTally>);

    private static async Task projectLatestTwice<T>(DocumentStore store, Guid streamId, IDocumentSession session,
        Func<T, int> count) where T : class
    {
        var stream = await session.Events.FetchForWriting<T>(streamId, Token);
        stream.AppendOne(new TallyBumped());

        count((await session.Events.ProjectLatest<T>(streamId, Token))!).ShouldBe(2);
        count((await session.Events.ProjectLatest<T>(streamId, Token))!).ShouldBe(2);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public Task project_latest_after_fetch_latest_applies_the_appended_event_once(ProjectionLifecycle lifecycle,
        bool identityMapForAggregates, bool identitySession, bool immutable)
        => runAsync(lifecycle, identityMapForAggregates, identitySession, immutable,
            projectLatestAfterFetchLatest<MutableTally>, projectLatestAfterFetchLatest<ImmutableTally>);

    private static async Task projectLatestAfterFetchLatest<T>(DocumentStore store, Guid streamId,
        IDocumentSession session, Func<T, int> count) where T : class
    {
        var stream = await session.Events.FetchForWriting<T>(streamId, Token);
        stream.AppendOne(new TallyBumped());

        // Before the commit, FetchLatest leaves the pending event out
        count((await session.Events.FetchLatest<T>(streamId, Token))!).ShouldBe(1);
        count((await session.Events.ProjectLatest<T>(streamId, Token))!).ShouldBe(2);
        count((await session.Events.FetchLatest<T>(streamId, Token))!).ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public Task committing_after_project_latest_leaves_the_aggregate_counted_once(ProjectionLifecycle lifecycle,
        bool identityMapForAggregates, bool identitySession, bool immutable)
        => runAsync(lifecycle, identityMapForAggregates, identitySession, immutable,
            commitAfterProjectLatest<MutableTally>, commitAfterProjectLatest<ImmutableTally>);

    private static async Task commitAfterProjectLatest<T>(DocumentStore store, Guid streamId,
        IDocumentSession session, Func<T, int> count) where T : class
    {
        var stream = await session.Events.FetchForWriting<T>(streamId, Token);
        stream.AppendOne(new TallyBumped());

        await session.Events.ProjectLatest<T>(streamId, Token);
        await session.SaveChangesAsync(Token);

        count((await session.Events.FetchLatest<T>(streamId, Token))!).ShouldBe(2);

        await using var query = store.LightweightSession();
        count((await query.Events.FetchLatest<T>(streamId, Token))!).ShouldBe(2);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public Task fetch_latest_answers_the_same_when_called_twice_after_the_commit(ProjectionLifecycle lifecycle,
        bool identityMapForAggregates, bool identitySession, bool immutable)
        => runAsync(lifecycle, identityMapForAggregates, identitySession, immutable,
            fetchLatestTwiceAfterCommit<MutableTally>, fetchLatestTwiceAfterCommit<ImmutableTally>);

    private static async Task fetchLatestTwiceAfterCommit<T>(DocumentStore store, Guid streamId,
        IDocumentSession session, Func<T, int> count) where T : class
    {
        var stream = await session.Events.FetchForWriting<T>(streamId, Token);
        stream.AppendOne(new TallyBumped());
        await session.SaveChangesAsync(Token);

        count((await session.Events.FetchLatest<T>(streamId, Token))!).ShouldBe(2);
        count((await session.Events.FetchLatest<T>(streamId, Token))!).ShouldBe(2);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public Task fetch_latest_answers_the_same_twice_when_an_event_replaces_the_aggregate(
        ProjectionLifecycle lifecycle, bool identityMapForAggregates, bool identitySession, bool immutable)
        => runAsync(lifecycle, identityMapForAggregates, identitySession, immutable,
            replacedAggregate<MutableTally>, replacedAggregate<ImmutableTally>);

    private static async Task replacedAggregate<T>(DocumentStore store, Guid streamId, IDocumentSession session,
        Func<T, int> count) where T : class
    {
        var stream = await session.Events.FetchForWriting<T>(streamId, Token);
        stream.AppendMany(new TallyBumped(), new TallyReplaced());
        await session.SaveChangesAsync(Token);

        count((await session.Events.FetchLatest<T>(streamId, Token))!).ShouldBe(2);
        count((await session.Events.FetchLatest<T>(streamId, Token))!).ShouldBe(2);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public Task project_latest_applies_an_event_appended_after_the_commit(ProjectionLifecycle lifecycle,
        bool identityMapForAggregates, bool identitySession, bool immutable)
        => runAsync(lifecycle, identityMapForAggregates, identitySession, immutable,
            appendAfterCommit<MutableTally>, appendAfterCommit<ImmutableTally>);

    private static async Task appendAfterCommit<T>(DocumentStore store, Guid streamId, IDocumentSession session,
        Func<T, int> count) where T : class
    {
        var stream = await session.Events.FetchForWriting<T>(streamId, Token);
        stream.AppendOne(new TallyBumped());
        await session.SaveChangesAsync(Token);

        session.Events.Append(streamId, new TallyBumped());

        count((await session.Events.ProjectLatest<T>(streamId, Token))!).ShouldBe(3);
    }
}
