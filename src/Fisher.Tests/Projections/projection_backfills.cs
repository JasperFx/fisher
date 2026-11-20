using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Projections;

/// <summary>
///     fisher#643 / jasperfx#1222: what <c>BackfillCompliance</c> does not reach. Fisher refuses a backfill where it
///     would patch nothing or apply an event twice: a version bump whose prior version wrote another table
///     (fisher#638), and a rebuild, of the projection or of one stream, while a backfill has not finished.
/// </summary>
public class projection_backfills : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("projection-backfills");
    private readonly List<DocumentStore> _stores = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }

        _database.Dispose();
    }

    private async Task<DocumentStore> storeAsync(ProjectionBase projection,
        ProjectionLifecycle lifecycle = ProjectionLifecycle.Async)
    {
        var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.EventGraph.HasJobHost = true;
            options.Projections.Add(projection, lifecycle);
        });

        _stores.Add(store);
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        return store;
    }

    private static async Task runDaemonAsync(DocumentStore store)
    {
        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();
        try
        {
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);
        }
        finally
        {
            await daemon.StopAllAsync();
        }
    }

    private static async Task<Guid> aLedgerAsync(DocumentStore store, params int[] amounts)
    {
        var id = Guid.NewGuid();
        await using var session = store.LightweightSession();
        session.Events.StartStream<BackfilledLedger>(id,
            [new BackfillLedgerOpened(), ..amounts.Select(x => (object)new BackfillLedgerCredited(x))]);
        await session.SaveChangesAsync(Token);
        return id;
    }

    private static async Task creditAsync(DocumentStore store, Guid id, int amount)
    {
        await using var session = store.LightweightSession();
        session.Events.Append(id, new BackfillLedgerCredited(amount));
        await session.SaveChangesAsync(Token);
    }

    private static async Task<BackfilledLedger> loadAsync(DocumentStore store, Guid id)
    {
        await using var query = store.QuerySession();
        return (await query.LoadAsync<BackfilledLedger>(id, Token)).ShouldNotBeNull();
    }

    private static ShardName liveShard(IEventStore store)
        => store.RegisteredShardNames().Single(x => x.Name == LedgerTotalProjection.ProjectionName);

    private static async Task<BackfillRun> aRunAsync(IEventStore store)
        => await BackfillRun.FromMarksAsync((await store.AllDatabases()).Single(), liveShard(store),
            LedgerTotalProjection.BackfillName, null, Token);

    private static async Task<BackfillProgress> backfillAsync(IEventStore store)
        => (await store.RunBackfillAsync(await aRunAsync(store), Token)).ShouldHaveSingleItem();

    // Version 1 of the projection, counting credits, then the same version with the new member and its backfill
    private async Task<(DocumentStore Store, Guid Ledger)> versionOneWithABackfillAsync(bool runTheNewCode = true)
    {
        var old = await storeAsync(new LedgerCountProjection());
        var ledger = await aLedgerAsync(old, 1, 10);
        await runDaemonAsync(old);

        var current = await storeAsync(new LedgerTotalProjection(1));
        if (runTheNewCode)
        {
            await runDaemonAsync(current);
        }

        return (current, ledger);
    }

    [Fact]
    public async Task a_backfill_without_a_version_bump_fills_the_member_on_what_the_old_code_wrote()
    {
        var (store, ledger) = await versionOneWithABackfillAsync();

        // The new code handles what comes after its own progress at the first start
        await creditAsync(store, ledger, 100);
        await runDaemonAsync(store);

        var total = await backfillAsync(store);

        total.Completed.ShouldBeTrue();
        total.Visited.ShouldBe(2);
        total.Applied.ShouldBe(2);

        var document = await loadAsync(store, ledger);
        document.Credits.ShouldBe(3);
        document.Total.ShouldBe(111);
    }

    [Fact]
    public async Task a_document_that_is_gone_is_visited_and_not_applied()
    {
        var (store, ledger) = await versionOneWithABackfillAsync();

        await using (var session = store.LightweightSession())
        {
            session.Delete<BackfilledLedger>(ledger);
            await session.SaveChangesAsync(Token);
        }

        var total = await backfillAsync(store);

        total.Visited.ShouldBe(2);
        total.Applied.ShouldBe(0);
    }

    [Fact]
    public async Task a_run_built_before_another_run_finished_does_not_apply_an_event_twice()
    {
        var (store, ledger) = await versionOneWithABackfillAsync();

        var first = await aRunAsync(store);
        var second = await aRunAsync(store);

        (await ((IEventStore)store).RunBackfillAsync(second, Token)).ShouldHaveSingleItem().Applied.ShouldBe(2);

        // The first run's cursor said 0, but the stored cursor is at the ceiling now
        var late = (await ((IEventStore)store).RunBackfillAsync(first, Token)).ShouldHaveSingleItem();
        late.Completed.ShouldBeTrue();
        late.Visited.ShouldBe(0);

        (await loadAsync(store, ledger)).Total.ShouldBe(11);
    }

    [Fact]
    public async Task a_version_bump_is_refused_when_the_prior_version_wrote_its_documents_to_another_table()
    {
        var old = await storeAsync(new LedgerCountProjection());
        await aLedgerAsync(old, 1, 10);
        await runDaemonAsync(old);

        // Version 2 gets fi_doc_backfilledledger_2 (fisher#638), so version 1's documents are out of its reach
        var bumped = await storeAsync(new LedgerTotalProjection(2));
        var database = (await ((IEventStore)bumped).AllDatabases()).Single();

        // What the live version's start does, and what the backfill job does
        var refused = await Should.ThrowAsync<InvalidOperationException>(() =>
            EventRangeMarks.CaptureBackfillCeilingAsync(database, liveShard(bumped), LedgerTotalProjection.BackfillName,
                Token));
        await Should.ThrowAsync<InvalidOperationException>(() => aRunAsync(bumped));

        refused.Message.ShouldContain("fi_doc_backfilledledger_2");
        refused.Message.ShouldContain("version 1's documents are in fi_doc_backfilledledger");
        refused.Message.ShouldContain("Declare the backfill without bumping the version");

        // Nothing was captured, so a corrected deployment starts clean
        (await database.FetchEventRangeMarkAsync(
            EventRangeMarks.BackfillKey(LedgerTotalProjection.ProjectionName, LedgerTotalProjection.BackfillName),
            null, Token)).ShouldBeNull();
    }

    [Fact]
    public async Task a_rebuild_is_refused_while_the_backfill_has_not_finished()
    {
        var (store, ledger) = await versionOneWithABackfillAsync();

        using var daemon = await store.BuildProjectionDaemonAsync();
        var refused = await Should.ThrowAsync<Exception>(() =>
            daemon.RebuildProjectionAsync(LedgerTotalProjection.ProjectionName, DaemonWait.Timeout, Token));

        messageOf(refused).ShouldContain("its backfill 'total' has not finished");
        messageOf(refused).ShouldContain("its cursor is at sequence 0");

        // The teardown deleted nothing
        (await loadAsync(store, ledger)).Credits.ShouldBe(2);

        // Once the backfill finished, the rebuild goes ahead and fills the member itself
        await backfillAsync(store);
        await daemon.RebuildProjectionAsync(LedgerTotalProjection.ProjectionName, DaemonWait.Timeout, Token);

        var rebuilt = await loadAsync(store, ledger);
        rebuilt.Total.ShouldBe(11);
        (await backfillAsync(store)).Visited.ShouldBe(0);
        (await loadAsync(store, ledger)).Total.ShouldBe(11);
    }

    [Fact]
    public async Task a_rebuild_is_refused_before_the_backfill_has_started()
    {
        var (store, _) = await versionOneWithABackfillAsync(runTheNewCode: false);

        using var daemon = await store.BuildProjectionDaemonAsync();
        var refused = await Should.ThrowAsync<Exception>(() =>
            daemon.RebuildProjectionAsync(LedgerTotalProjection.ProjectionName, DaemonWait.Timeout, Token));

        messageOf(refused).ShouldContain("it has not started");
    }

    [Fact]
    public async Task rebuilding_one_stream_is_refused_while_the_backfill_has_not_finished()
    {
        var (store, ledger) = await versionOneWithABackfillAsync();

        var refused = await Should.ThrowAsync<InvalidOperationException>(() =>
            store.Advanced.RebuildSingleStreamAsync<BackfilledLedger>(ledger, token: Token));
        refused.Message.ShouldContain("Cannot rebuild a stream of projection Ledger");
        (await loadAsync(store, ledger)).Total.ShouldBe(0);
    }

    [Fact]
    public void a_backfill_on_an_inline_projection_is_refused_at_startup()
    {
        var refused = Should.Throw<InvalidOperationException>(() => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.EventGraph.HasJobHost = true;
            options.Projections.Add(new LedgerTotalProjection(1), ProjectionLifecycle.Inline);
        }));

        refused.Message.ShouldContain("registered as Inline");
    }

    [Fact]
    public void a_backfill_inside_a_composite_is_refused_at_startup()
    {
        var refused = Should.Throw<InvalidOperationException>(() => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.EventGraph.HasJobHost = true;
            options.Projections.CompositeProjectionFor("ledgers", composite => composite.Add(new LedgerTotalProjection(1)));
        }));

        refused.Message.ShouldContain("inside the composite projection ledgers");
    }

    private static string messageOf(Exception exception)
    {
        var messages = new List<string>();
        for (var e = exception; e != null; e = e.InnerException)
        {
            messages.Add(e.Message);
        }

        return string.Join(" | ", messages);
    }
}

