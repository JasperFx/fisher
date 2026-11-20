using JasperFx;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Events;

public record TallyStarted;

public record TallyBumped;

/// <summary>A mutable aggregate: <c>Apply</c> changes the instance it is given.</summary>
public partial class MutableTally
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public static MutableTally Create(TallyStarted _) => new();

    public void Apply(TallyBumped _) => Count++;
}

/// <summary>An immutable aggregate: <c>Apply</c> returns a new record.</summary>
public partial record ImmutableTally
{
    public Guid Id { get; init; }
    public int Count { get; init; }

    public static ImmutableTally Create(TallyStarted _) => new();

    public ImmutableTally Apply(TallyBumped _) => this with { Count = Count + 1 };
}

/// <summary>
///     fisher#645: <c>FetchLatest</c> of an Inline aggregate in the session that just committed it.
/// </summary>
/// <remarks>
///     <para>
///         An Inline <c>FetchLatest</c> is a document load, and on an <c>IdentitySession</c> the load
///         answers from the identity map. The projection used to load the current snapshot through the
///         session's own storage, which put it in the map, and then queue an upsert of the new snapshot
///         that never replaced it. A mutable aggregate hid that, because the projection changed the
///         cached instance itself. An immutable record did not: every later <c>FetchLatest</c> in the
///         session returned the record from before the commit.
///     </para>
///     <para>
///         Every combination is run, because the bug was in one corner of them: each session kind, both
///         aggregate shapes, <c>UseIdentityMapForAggregates</c> on and off (Fisher keeps no aggregate
///         identity map, so the option must make no difference), and the ways a session can reach the
///         stream before it appends.
///     </para>
/// </remarks>
public class fetch_latest_after_an_inline_commit
{
    public enum SessionKind
    {
        Lightweight,
        Identity,
        DirtyTracked
    }

    public enum Reach
    {
        /// <summary>A plain <c>Events.Append</c> with nothing read first.</summary>
        Append,

        /// <summary><c>FetchForWriting</c>, then appending through the stream.</summary>
        FetchForWriting,

        /// <summary><c>FetchLatest</c> before the append, so the session has the old version in hand.</summary>
        FetchLatestFirst,

        /// <summary>A <c>LoadAsync</c> of the snapshot before the append.</summary>
        LoadFirst
    }

    public static TheoryData<SessionKind, bool, bool, Reach> Cases()
    {
        var data = new TheoryData<SessionKind, bool, bool, Reach>();
        foreach (var kind in Enum.GetValues<SessionKind>())
        foreach (var immutable in new[] { false, true })
        foreach (var identityMapForAggregates in new[] { false, true })
        foreach (var reach in Enum.GetValues<Reach>())
        {
            data.Add(kind, immutable, identityMapForAggregates, reach);
        }

        return data;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task fetch_latest_sees_the_commit(SessionKind kind, bool immutable,
        bool identityMapForAggregates, Reach reach)
    {
        if (immutable)
        {
            await runAsync<ImmutableTally>(kind, identityMapForAggregates, reach, x => x.Count);
        }
        else
        {
            await runAsync<MutableTally>(kind, identityMapForAggregates, reach, x => x.Count);
        }
    }

    private static async Task runAsync<T>(SessionKind kind, bool identityMapForAggregates, Reach reach,
        Func<T, int> count) where T : class
    {
        using var database = TemporaryDatabase.Create("fetch-latest-after-commit");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.EventGraph.UseIdentityMapForAggregates = identityMapForAggregates;
            options.Projections.Snapshot<T>(SnapshotLifecycle.Inline);
        });

        // Committed as Started + Bumped: a count of 1.
        var id = Guid.NewGuid();
        await using (var setup = store.LightweightSession())
        {
            setup.Events.StartStream<T>(id, new TallyStarted(), new TallyBumped());
            await setup.SaveChangesAsync(Token);
        }

        await using var session = kind switch
        {
            SessionKind.Identity => store.IdentitySession(),
            SessionKind.DirtyTracked => store.DirtyTrackedSession(),
            _ => store.LightweightSession()
        };

        await appendOneAsync<T>(session, id, reach);
        await session.SaveChangesAsync(Token);

        count((await session.Events.FetchLatest<T>(id, Token))!).ShouldBe(2);
        count((await session.Events.FetchLatest<T>(id, Token))!).ShouldBe(2);
        count((await session.LoadAsync<T>(id, Token))!).ShouldBe(2);

        // And again, in the same session, on top of the first commit.
        await appendOneAsync<T>(session, id, reach);
        await session.SaveChangesAsync(Token);

        count((await session.Events.FetchLatest<T>(id, Token))!).ShouldBe(3);
        count((await session.LoadAsync<T>(id, Token))!).ShouldBe(3);

        // The stored document agrees, read from a session that has seen none of this.
        await using var fresh = store.LightweightSession();
        count((await fresh.Events.FetchLatest<T>(id, Token))!).ShouldBe(3);
    }

    private static async Task appendOneAsync<T>(IDocumentSession session, Guid id, Reach reach) where T : class
    {
        switch (reach)
        {
            case Reach.FetchForWriting:
                var stream = await session.Events.FetchForWriting<T>(id, Token);
                stream.AppendOne(new TallyBumped());
                return;

            case Reach.FetchLatestFirst:
                (await session.Events.FetchLatest<T>(id, Token)).ShouldNotBeNull();
                break;

            case Reach.LoadFirst:
                (await session.LoadAsync<T>(id, Token)).ShouldNotBeNull();
                break;
        }

        session.Events.Append(id, new TallyBumped());
    }
}
