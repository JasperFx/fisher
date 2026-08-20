using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fisher.EntityFrameworkCore.Tests;

/// <summary>
///     fisher#108, over jasperfx#683 — an EF Core projection storage declares itself not thread-safe,
///     so the daemon applies a range's slices one at a time instead of ten-wide.
/// </summary>
/// <remarks>
///     <para>
///         <c>AggregationRunner</c> posts every slice of a range into a fixed ten-wide <c>Block</c> —
///         ten real reader tasks — against one storage instance. That is fine for Fisher's own document
///         storage, which queues onto a session whose operation queue fisher#13 made thread-safe for
///         this exact fan-out; it is not fine for a <c>DbContext</c>, which is explicitly not
///         thread-safe. Reported on Marten as <c>InvalidOperationException</c> out of
///         <c>Dictionary.TryInsert</c> and <c>NullReferenceException</c> out of
///         <c>ChangeDetector.DetectChanges</c> (marten#5266).
///     </para>
///     <para>
///         <b>What is asserted here is that the fan-out stopped, not that a crash stopped.</b> That is
///         deliberate, and it is the stronger of the two: the corruption is a data race, so a test
///         waiting for it to throw is probabilistic in the direction that fails you — green would prove
///         nothing. Concurrency itself is directly observable, so <see cref="SquadTallyProjection" />
///         records the most <c>Apply</c> calls ever in flight at once, which is ten-wide before the
///         declaration and exactly one after it. Measured rather than assumed: with <c>IsThreadSafe</c>
///         forced back to <c>true</c> this suite observes 9 concurrent applies and
///         <see cref="slices_are_applied_one_at_a_time" /> fails on the number.
///     </para>
///     <para>
///         <b>Fisher was harder to break than Marten, and why is worth keeping.</b> The storage's own
///         <c>SemaphoreSlim</c> serializes each individual call, so the simultaneous-call corruption
///         Marten hit is already closed here — 15,000 slice applications over a forced ten-wide run
///         produced no exception at all. What a lock cannot close is the window <em>between</em> two
///         calls, where one thread's aggregation mutates entities that another thread's <c>Entry()</c>
///         is running <c>DetectChanges</c> over; that window is why the fix has to be the fan-out
///         rather than the lock. It is also why this was never reported against Fisher in the wild
///         while it was against Marten — and why it still had to be fixed.
///     </para>
///     <para>
///         <c>Identities</c> rather than a single-stream projection, because one event naming every
///         squad guarantees a range far wider than the block; a single-stream projection reaches the
///         same place only when a range happens to span several streams.
///     </para>
///     <para>
///         This suite registers through <c>Projections.Add</c> rather than <c>Snapshot&lt;T&gt;</c>,
///         which is forced — <c>Add</c> is the only door for a multi-stream projection — and is also
///         why <see cref="an_ef_backed_type_registered_through_add_gets_no_fisher_document_table" />
///         lives here: fisher#111 was that only <c>Snapshot&lt;T&gt;</c> guarded its mapping, so this
///         registration left a stray, empty <c>fi_doc_squadtally</c> table behind.
///     </para>
/// </remarks>
public class ef_core_projection_concurrency : IAsyncLifetime
{
    // Wider than AggregationRunner's ten-slice block, so a range always holds more slices than the
    // block can take at once and the concurrent path is genuinely exercised rather than incidentally
    // serial.
    private const int SquadCount = 60;
    private const int AuditCount = 10;

    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("ef-projection-concurrency");
    private DocumentStore _store = null!;
    private IProjectionDaemon? _daemon;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string[] Squads =>
        Enumerable.Range(0, SquadCount).Select(i => $"squad-{i:D3}").ToArray();

    public async ValueTask InitializeAsync()
    {
        SquadTallyProjection.ResetConcurrencyProbe();

        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            options.ProjectToEfCore<SquadTally, string, SquadContext>("SquadTallies", ContextForReads);
            options.Projections.Add(new SquadTallyProjection(), ProjectionLifecycle.Async);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "create table if not exists SquadTallies (Id text primary key, Audits integer not null)";
        await command.ExecuteNonQueryAsync(Token);
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

    private SquadContext ContextForReads()
        => new(new DbContextOptionsBuilder<SquadContext>().UseSqlite(_database.ConnectionString).Options);

    private async Task AppendAuditsAsync()
    {
        await using var session = _store.LightweightSession();

        for (var i = 0; i < AuditCount; i++)
        {
            session.Events.StartStream(Guid.NewGuid(), new SquadsAudited(Squads));
        }

        await session.SaveChangesAsync(Token);
    }

    private async Task RunDaemonAsync()
    {
        _daemon ??= await _store.BuildProjectionDaemonAsync();
        await _daemon.StartAllAsync();
        await _store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(60));
    }

