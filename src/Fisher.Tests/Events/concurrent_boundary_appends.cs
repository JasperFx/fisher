using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Tags;

namespace Fisher.Tests.Events;

public record EnrolleeId(Guid Value);

public record ProgramId(Guid Value);

public record Enrolled(string Name);

public record ProgressRecorded(string Milestone);

public class ProgramEnrollment
{
    // Fisher resolves an aggregate's identity from a Guid `Id` (or an [Identity] member) even when the
    // aggregate is only ever reached through a tag boundary — there is no boundary-aggregate marker here
    // the way Polecat has one. The value is unused by these tests, which only read Enrollee.
    public Guid Id { get; set; }

    public string Enrollee { get; set; } = "";

    public List<string> Milestones { get; set; } = [];

    public void Apply(Enrolled e) => Enrollee = e.Name;

    public void Apply(ProgressRecorded e) => Milestones.Add(e.Milestone);
}

/// <summary>
///     Concurrent appends guarded by the same DCB tag boundary must serialize: when several writers race
///     the same boundary, exactly one may commit. Ported from the coverage that caught this in the other
///     two stores — marten#5300 (staggered racers) and marten#4591 (barrier-synced racers), which found
///     polecat#515 as well.
/// </summary>
/// <remarks>
///     <para>
///         Fisher is expected to pass this from the outset, and the point of the fixture is to keep it
///         that way. Marten and Polecat both checked their boundaries with a non-locking predicate read,
///         which at READ COMMITTED lets every concurrent saver run the check before any of them commits —
///         so all of them win. Fisher re-runs the tag query inside <c>BEGIN IMMEDIATE</c>, and SQLite
///         admits one writer per file, so the check-then-act is genuinely serial.
///     </para>
///     <para>
///         That argument rests on two properties that a later change could quietly remove: the check runs
///         inside the write transaction, and <c>seq_id</c> is globally monotonic. Move the check outside
///         the lock, or shard the sequence, and this fixture is what notices. See
///         <c>FisherSession.AssertBoundariesAreStillConsistentAsync</c>.
///     </para>
///     <para>
///         Racers each carry their OWN EnrolleeId as well as the shared ProgramId, so the boundary routes
///         them to distinct streams and the (stream, version) constraint cannot serialize them. The DCB
///         boundary has to be what catches the race.
///     </para>
/// </remarks>
public class concurrent_boundary_appends : IAsyncLifetime
{
    private const int Racers = 16;
    private const int Rounds = 50;

    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("dcb-concurrency");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.RegisterTagType<EnrolleeId>("enrollee").ForAggregate<ProgramEnrollment>();
            options.Events.RegisterTagType<ProgramId>("program").ForAggregate<ProgramEnrollment>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task staggered_racers_on_one_boundary_serialize_to_one_winner()
    {
        long worst = 0;

        for (var round = 0; round < Rounds; round++)
        {
            var programId = new ProgramId(Guid.NewGuid());
            await Task.WhenAll(Enumerable.Range(0, Racers).Select(_ => TryEnrollAsync(programId)));

            worst = Math.Max(worst, await EnrollmentCountAsync(programId));
        }

        worst.ShouldBe(1);
    }

    /// <summary>
    ///     Same race, but the boundary already has an unrelated event under it, so the racers append at an
    ///     established position rather than creating the boundary from nothing. A check that only guarded
    ///     the first-ever append would pass the test above and fail here.
    /// </summary>
    [Fact]
    public async Task staggered_racers_at_an_established_boundary_serialize_to_one_winner()
    {
        long worst = 0;

        for (var round = 0; round < Rounds; round++)
        {
            var programId = new ProgramId(Guid.NewGuid());
            await SeedUnrelatedActivityAsync(programId);
            await Task.WhenAll(Enumerable.Range(0, Racers).Select(_ => TryEnrollAsync(programId)));

            worst = Math.Max(worst, await EnrollmentCountAsync(programId));
        }

        worst.ShouldBe(1);
    }

    /// <summary>
    ///     Every racer completes its fetch, then all of them are released into SaveChangesAsync at once —
    ///     a different interleaving from the staggered tests, and the one that caught marten#4591.
    /// </summary>
    [Fact]
    public async Task barrier_synced_racers_on_one_boundary_serialize_to_one_winner()
    {
        var programId = new ProgramId(Guid.NewGuid());
        var query = new EventTagQuery().Or<ProgramId>(programId);

        var fetched = new TaskCompletionSource[Racers];
        for (var i = 0; i < Racers; i++) fetched[i] = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var racers = Enumerable.Range(0, Racers).Select(i => Task.Run(async () =>
        {
            await using var session = _store.LightweightSession();
            var boundary = await session.Events.FetchForWritingByTags<ProgramEnrollment>(query);

            fetched[i].SetResult();
            await release.Task;

            var enrolled = session.Events.BuildEvent(new Enrolled($"Student-{i}"));
            enrolled.WithTag(new EnrolleeId(Guid.NewGuid()), programId);
            boundary.AppendOne(enrolled);

            try
            {
                await session.SaveChangesAsync();
                return true;
            }
            catch (DcbConcurrencyException)
            {
                return false;
            }
        })).ToArray();

        await Task.WhenAll(fetched.Select(x => x.Task));
        release.SetResult();

        var results = await Task.WhenAll(racers);

        results.Count(x => x).ShouldBe(1);

        // And the losers lose the documented way. Every racer here appended, so none of them can have
        // returned false for any reason other than a DcbConcurrencyException — worth pinning, because
        // SQLite serializes writers and the plausible alternative failure is a busy/locked error leaking
        // out of the resilience pipeline, which is not the contract callers write retry loops against.
        results.Count(x => !x).ShouldBe(Racers - 1);
    }

    // Invariant: at most one enrollment per program. A racer whose fetch already shows one backs off, so
    // every commit past the first is one the boundary check should have refused.
    //
    // Only DcbConcurrencyException is caught, deliberately. If contention on SQLite's single writer
    // surfaces as something else — a busy/locked error escaping the resilience pipeline, say — that is
    // worth failing over rather than swallowing: the contract callers write their retry loops against is
    // the exception type.
    private async Task TryEnrollAsync(ProgramId programId)
    {
        await using var session = _store.LightweightSession();
        var boundary = await session.Events.FetchForWritingByTags<ProgramEnrollment>(
            new EventTagQuery().Or<ProgramId>(programId));

        if (boundary.Aggregate is { Enrollee.Length: > 0 })
        {
            return;
        }

        var enrolled = session.Events.BuildEvent(new Enrolled("Student"));
        enrolled.WithTag(new EnrolleeId(Guid.NewGuid()), programId);
        boundary.AppendOne(enrolled);

        try
        {
            await session.SaveChangesAsync();
        }
        catch (DcbConcurrencyException)
        {
        }
    }

    private async Task SeedUnrelatedActivityAsync(ProgramId programId)
    {
        await using var session = _store.LightweightSession();
        var progress = session.Events.BuildEvent(new ProgressRecorded("kickoff"));
        progress.WithTag(new EnrolleeId(Guid.NewGuid()), programId);
        session.Events.Append(Guid.NewGuid(), progress);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<long> EnrollmentCountAsync(ProgramId programId)
    {
        await using var session = _store.LightweightSession();
        var enrollments = await session.Events.QueryByTagsAsync(
            new EventTagQuery().Or<Enrolled, ProgramId>(programId), TestContext.Current.CancellationToken);

        return enrollments.Count;
    }
}
