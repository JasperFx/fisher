using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Fisher.TestUtils;

namespace Fisher.Benchmarks;

/// <summary>
///     The allocation-shaped half of the harness: BenchmarkDotNet with <c>MemoryDiagnoser</c> over a
///     single unit of work, where per-operation allocations and tight per-commit timing matter and a
///     wall-clock loop would hide them.
/// </summary>
/// <remarks>
///     The checked-in default is a short in-process run so <c>dotnet run -c Release -- bdn</c>
///     finishes in minutes; pass BenchmarkDotNet's own arguments after <c>bdn</c> (for example
///     <c>--job medium</c>) for a publishable run — see the README.
/// </remarks>
public class MicroBenchmarkConfig : ManualConfig
{
    public MicroBenchmarkConfig()
    {
        AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance));
        AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default);
    }
}

[Config(typeof(MicroBenchmarkConfig))]
public class DocSaveBenchmarks
{
    private TemporaryDatabase _database = null!;
    private DocumentStore _store = null!;
    private BenchDoc[] _docs = null!;

    [Params(100, 1000)]
    public int Documents { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _database = TemporaryDatabase.Create("bdn-doc-save");
        _store = Scenarios.Harness.BuildStore(_database);
        await _store.ApplyAllConfiguredChangesToDatabaseAsync();

        // Fixed ids: every invocation upserts the same N rows, so the table does not grow across
        // iterations and the first-use table ensure is paid here rather than in the measurement.
        _docs = new BenchDoc[Documents];
        for (var i = 0; i < Documents; i++)
        {
            _docs[i] = new BenchDoc
            {
                Id = Guid.NewGuid(),
                Name = $"doc-{i}",
                Number = i,
                Timestamp = DateTimeOffset.UtcNow
            };
        }

        await using var warm = _store.LightweightSession();
        warm.Store(_docs[0]);
        await warm.SaveChangesAsync();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    /// <summary>N documents through one SaveChangesAsync — wall clock and allocations per commit.</summary>
    [Benchmark]
    public async Task SaveDocuments()
    {
        await using var session = _store.LightweightSession();
        session.Store(_docs);
        await session.SaveChangesAsync();
    }
}

[Config(typeof(MicroBenchmarkConfig))]
public class EventAppendBenchmarks
{
    private TemporaryDatabase _database = null!;
    private DocumentStore _store = null!;
    private Guid _streamId;

    [Params(1, 10)]
    public int EventsPerCommit { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _database = TemporaryDatabase.Create("bdn-append");
        _store = Scenarios.Harness.BuildStore(_database);
        await _store.ApplyAllConfiguredChangesToDatabaseAsync();

        _streamId = Guid.NewGuid();
        await using var seed = _store.LightweightSession();
        seed.Events.StartStream<BenchTally>(_streamId, new BenchCheckIn(0));
        await seed.SaveChangesAsync();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    /// <summary>Append E events to an existing stream in one commit.</summary>
    [Benchmark]
    public async Task AppendToExistingStream()
    {
        await using var session = _store.LightweightSession();

        var events = new object[EventsPerCommit];
        for (var i = 0; i < EventsPerCommit; i++)
        {
            events[i] = i % 2 == 0 ? new BenchCheckIn(i) : new BenchCheckOut(i);
        }

        session.Events.Append(_streamId, events);
        await session.SaveChangesAsync();
    }

    /// <summary>Start a brand-new stream with E events in one commit.</summary>
    [Benchmark]
    public async Task StartNewStream()
    {
        await using var session = _store.LightweightSession();

        var events = new object[EventsPerCommit];
        for (var i = 0; i < EventsPerCommit; i++)
        {
            events[i] = i % 2 == 0 ? new BenchCheckIn(i) : new BenchCheckOut(i);
        }

        session.Events.StartStream<BenchTally>(Guid.NewGuid(), events);
        await session.SaveChangesAsync();
    }
}