    private async Task<List<SquadTally>> TalliesAsync()
    {
        await using var context = ContextForReads();

        return await context.SquadTallies.OrderBy(x => x.Id).ToListAsync(Token);
    }

    /// <summary>
    ///     The declaration, read back through the seam the daemon resolves storage through.
    /// </summary>
    /// <remarks>
    ///     Asserted against the resolved storage rather than by constructing the internal type, so what
    ///     is pinned is what <c>AggregationRunner</c> will actually ask — a registration that stopped
    ///     routing to the EF storage would fail here rather than silently going back to ten-wide.
    /// </remarks>
    [Fact]
    public async Task an_ef_backed_storage_declares_itself_not_thread_safe()
    {
        await using var session = _store.LightweightSession();

        var storage = await ((IStorageOperations)session)
            .FetchProjectionStorageAsync<SquadTally, string>(StorageConstants.DefaultTenantId, Token);

        storage.IsThreadSafe.ShouldBeFalse();
    }

    /// <summary>
    ///     And the contrast, because "not thread-safe" has to be the EF storage's answer rather than
    ///     Fisher's.
    /// </summary>
    /// <remarks>
    ///     <c>FisherProjectionStorage</c> keeps JasperFx's default of <c>true</c>: it queues operations
    ///     onto the session, whose queue is guarded precisely so the daemon can fan out onto it
    ///     (fisher#13). Serializing every projection would cost throughput for every store that does
    ///     not use EF and nothing would report it, so both answers are pinned rather than one.
    /// </remarks>
    [Fact]
    public async Task fishers_own_projection_storage_is_still_thread_safe()
    {
        await using var session = _store.LightweightSession();

        var storage = await ((IStorageOperations)session)
            .FetchProjectionStorageAsync<PlainTally, Guid>(StorageConstants.DefaultTenantId, Token);

        storage.IsThreadSafe.ShouldBeTrue();
    }

    /// <summary>
    ///     fisher#111 — the other registration door leaves no stray Fisher table either.
    /// </summary>
    /// <remarks>
    ///     <c>an_ef_backed_type_gets_no_fisher_document_table</c> covers the same property for
    ///     <c>Snapshot&lt;T&gt;</c>, which was the only door that had the guard. Asserted on the name
    ///     rather than on "no <c>fi_doc</c> tables at all", because
    ///     <see cref="fishers_own_projection_storage_is_still_thread_safe" /> resolves storage for a
    ///     genuine Fisher document and creates <c>fi_doc_plaintally</c> on demand — a blanket assertion
    ///     would pass or fail on test ordering.
    /// </remarks>
    [Fact]
    public async Task an_ef_backed_type_registered_through_add_gets_no_fisher_document_table()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "select count(*) from sqlite_master where type = 'table' and name = 'fi_doc_squadtally'";

        var count = Convert.ToInt64(await command.ExecuteScalarAsync(Token));

