using System.Diagnostics;
using Fisher.Linq;
using JasperFx;
using JasperFx.Events;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Events;

public class ProbeHiloDoc
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class ProbeGuidDoc
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
///     Probes of the non-LINQ bugs Marten fixed on 2026-11-15: a version conflict mis-attributed to a
///     stream started in the same save (marten#5914), a connection leaked by a cancelled transaction
///     begin (marten#5881), and thread-pool starvation on the first burst of synchronous Hi-Lo
///     <c>Store()</c> calls (marten#5969).
/// </summary>
public class marten_1115_session_probes : IAsyncLifetime
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fisher-m1115-" + Guid.NewGuid().ToString("n")[..8]);

    private string _path = null!;
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "probe.db");
        _store = BuildStore();
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    private DocumentStore BuildStore(int maxLo = 1000) => DocumentStore.For(options =>
    {
        options.ConnectionString = $"Data Source={_path}";
        options.AutoCreateSchemaObjects = AutoCreate.All;
        options.Schema.For<ProbeHiloDoc>().HiloSettings(new Weasel.Core.Sequences.HiloSettings { MaxLo = maxLo });
    });

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- marten#5914 ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task a_version_conflict_behind_a_new_stream_is_reported_against_the_fetched_stream(bool startFirst)
    {
        var fetched = Guid.NewGuid();
        var started = Guid.NewGuid();

        await using (var setup = _store.LightweightSession())
        {
            setup.Events.StartStream<QuestParty>(fetched, new QuestStarted("q"), new MemberJoined("Frodo"));
            await setup.SaveChangesAsync(Token);
        }

        await using var session = _store.LightweightSession();
        var stream = await session.Events.FetchForWriting<QuestParty>(fetched, Token);

        // Somebody else moves the fetched stream on.
        await using (var other = _store.LightweightSession())
        {
            other.Events.Append(fetched, new MemberJoined("Sam"));
            await other.SaveChangesAsync(Token);
        }

        if (startFirst)
        {
            session.Events.StartStream<QuestParty>(started, new QuestStarted("new"));
            stream.AppendOne(new MemberJoined("Merry"));
        }
        else
        {
            stream.AppendOne(new MemberJoined("Merry"));
            session.Events.StartStream<QuestParty>(started, new QuestStarted("new"));
        }

        var thrown = await Should.ThrowAsync<Exception>(() => session.SaveChangesAsync(Token));

        thrown.ShouldBeAssignableTo<ConcurrencyException>(
            $"Expected a concurrency failure on the fetched stream, got {thrown.GetType().FullName}: {thrown.Message}");
        thrown.ShouldNotBeOfType<ExistingStreamIdCollisionException>();
        thrown.Message.ShouldContain(fetched.ToString(), Case.Insensitive);

        await using var query = _store.LightweightSession();
        (await query.Events.FetchStreamStateAsync(started, Token)).ShouldBeNull();
    }

    // ---- marten#5881 ----

    private bool IsOpen() => File.Exists(_path + "-wal");

    private async Task ShouldCloseAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (IsOpen() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20, Token);
        }

        IsOpen().ShouldBeFalse("the database file was never released, so a connection leaked");
    }

    private sealed class CancellingParticipant(CancellationTokenSource source) : ITransactionParticipant
    {
        public Task BeforeCommitAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
        {
            source.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task a_cancelled_save_after_the_connection_opened_leaks_nothing()
    {
        await using (var session = _store.LightweightSession())
        {
            // Opens the session's connection before the save.
            (await session.Query<ProbeHiloDoc>().ToListAsync(Token)).ShouldBeEmpty();

            session.Store(new ProbeHiloDoc { Name = "cancelled-before-begin" });

            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await Should.ThrowAsync<OperationCanceledException>(() => session.SaveChangesAsync(cancelled.Token));
        }

        await using (var session = _store.LightweightSession())
        {
            using var source = new CancellationTokenSource();
            session.Store(new ProbeHiloDoc { Name = "cancelled-inside-the-transaction" });
            session.AddTransactionParticipant(new CancellingParticipant(source));

            await Should.ThrowAsync<OperationCanceledException>(() => session.SaveChangesAsync(source.Token));
        }

        // The pool must hand back a usable connection: no stray transaction left behind.
        await using (var session = _store.LightweightSession())
        {
            session.Store(new ProbeHiloDoc { Name = "after" });
            await session.SaveChangesAsync(Token);

            (await session.Query<ProbeHiloDoc>().Select(x => x.Name).ToListAsync(Token)).ShouldBe(["after"]);
        }

        await _store.DisposeAsync();
        await ShouldCloseAsync();
        _store = BuildStore();
    }

    // ---- marten#5969 ----

    /// <summary>
    ///     A burst of synchronous <c>Store()</c> calls on an int-keyed type whose sequence (and fi_hilo table)
    ///     has never been touched, with a small MaxLo so blocks run out while async saves open connections
    ///     through the same per-connection-string gate. A heartbeat measures whether the pool stalls.
    /// </summary>
    [Theory]
    [InlineData("hilo-cold-maxlo3", true, false, 3, Skip = "fisher: a sync Hi-Lo advance blocks on SerializedOpenDataSource's SemaphoreSlim (fisher#576) and starves the thread pool for ~30s in a fresh process")]
    [InlineData("hilo-cold-maxlo1000", true, false, 1000)]
    [InlineData("hilo-migrated-maxlo3", true, true, 3, Skip = "fisher: a sync Hi-Lo advance blocks on SerializedOpenDataSource's SemaphoreSlim (fisher#576) and starves the thread pool for ~30s in a fresh process")]
    [InlineData("guid-control", false, true, 1000)]
    public async Task a_first_burst_of_synchronous_hilo_stores_does_not_starve_the_pool(string label, bool hilo,
        bool migrateFirst, int maxLo)
    {
        var only = Environment.GetEnvironmentVariable("FISHER_BURST_ONLY");
        if (!string.IsNullOrEmpty(only) && only != label) return;

        await _store.DisposeAsync();
        File.Delete(_path);
        _store = BuildStore(maxLo: maxLo);

        // Cold variants run no migration: the first Store() creates fi_hilo through EnsureTableSync.
        if (migrateFirst)
        {
            await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        }

        using var stop = new CancellationTokenSource();
        var worstLateness = TimeSpan.Zero;

        var heartbeat = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    await Task.Delay(10, stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                var late = watch.Elapsed - TimeSpan.FromMilliseconds(10);
                if (late > worstLateness) worstLateness = late;
            }
        });

        const int writers = 64;
        const int perWriter = 10;
        var total = Stopwatch.StartNew();

        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                await using var session = _store.LightweightSession();
                if (hilo)
                {
                    session.Store(new ProbeHiloDoc { Name = $"{w}-{i}" }); // synchronous Hi-Lo assignment
                }
                else
                {
                    session.Store(new ProbeGuidDoc { Name = $"{w}-{i}" });
                }

                await session.SaveChangesAsync(Token);
            }
        })).ToArray();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60), Token);
        total.Stop();
        await stop.CancelAsync();
        await heartbeat;

        await using var query = _store.LightweightSession();
        if (hilo)
        {
            var ids = await query.Query<ProbeHiloDoc>().Select(x => x.Id).ToListAsync(Token);
            ids.Count.ShouldBe(writers * perWriter);
            ids.Distinct().Count().ShouldBe(writers * perWriter);
        }
        else
        {
            (await query.Query<ProbeGuidDoc>().CountAsync(Token)).ShouldBe(writers * perWriter);
        }

        var report = Environment.GetEnvironmentVariable("FISHER_PROBE_REPORT");
        if (!string.IsNullOrEmpty(report))
        {
            File.AppendAllText(report,
                $"5969 burst {label}\twall={total.ElapsedMilliseconds}ms worst heartbeat lateness={worstLateness.TotalMilliseconds:F0}ms\n");
        }

        worstLateness.ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }
}