public record BackfillLedgerOpened;

public record BackfillLedgerCredited(int Amount);

public class BackfilledLedger
{
    public Guid Id { get; set; }
    public int Credits { get; set; }

    /// <summary>The member the new code adds.</summary>
    public int Total { get; set; }
}

/// <summary>The old code: counts credits.</summary>
public class LedgerCountProjection : Fisher.Projections.SingleStreamProjection<BackfilledLedger, Guid>
{
    public LedgerCountProjection()
    {
        Name = LedgerTotalProjection.ProjectionName;
    }

    public void Apply(BackfillLedgerOpened _, BackfilledLedger ledger)
    {
    }

    public void Apply(BackfillLedgerCredited _, BackfilledLedger ledger) => ledger.Credits++;
}

/// <summary>The new code: totals the credits too, and backfills the total on what the old code wrote.</summary>
public class LedgerTotalProjection : Fisher.Projections.SingleStreamProjection<BackfilledLedger, Guid>
{
    public const string ProjectionName = "Ledger";
    public const string BackfillName = "total";

    public LedgerTotalProjection(uint version)
    {
        Name = ProjectionName;
        Version = version;

        Options.SubscribeFromPriorVersionMark(BackfillName);
        Backfill(BackfillName).On<BackfillLedgerCredited>((ledger, e) => ledger.Total += e.Amount);
    }

    public void Apply(BackfillLedgerOpened _, BackfilledLedger ledger)
    {
    }

    public void Apply(BackfillLedgerCredited e, BackfilledLedger ledger)
    {
        ledger.Credits++;
        ledger.Total += e.Amount;
    }
}
