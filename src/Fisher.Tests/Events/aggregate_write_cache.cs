using System.Diagnostics.CodeAnalysis;
using Fisher.Linq;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Fetching;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Events;

public record TabOpened(string Owner);

public record TabCharged(decimal Amount);

/// <summary>
///     Deliberately mutable and snapshotted <c>Inline</c> — the two properties the write-back's
///     correctness rests on.
/// </summary>
public partial class BarTab
{
    public Guid Id { get; set; }
    public string Owner { get; set; } = "";
    public decimal Balance { get; set; }

    public static BarTab Create(TabOpened e) => new() { Owner = e.Owner };

    public void Apply(TabCharged e) => Balance += e.Amount;
}

/// <summary>
///     Identical, and never enrolled in the cache.
/// </summary>
public partial class SideTab
{
    public Guid Id { get; set; }
    public string Owner { get; set; } = "";
    public decimal Balance { get; set; }

    public static SideTab Create(TabOpened e) => new() { Owner = e.Owner };

    public void Apply(TabCharged e) => Balance += e.Amount;
}

/// <summary>
///     fisher#97 / jasperfx#674 — the second-level <c>FetchForWriting</c> snapshot cache.
/// </summary>
/// <remarks>
///     <para>
///         <c>AggregateWriteCacheCompliance</c> is the definition, and it covers the semantics: a hit is
///         indistinguishable from a miss, a baseline ahead of the stream heals, an evicted entry falls
///         back, and a cached baseline can never suppress a concurrency failure. What it cannot see is
///         what Fisher's implementation of those semantics <em>rests on</em>, which is what this file
///         pins.
///     </para>
///     <para>
///         Two things in particular. <b>The entry is written back after the unit of work with the
///         version read before it</b>, and that is only honest while nothing applies this session's
///         events to the instance that was handed out — which is a fact about Fisher's inline
///         projection rather than about the cache. And <b>what a hit removes here is the fold of the
///         whole stream</b>, not a snapshot load, because Fisher's <c>FetchForWriting</c> folds on every
///         call; that is what makes the feature worth more here than the measurements behind
///         jasperfx#674 suggest.
///     </para>
/// </remarks>
public class aggregate_write_cache : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("aggregate-write-cache");
    private readonly CountingAggregateWriteCache _cache = new();
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            options.Projections.Snapshot<BarTab>(SnapshotLifecycle.Inline);
            options.Projections.Snapshot<SideTab>(SnapshotLifecycle.Inline);

            options.Events.AggregateWriteCaching.Cache = _cache;
            options.Events.CacheAggregatesForWriting<BarTab>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<Guid> anOpenTabAsync()
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Events.StartStream<BarTab>(streamId, new TabOpened("Hilda"), new TabCharged(100));
        await session.SaveChangesAsync(Token);

        return streamId;
    }

    /// <remarks>
    ///     <b>The premise the write-back rests on, and it is a fact about Fisher rather than a choice.</b>
    ///     Marten's inline projection mutates the very instance <c>FetchForWriting</c> handed out, which
    ///     is why Marten has to defer its write-back and take the version off the committed
    ///     <c>StreamAction</c>. Fisher's inline projection loads the snapshot document and builds its own
    ///     instance, so the fetched aggregate is untouched by the commit — which is what makes storing it
    ///     against the version read <em>before</em> the append the honest label rather than an
    ///     off-by-one. If this ever stops being true, the write-back is wrong and the next fetch folds
    ///     this session's events onto an aggregate that already has them.
    /// </remarks>
    [Fact]
    public async Task the_inline_projection_leaves_the_fetched_aggregate_alone()
    {
        var streamId = await anOpenTabAsync();

        await using var session = _store.LightweightSession();
        var stream = await session.Events.FetchForWriting<BarTab>(streamId, Token);
        stream.Aggregate.ShouldNotBeNull().Balance.ShouldBe(100);

        stream.AppendOne(new TabCharged(10));
        await session.SaveChangesAsync(Token);

        // The committed snapshot has the charge; the instance the fetch handed back does not.
        stream.Aggregate.Balance.ShouldBe(100);

        await using var reader = _store.QuerySession();
        (await reader.LoadAsync<BarTab>(streamId, Token)).ShouldNotBeNull().Balance.ShouldBe(110);
    }

    /// <remarks>
    ///     Nothing is stored at fetch time — see <c>RecordAggregateCacheWriteBack</c>. An entry written
    ///     while the caller still holds the instance can be claimed by another session, which folds its
    ///     delta onto the object the first caller is still reading.
    /// </remarks>
    [Fact]
    public async Task a_fetch_that_never_commits_leaves_nothing_behind()
    {
        var streamId = await anOpenTabAsync();

        await using (var session = _store.LightweightSession())
        {
            await session.Events.FetchForWriting<BarTab>(streamId, Token);
        }

        _cache.Stored.ShouldBeEmpty();
    }

    [Fact]
    public async Task the_entry_is_written_back_once_the_unit_of_work_is_written()
    {
        var streamId = await anOpenTabAsync();

        await using var session = _store.LightweightSession();
        var stream = await session.Events.FetchForWriting<BarTab>(streamId, Token);
        stream.AppendOne(new TabCharged(10));
        await session.SaveChangesAsync(Token);

        var (aggregate, version) = _cache.Stored.ShouldHaveSingleItem().Value;

        // The version read *before* this unit of work appended anything, and the aggregate as of it.
        // Behind the stream by exactly what was appended, which is the delta the next fetch folds.
        version.ShouldBe(2);
        aggregate.ShouldBeOfType<BarTab>().Balance.ShouldBe(100);
    }

    /// <remarks>
    ///     A cached baseline saves the fold of everything below it, which on Fisher is the whole point:
    ///     <c>FetchForWriting</c> folds the stream on every call, so a hit is the difference between
    ///     reading one event and reading the stream. Counted through the store's own query surface rather
    ///     than by timing anything.
    /// </remarks>
    [Fact]
    public async Task a_hit_reads_only_the_events_after_the_baseline()
    {
        var streamId = await anOpenTabAsync();

        // Warm: fetch, append, commit. The entry lands at version 2 with a balance of 100.
        await using (var warming = _store.LightweightSession())
        {
            var warm = await warming.Events.FetchForWriting<BarTab>(streamId, Token);
            warm.AppendOne(new TabCharged(10));
            await warming.SaveChangesAsync(Token);
        }

        _cache.Takes = 0;

        await using var session = _store.LightweightSession();
        var stream = await session.Events.FetchForWriting<BarTab>(streamId, Token);

        _cache.Hits.ShouldBe(1);
        _cache.Takes.ShouldBe(1);

        // Folded onto the baseline rather than from nothing, and the answer is the same either way —
        // which is the entire subject of the feature.
        stream.Aggregate.ShouldNotBeNull().Balance.ShouldBe(110);
        stream.Aggregate.Owner.ShouldBe("Hilda");
        stream.StartingVersion.ShouldBe(3);
    }

    /// <remarks>
    ///     Caching is per aggregate type. A store that turned it on globally the moment one type opted in
    ///     would pass every behavioural fact, since an uncached fetch is correct by construction — so
    ///     this asserts on the cache rather than on the aggregate.
    /// </remarks>
    [Fact]
    public async Task a_type_that_did_not_opt_in_never_reaches_the_cache()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream<SideTab>(streamId, new TabOpened("Hilda"), new TabCharged(100));
            await session.SaveChangesAsync(Token);
        }

        await using (var session = _store.LightweightSession())
        {
            var stream = await session.Events.FetchForWriting<SideTab>(streamId, Token);
            stream.AppendOne(new TabCharged(10));
            await session.SaveChangesAsync(Token);
        }

        _cache.Takes.ShouldBe(0);
        _cache.Stored.ShouldBeEmpty();
    }

    /// <remarks>
    ///     The delta fold is what a hit turns the read into, so an aggregate whose fold deletes it must
    ///     leave no entry — otherwise the next fetch resurrects it from a baseline. Nothing is stored
    ///     because there is nothing to store; <c>TryTake</c> has already removed whatever was there.
    /// </remarks>
    [Fact]
    public async Task a_new_stream_stores_nothing()
    {
        await using var session = _store.LightweightSession();
        var stream = await session.Events.FetchForWriting<BarTab>(Guid.NewGuid(), Token);

        stream.Aggregate.ShouldBeNull();
        stream.AppendOne(new TabOpened("Hilda"));
        await session.SaveChangesAsync(Token);

        _cache.Stored.ShouldBeEmpty();
    }

    /// <remarks>
    ///     The key has to carry the tenant, or under conjoined tenancy two tenants' streams — which share
    ///     an id space — would share an entry, and one tenant would read the other's aggregate. Checked
    ///     on the keys rather than through a read, because a read would have to be wrong twice to fail.
    /// </remarks>
    [Fact]
    public async Task the_key_separates_two_tenants_sharing_a_stream_id()
    {
        using var tenanted = TemporaryDatabase.Create("tenanted-aggregate-cache");
        var cache = new CountingAggregateWriteCache();

        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = tenanted.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Projections.Snapshot<BarTab>(SnapshotLifecycle.Inline);
            options.Events.AggregateWriteCaching.Cache = cache;
            options.Events.CacheAggregatesForWriting<BarTab>();
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var streamId = Guid.NewGuid();

        foreach (var tenant in new[] { "north", "south" })
        {
            await using var opening = store.LightweightSession(tenant);
            opening.Events.StartStream<BarTab>(streamId, new TabOpened(tenant), new TabCharged(100));
            await opening.SaveChangesAsync(Token);

            await using var session = store.LightweightSession(tenant);
            var stream = await session.Events.FetchForWriting<BarTab>(streamId, Token);
            stream.AppendOne(new TabCharged(10));
            await session.SaveChangesAsync(Token);
        }

        cache.Stored.Keys.Select(x => x.TenantId).OrderBy(x => x).ShouldBe(["north", "south"]);
        cache.Stored.Keys.Select(x => x.Id).Distinct().ShouldHaveSingleItem().ShouldBe(streamId);
    }

    /// <remarks>
    ///     SQLite has no schemas, so two logical stores in one file are separated by the table prefix —
    ///     which means the shared key's database identifier has to carry it, or the two collide when they
    ///     are handed the same cache. <see cref="AggregateWriteCacheOptions.Cache" /> names that
    ///     collision as the one its key cannot close; Fisher closes it.
    /// </remarks>
    [Fact]
    public async Task two_logical_stores_in_one_file_do_not_share_an_entry()
    {
        var cache = new CountingAggregateWriteCache();
        var streamId = Guid.NewGuid();

        foreach (var schema in new[] { "first", "second" })
        {
            await using var store = DocumentStore.For(options =>
            {
                options.ConnectionString = _database.ConnectionString;
                options.AutoCreateSchemaObjects = AutoCreate.All;
                options.DatabaseSchemaName = schema;
                options.Projections.Snapshot<BarTab>(SnapshotLifecycle.Inline);
                options.Events.AggregateWriteCaching.Cache = cache;
                options.Events.CacheAggregatesForWriting<BarTab>();
            });

            await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

            await using var opening = store.LightweightSession();
            opening.Events.StartStream<BarTab>(streamId, new TabOpened(schema), new TabCharged(100));
            await opening.SaveChangesAsync(Token);

            await using var session = store.LightweightSession();
            var stream = await session.Events.FetchForWriting<BarTab>(streamId, Token);
            stream.AppendOne(new TabCharged(10));
            await session.SaveChangesAsync(Token);
        }

        cache.Stored.Keys.Select(x => x.DatabaseIdentifier).Distinct().Count().ShouldBe(2);
    }
}

