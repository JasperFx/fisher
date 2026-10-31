using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Fetching;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Events;

public record VoteCast;

public partial class VoteTally
{
    public Guid Id { get; set; }
    public int Votes { get; set; }

    public static VoteTally Create(VoteCast e) => new() { Votes = 1 };

    public void Apply(VoteCast e) => Votes++;
}

public class VoteReceipt
{
    public Guid Id { get; set; }
}

/// <summary>
///     Commits at the exact point between <c>FetchForWriting</c>'s version read and its fold, which is the
///     first thing that happens after the read, so the race becomes deterministic.
/// </summary>
internal class InterleavingWriteCache : IAggregateWriteCache
{
    private readonly Lock _lock = new();
    private readonly Dictionary<AggregateCacheKey, (object Aggregate, long Version)> _stored = new();

    public Func<Task>? BeforeNextTake { get; set; }

    public bool TryTake(AggregateCacheKey key, [NotNullWhen(true)] out object? aggregate, out long version)
    {
        if (BeforeNextTake is { } interleave)
        {
            BeforeNextTake = null;
            Task.Run(interleave).GetAwaiter().GetResult();
        }

        lock (_lock)
        {
            if (_stored.Remove(key, out var entry))
            {
                (aggregate, version) = entry;
                return true;
            }
        }

        aggregate = null;
        version = 0;
        return false;
    }

    public void Store(AggregateCacheKey key, object aggregate, long version)
    {
        lock (_lock)
        {
            _stored[key] = (aggregate, version);
        }
    }

    public void Evict(AggregateCacheKey key)
    {
        lock (_lock)
        {
            _stored.Remove(key);
        }
    }
}

/// <summary>
///     fisher#486, the marten#5604 class. <c>FetchForWriting</c> is two reads, so an append can commit
///     between them. The handler here leaves the stream alone and commits other work, because an append
///     would fail the version guard before the write-back ever ran.
/// </summary>
public class aggregate_write_cache_interleaving : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("cache-interleaving");
    private readonly InterleavingWriteCache _cache = new();
    private readonly Guid _streamId = Guid.NewGuid();
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<VoteReceipt>();
            options.Events.AddEventType<VoteCast>();
            options.Events.CacheAggregatesForWriting<VoteTally>();
            options.Events.AggregateWriteCaching.Cache = _cache;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        session.Events.StartStream<VoteTally>(_streamId, new VoteCast());
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task AppendAsync()
    {
        await using var session = _store.LightweightSession();
        session.Events.Append(_streamId, new VoteCast());
        await session.SaveChangesAsync();
    }

    /// <summary>A fetch whose unit of work commits, so the write-back runs.</summary>
    private async Task<IEventStream<VoteTally>> FetchAndCommitOtherWorkAsync()
    {
        await using var session = _store.LightweightSession();

        var stream = await session.Events.FetchForWriting<VoteTally>(_streamId, Token);

        session.Store(new VoteReceipt { Id = Guid.NewGuid() });
        await session.SaveChangesAsync(Token);

        return stream;
    }

    private async Task<int> VotesOnANewFetchAsync()
    {
        await using var session = _store.LightweightSession();
        return (await session.Events.FetchForWriting<VoteTally>(_streamId, Token)).Aggregate!.Votes;
    }

    [Fact]
    public async Task a_commit_between_the_reads_on_a_cache_miss_is_not_folded_twice()
    {
        _cache.BeforeNextTake = AppendAsync;

        await FetchAndCommitOtherWorkAsync();

        (await VotesOnANewFetchAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task a_commit_between_the_reads_on_a_cache_hit_is_not_folded_twice()
    {
        await FetchAndCommitOtherWorkAsync();

        _cache.BeforeNextTake = AppendAsync;

        await FetchAndCommitOtherWorkAsync();

        (await VotesOnANewFetchAsync()).ShouldBe(2);
    }

    /// <summary>The aggregate agrees with the version it is labelled with, which is what the fix provides.</summary>
    [Fact]
    public async Task the_aggregate_handed_back_is_at_the_version_read()
    {
        _cache.BeforeNextTake = AppendAsync;

        await using var session = _store.LightweightSession();

        var stream = await session.Events.FetchForWriting<VoteTally>(_streamId, Token);

        stream.StartingVersion.ShouldBe(1);
        stream.Aggregate!.Votes.ShouldBe(1);
    }
}
