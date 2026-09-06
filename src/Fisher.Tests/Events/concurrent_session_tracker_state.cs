using Fisher.Internal;
using Fisher.Tests.Documents;
using JasperFx;
using Weasel.Core;
using Weasel.Storage;

namespace Fisher.Tests.Events;

/// <summary>
///     The rest of the session's shared mutable state under the async daemon's concurrent writers —
///     marten#4657 / marten#4667, and the half of fisher#13 that the operation queue's lock did not
///     cover.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this is a real caller and not a synthetic one.</b> JasperFx's <c>ExecutionStage</c>
///         fans its executions out with <c>Task.WhenAll</c> and they all queue onto the <em>same</em>
///         Fisher session — the premise <c>concurrent_operation_queueing</c> already establishes and
///         fisher#13 was. <c>_operations</c> got a lock out of that bug. The session's other shared
///         fields did not, and two of them are on the write path a projection slice takes:
///         <c>Versions</c> is resolved by <c>NumericLightweightFisherStorage.Upsert</c> and its
///         optimistic twin at operation-construction time, on the calling thread, and
///         <c>_queryProvider</c> is resolved by <c>Query&lt;T&gt;()</c>, which is how a
///         <c>ViewProjection</c> looks up what it is about to fold into.
///     </para>
///     <para>
///         <b>What goes wrong is not an exception.</b> <c>_versionTracker ??= new FisherVersionTracker()</c>
///         is a read, a branch and a write, so two slices arriving together each construct a tracker
///         and one is silently discarded along with everything recorded on it. The next optimistic or
///         numeric write then guards against a version the session no longer remembers reading — a
///         spurious <c>ConcurrencyException</c> at best, a stale write accepted at worst. One layer
///         down, <c>FisherVersionTracker</c>'s two <c>Dictionary&lt;Type, object&gt;</c> fields are
///         mutated unguarded by <c>ForType</c> / <c>RevisionsFor</c>, which is the shape that loses
///         entries outright or spins forever inside a resize.
///     </para>
///     <para>
///         Marten's #4657 is exactly this: projection slices handed sessions that shared a
///         <c>VersionTracker</c>, an <c>ItemMap</c> and a <c>ChangeTrackers</c> list, racing on them.
///         Fisher shares one session rather than several that share state, which arrives at the same
///         place by a shorter route.
///     </para>
///     <para>
///         <b>The identity map and the change trackers are deliberately not covered here</b>, and the
///         reason is a real invariant rather than an omission: both are populated only under
///         <c>DocumentTracking.IdentityOnly</c> or <c>DirtyTracking</c>, and the daemon opens its
///         sessions through <c>DocumentStore.OpenSessionOn</c>, which takes the default
///         <c>DocumentTracking.None</c>. <c>the_daemons_own_sessions_are_untracked</c> below is what
///         keeps that true, because it is the premise the omission rests on.
///     </para>
/// </remarks>
public class concurrent_session_tracker_state : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("tracker-races");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    ///     Release <paramref name="workers" /> tasks at the same instant and wait for all of them, so
    ///     the contended field is genuinely contended rather than reached by staggered thread starts.
    /// </summary>
    private static Task ReleaseTogetherAsync(int workers, Func<int, Task> work)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, workers).Select(i => Task.Run(async () =>
        {
            await start.Task;
            await work(i);
        })).ToArray();

        start.SetResult();

        return Task.WhenAll(tasks);
    }

    /// <remarks>
    ///     Repeated, because the window is the gap between the null check and the assignment and one
    ///     attempt can easily miss it. A single duplicate anywhere across the rounds is a failure:
    ///     under the daemon that is one slice's version bookkeeping thrown away.
    /// </remarks>
    [Fact]
    public async Task concurrent_slices_see_one_version_tracker()
    {
        const int rounds = 300;
        const int workers = 8;

        for (var round = 0; round < rounds; round++)
        {
            await using var session = _store.LightweightSession();
            var storage = (IStorageSession)session;

            var seen = new IVersionTracker[workers];

            await ReleaseTogetherAsync(workers, i =>
            {
                seen[i] = storage.Versions;
                return Task.CompletedTask;
            });

            seen.Distinct().Count().ShouldBe(1);
        }
    }

    /// <remarks>
    ///     The same window on the query provider. A projection slice that reads before it writes —
    ///     the shape marten#4667 actually bit on — takes it through <c>Query&lt;T&gt;()</c>.
    /// </remarks>
    [Fact]
    public async Task concurrent_slices_see_one_query_provider()
    {
        const int rounds = 300;
        const int workers = 8;

        for (var round = 0; round < rounds; round++)
        {
            await using var session = _store.LightweightSession();

            var seen = new object[workers];

            await ReleaseTogetherAsync(workers, i =>
            {
                seen[i] = session.Query<Permit>().Provider;
                return Task.CompletedTask;
            });

            seen.Distinct().Count().ShouldBe(1);
        }
    }

    /// <remarks>
    ///     <para>
    ///         The realistic shape, one level up from the field: many concurrent <c>Store</c> calls
    ///         for a revisioned type on one session, which is what a multi-stream projection writing
    ///         snapshots does. Every one of them resolves
    ///         <c>session.Versions.RevisionsFor&lt;Permit, Guid&gt;()</c> while constructing its
    ///         upsert, on the calling thread.
    ///     </para>
    ///     <para>
    ///         <b>The assertion is on the dictionary's identity, not on its contents, and the reason
    ///         is worth stating because the obvious assertion is vacuous.</b> An operation is handed
    ///         the revision dictionary at construction and writes into it during <em>postprocessing</em>,
    ///         which happens when the batch executes — so before a <c>SaveChangesAsync</c> the
    ///         dictionary is empty however the race went, and a count assertion here reads zero on
    ///         correct code. What the loss actually looks like is two dictionaries: the operations
    ///         built first hold one, the tracker ends up holding another, and everything the first
    ///         set postprocesses lands in an orphan no later write will ever consult.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task concurrent_stores_capture_one_revision_dictionary()
    {
        const int workers = 8;
        const int each = 250;

        await using var session = _store.LightweightSession();
        var storage = (IStorageSession)session;

        var captured = new Dictionary<Guid, long>[workers];

        await ReleaseTogetherAsync(workers, w =>
        {
            // Exactly what NumericLightweightFisherStorage.Upsert does while building each operation.
            captured[w] = storage.Versions.RevisionsFor<Permit, Guid>();

            for (var i = 0; i < each; i++)
            {
                var permit = new Permit { Id = Guid.NewGuid(), Description = "Trout, one rod" };
                session.UpdateRevision(permit, 1);
            }

            return Task.CompletedTask;
        });

        captured.Distinct().Count().ShouldBe(1);

        // fisher#13's own property, re-checked here because this file drives the same session the
        // same way and a regression in either lock would show up first as a lost operation.
        ((FisherSession)session).PendingOperations.Count.ShouldBe(workers * each);
    }

    /// <remarks>
    ///     The version tracker's own dictionaries, driven directly across several document types.
    ///     One type alone barely exercises them — the outer <c>Dictionary&lt;Type, object&gt;</c> is
    ///     written once and read forever after — so the contention that matters comes from a
    ///     composite projection whose concurrent slices write <em>different</em> snapshot types.
    /// </remarks>
    [Fact]
    public async Task the_version_tracker_keeps_every_type_written_concurrently()
    {
        const int rounds = 200;

        for (var round = 0; round < rounds; round++)
        {
            await using var session = _store.LightweightSession();
            var versions = ((IStorageSession)session).Versions;

            var id = Guid.NewGuid();

            await ReleaseTogetherAsync(4, i =>
            {
                switch (i)
                {
                    case 0:
                        versions.StoreVersion<Permit, Guid>(id, Guid.NewGuid());
                        break;
                    case 1:
                        versions.StoreVersion<Licence, Guid>(id, Guid.NewGuid());
                        break;
                    case 2:
                        versions.StoreRevision<Permit, Guid>(id, 1);
                        break;
                    default:
                        versions.StoreRevision<Licence, Guid>(id, 1);
                        break;
                }

                return Task.CompletedTask;
            });

            versions.VersionFor<Permit, Guid>(id).ShouldNotBeNull();
            versions.VersionFor<Licence, Guid>(id).ShouldNotBeNull();
            versions.RevisionFor<Permit, Guid>(id).ShouldBe(1);
            versions.RevisionFor<Licence, Guid>(id).ShouldBe(1);
        }
    }

    /// <remarks>
    ///     The premise the identity map and change trackers are left unguarded on. If the daemon ever
    ///     opened a tracking session, both would become concurrently mutated too and this file would
    ///     need two more tests — so the assertion belongs here rather than in a configuration test.
    /// </remarks>
    [Fact]
    public void the_daemons_own_sessions_are_untracked()
    {
        var session = (FisherSession)_store.OpenSessionOn(_store.Database, StorageConstants.DefaultTenantId);

        session.SessionOptions.Tracking.ShouldBe(DocumentTracking.None);
    }
}