/// <summary>
///     A cache that answers correctly and counts what it was asked, so a test can assert that Fisher
///     consulted it rather than that an uncached fetch happened to be right.
/// </summary>
/// <remarks>
///     Take-on-read, exactly as the contract requires: an entry is removed as it is claimed. The
///     compliance suite ships its own recorder of the same shape; this one exists so Fisher's tests can
///     see the stored version and the keys, which that one does not expose.
/// </remarks>
internal class CountingAggregateWriteCache : IAggregateWriteCache
{
    private readonly Lock _lock = new();

    public Dictionary<AggregateCacheKey, (object Aggregate, long Version)> Stored { get; } = new();

    public int Takes { get; set; }

    public int Hits { get; private set; }

    public bool TryTake(AggregateCacheKey key, [NotNullWhen(true)] out object? aggregate, out long version)
    {
        lock (_lock)
        {
            Takes++;

            if (Stored.Remove(key, out var entry))
            {
                Hits++;
                aggregate = entry.Aggregate;
                version = entry.Version;
                return true;
            }

            aggregate = null;
            version = 0;
            return false;
        }
    }

    public void Store(AggregateCacheKey key, object aggregate, long version)
    {
        lock (_lock)
        {
            Stored[key] = (aggregate, version);
        }
    }

    public void Evict(AggregateCacheKey key)
    {
        lock (_lock)
        {
            Stored.Remove(key);
        }
    }
}
