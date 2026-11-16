using System.Diagnostics;
using Fisher.Linq;
using JasperFx;
using Weasel.Core.Sequences;

namespace Fisher.Tests.Configuration;

public class BurstHiloDoc
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
///     fisher#587: a first burst of synchronous Hi-Lo <c>Store()</c> calls, each advance opening a connection
///     synchronously, alongside async saves through the same per-connection-string open gate. The burst
///     must not stall the thread pool.
/// </summary>
/// <remarks>
///     Run alone in a fresh process (<c>Fisher.Tests --filter-class "*synchronous_hilo_burst*"</c>), it took
///     29 s against the semaphore gate (fisher#582) and 0.56 s against the fix. In a full suite run the thread
///     pool is already warm. The heartbeat bound did not trip against the old gate either: the stall shows in
///     wall clock, which this repository deliberately does not budget in a test (fisher#189). So this fact
///     covers correctness (every id unique) and documents the burst.
///     <c>serialized_connection_opens.a_contended_async_open_completes_before_it_returns</c> is the
///     discriminating pin of the mechanism.
/// </remarks>
public class synchronous_hilo_burst : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("hilo-burst");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        // MaxLo 3, so blocks run out constantly and every few stores advance the hi synchronously.
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<BurstHiloDoc>().HiloSettings(new HiloSettings { MaxLo = 3 });
        });

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task a_first_burst_of_synchronous_hilo_stores_does_not_starve_the_pool()
    {
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
        }, Token);

        const int writers = 64;
        const int perWriter = 10;

        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                await using var session = _store.LightweightSession();
                session.Store(new BurstHiloDoc { Name = $"{w}-{i}" }); // assigns the id synchronously
                await session.SaveChangesAsync(Token);
            }
        }, Token)).ToArray();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60), Token);
        await stop.CancelAsync();
        await heartbeat;

        await using var query = _store.LightweightSession();
        var ids = await query.Query<BurstHiloDoc>().Select(x => x.Id).ToListAsync(Token);
        ids.Count.ShouldBe(writers * perWriter);
        ids.Distinct().Count().ShouldBe(writers * perWriter);

        worstLateness.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }
}