        count.ShouldBe(0);
    }

    /// <summary>
    ///     The regression: the daemon never has two of this projection's slices in flight at once.
    /// </summary>
    /// <remarks>
    ///     This is the test that fails without the declaration, and it fails on a number rather than on
    ///     a crash — see the class remarks for why that is the stronger assertion. The projection's
    ///     <c>Apply</c> yields, which is what makes overlap observable at all: ten threads each applying
    ///     one increment can finish without ever coinciding, where anything with an await point in it
    ///     coincides immediately if the runner is fanning out.
    /// </remarks>
    [Fact]
    public async Task slices_are_applied_one_at_a_time()
    {
        await AppendAuditsAsync();
        await RunDaemonAsync();

        // Guards a vacuous pass: with nothing applied there is no concurrency to observe, and a max of
        // zero or one would "prove" serialization without the projection having run at all.
        SquadTallyProjection.AppliedCount.ShouldBe(SquadCount * AuditCount);
        SquadTallyProjection.MaxConcurrentApplies.ShouldBe(1);
    }

    /// <summary>
    ///     And every slice's write still lands, which is the half a lock alone would not save.
    /// </summary>
    /// <remarks>
    ///     A torn change tracker can lose a slice's write as easily as it can throw — fisher#13's shape
    ///     one layer over — so asserting each squad reached its full audit count is what separates "did
    ///     not crash" from "wrote everything".
    /// </remarks>
    [Fact]
    public async Task a_wide_fan_out_applies_every_slice()
    {
        await AppendAuditsAsync();
        await RunDaemonAsync();

        var tallies = await TalliesAsync();

        tallies.Count.ShouldBe(SquadCount);
        tallies.ShouldAllBe(x => x.Audits == AuditCount);
    }

    /// <summary>
    ///     And the same under a rebuild, which is the shape the failures were reported against.
    /// </summary>
    /// <remarks>
    ///     A rebuild replays every event through the same runner with no throttling from the append
    ///     side, so slices arrive as fast as the loader can page them — the densest fan-out the daemon
    ///     ever produces.
    /// </remarks>
    [Fact]
    public async Task a_wide_fan_out_survives_a_rebuild()
    {
        await AppendAuditsAsync();
        await RunDaemonAsync();

        SquadTallyProjection.ResetConcurrencyProbe();

        await _daemon!.RebuildProjectionAsync("SquadTally", TimeSpan.FromSeconds(60), Token);

        var tallies = await TalliesAsync();

        tallies.Count.ShouldBe(SquadCount);
        tallies.ShouldAllBe(x => x.Audits == AuditCount);
        SquadTallyProjection.MaxConcurrentApplies.ShouldBe(1);
    }
}

/// <remarks>
///     <para>
///         <c>Identities</c> is what produces the fan-out: one event names every squad, so a single
///         event becomes <c>SquadCount</c> slices and a range of them many times the block's width.
///     </para>
///     <para>
///         The counters are this suite's instrument, and they live here rather than in the storage so
///         that nothing is added to production code for a test's benefit. <c>Apply</c> runs inside the
///         slice execution, so two applies in flight at once <em>is</em> two slices in flight at once —
///         the thing under test, observed without reaching past the public projection surface.
///     </para>
/// </remarks>
public partial class SquadTallyProjection : Projections.MultiStreamProjection<SquadTally, string>
{
    private static int _current;
    private static int _max;
    private static int _applied;

    public SquadTallyProjection()
    {
        Name = "SquadTally";
        Identities<SquadsAudited>(x => x.Squads);
    }

    public static int MaxConcurrentApplies => Volatile.Read(ref _max);

    public static int AppliedCount => Volatile.Read(ref _applied);

    public static void ResetConcurrencyProbe()
    {
        Volatile.Write(ref _current, 0);
        Volatile.Write(ref _max, 0);
        Volatile.Write(ref _applied, 0);
    }

    public async Task Apply(SquadsAudited _, SquadTally tally)
    {
        var inFlight = Interlocked.Increment(ref _current);

        int seen;
        while (inFlight > (seen = Volatile.Read(ref _max)))
        {
            Interlocked.CompareExchange(ref _max, inFlight, seen);
        }

        try
        {
            // An await point, so overlap is observable at all rather than depending on ten increments
            // happening to coincide.
            await Task.Yield();

            tally.Audits++;
            Interlocked.Increment(ref _applied);
        }
        finally
        {
            Interlocked.Decrement(ref _current);
        }
    }
}

public class SquadContext : DbContext
{
    public SquadContext(DbContextOptions<SquadContext> options) : base(options)
    {
    }

    public DbSet<SquadTally> SquadTallies => Set<SquadTally>();
}

public class SquadTally
{
    public string Id { get; set; } = string.Empty;
    public int Audits { get; set; }
}

/// <remarks>
///     A Fisher-stored document, so the contrast test has a non-EF projection storage to resolve. It is
///     never projected into — resolving its storage is the whole of its job.
/// </remarks>
public class PlainTally
{
    public Guid Id { get; set; }
    public int Count { get; set; }
}

public record SquadsAudited(string[] Squads);
