using System.Collections.Concurrent;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Projections;

public record BusyTallied;

/// <summary>Throws a "database is locked" for a stream's event as many times as it's told to, then applies it.</summary>
public class BusyTally
{
    public Guid Id { get; set; }
    public int Applied { get; set; }

    public void Apply(IEvent<BusyTallied> e)
    {
        BusyFaults.Visit(e.StreamId);
        Applied++;
    }
}

public static class BusyFaults
{
    private static readonly ConcurrentDictionary<Guid, Fault> _faults = new();

    public static void Fail(Guid stream, int times) => _faults[stream] = new Fault(times);

    public static int Attempts(Guid stream) => _faults.TryGetValue(stream, out var fault) ? fault.Attempts : 0;

    public static void Forget(Guid stream) => _faults.TryRemove(stream, out _);

    public static void Visit(Guid stream)
    {
        if (!_faults.TryGetValue(stream, out var fault)) return;

        var attempt = Interlocked.Increment(ref fault.Attempts);

        // SQLITE_BUSY, what a second writer gets while another connection holds the write lock
        if (attempt <= fault.Times) throw new SqliteException("database is locked", 5);
    }

    private sealed class Fault(int times)
    {
        public readonly int Times = times;
        public int Attempts;
    }
}

/// <summary>
///     jasperfx#1256: the daemon builds a projection batch that failed transiently again through the store's
///     <c>ResiliencePipeline</c>, which on Fisher is <c>StoreOptions</c>' own pipeline. Its default retry handles
///     <c>SQLITE_BUSY</c> five times, so a busy database is built through, and one that stays busy pauses the shard
///     as transient rather than parking the stream.
/// </summary>
public class transient_batch_build_failures : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("transient-batch-builds");
    private readonly List<Guid> _streams = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        foreach (var stream in _streams) BusyFaults.Forget(stream);
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private DocumentStore store() => DocumentStore.For(options =>
    {
        options.ConnectionString = _database.ConnectionString;
        options.AutoCreateSchemaObjects = AutoCreate.All;
        options.Projections.Snapshot<BusyTally>(SnapshotLifecycle.Async);
    });

    private async Task<Guid[]> appendAsync(IDocumentStore store, int count)
    {
        var streams = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        _streams.AddRange(streams);

        await using var session = store.LightweightSession();
        foreach (var stream in streams) session.Events.StartStream<BusyTally>(stream, new BusyTallied());
        await session.SaveChangesAsync(Token);

        return streams;
    }

    private static async Task<int> appliedAsync(IDocumentStore store, Guid stream)
    {
        await using var query = store.QuerySession();
        return (await query.LoadAsync<BusyTally>(stream, Token)).ShouldNotBeNull().Applied;
    }

    [Fact]
    public async Task a_batch_that_hits_a_busy_database_is_built_again()
    {
        await using var store = this.store();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var streams = await appendAsync(store, 3);
        BusyFaults.Fail(streams[0], 2);

        using var daemon = await store.BuildProjectionDaemonAsync();
        var paused = daemon.Tracker.WaitForShardCondition(x => x.Action == ShardAction.Paused, "a paused shard",
            DaemonWait.Timeout);

        try
        {
            await daemon.StartAllAsync();
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

            // Failed twice, and the third build committed
            BusyFaults.Attempts(streams[0]).ShouldBe(3);
            foreach (var stream in streams) (await appliedAsync(store, stream)).ShouldBe(1);

            paused.IsCompleted.ShouldBeFalse();
            (await store.Database.FetchParkedIdentitiesAsync(new ParkedIdentityQuery(), Token)).ShouldBeEmpty();
        }
        finally
        {
            await daemon.StopAllAsync();
        }
    }

    [Fact]
    public async Task a_database_that_stays_busy_past_the_pipeline_pauses_the_shard_as_transient()
    {
        await using var store = this.store();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var streams = await appendAsync(store, 2);
        BusyFaults.Fail(streams[0], int.MaxValue);

        using var daemon = await store.BuildProjectionDaemonAsync();
        var paused = daemon.Tracker.WaitForShardCondition(x => x.Action == ShardAction.Paused, "a paused shard",
            DaemonWait.Timeout);

        try
        {
            await daemon.StartAllAsync();

            var state = await paused;
            var failure = state.Failure.ShouldNotBeNull();
            failure.IsTransient.ShouldBeTrue();
            failure.RootExceptionType.ShouldBe(typeof(SqliteException).FullName);

            // The first build and the five retries of Fisher's default pipeline
            BusyFaults.Attempts(streams[0]).ShouldBe(6);

            // Not the stream's fault, so the 2.0 default of parking it doesn't apply
            (await store.Database.FetchParkedIdentitiesAsync(new ParkedIdentityQuery(), Token)).ShouldBeEmpty();
        }
        finally
        {
            await daemon.StopAllAsync();
        }
    }
}
