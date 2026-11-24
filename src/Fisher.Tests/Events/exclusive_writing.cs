using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Events;

/// <summary>
///     fisher#689: <c>FetchForExclusiveWriting</c>, <c>AppendExclusive</c> and <c>WriteExclusivelyToAggregate</c>
///     take the database file's write lock and hold it until the unit of work commits, so competing exclusive
///     writers take turns instead of all but one failing on the version guard.
/// </summary>
/// <remarks>
///     <para>
///         <b>The property with teeth is that the holders never overlap.</b> Every writer succeeding could also
///         happen by luck if they happened not to collide; counting how many are between the fetch and the
///         commit at once cannot. Before this, 1 of 5 concurrent exclusive writers committed and 4 failed with
///         <see cref="EventStreamUnexpectedMaxEventIdException" />.
///     </para>
///     <para>
///         The "still waiting" checks are safe as timing goes: a writer waiting on a lock another session holds
///         cannot finish until that session lets go, however slow the machine is.
///     </para>
/// </remarks>
public class exclusive_writing : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("exclusive");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
            o.Projections.Snapshot<Counter>(SnapshotLifecycle.Inline);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<Guid> StartCounter(string name)
    {
        var id = Guid.NewGuid();
        await using var session = _store.LightweightSession();
        session.Events.StartStream<Counter>(id, new CounterStarted(new CounterName(name)));
        await session.SaveChangesAsync(Token);
        return id;
    }

    [Fact]
    public async Task concurrent_exclusive_writers_by_stream_id_take_turns()
    {
        var id = await StartCounter($"by-id-{Guid.NewGuid():N}");

        var max = await RaceAsync(5, session => session.Events.FetchForExclusiveWriting<Counter>(id, Token));

        max.ShouldBe(1);
        await AssertIncrementedAsync(id, 5);
    }

    [Fact]
    public async Task concurrent_exclusive_writers_by_natural_key_take_turns()
    {
        var name = new CounterName($"by-key-{Guid.NewGuid():N}");
        var id = await StartCounter(name.Value);

        var max = await RaceAsync(5,
            session => session.Events.FetchForExclusiveWriting<Counter, CounterName>(name, Token));

        max.ShouldBe(1);
        await AssertIncrementedAsync(id, 5);
    }

    [Fact]
    public async Task concurrent_exclusive_aggregate_writes_take_turns()
    {
        var id = await StartCounter($"aggregate-{Guid.NewGuid():N}");

        var holders = 0;
        var max = 0;

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            await using var session = _store.LightweightSession();
            await session.Events.WriteExclusivelyToAggregate<Counter>(id, async stream =>
            {
                RecordHolder(ref holders, ref max);
                await Task.Delay(20, Token);
                stream.AppendOne(new CounterIncremented());
                Interlocked.Decrement(ref holders);
            }, Token);
        }, Token)));

        max.ShouldBe(1);
        await AssertIncrementedAsync(id, 5);
    }

    [Fact]
    public async Task a_second_exclusive_append_waits_for_the_first_to_commit()
    {
        var id = await StartCounter($"append-{Guid.NewGuid():N}");

        await using var first = _store.LightweightSession();
        await first.Events.AppendExclusive(id, Token, new CounterIncremented());

        await using var second = _store.LightweightSession();
        var waiting = second.Events.AppendExclusive(id, Token, new CounterIncremented());

        await Task.Delay(250, Token);
        waiting.IsCompleted.ShouldBeFalse();

        await first.SaveChangesAsync(Token);
        await waiting.WaitAsync(DaemonWait.Timeout, Token);
        await second.SaveChangesAsync(Token);

        await AssertIncrementedAsync(id, 2);
    }

    [Fact]
    public async Task an_ordinary_commit_waits_while_the_lock_is_held()
    {
        var id = await StartCounter($"ordinary-{Guid.NewGuid():N}");

        await using var holder = _store.LightweightSession();
        var stream = await holder.Events.FetchForExclusiveWriting<Counter>(id, Token);

        await using var other = _store.LightweightSession();
        other.Events.StartStream<Counter>(Guid.NewGuid(), new CounterStarted(new CounterName($"other-{Guid.NewGuid():N}")));
        var waiting = other.SaveChangesAsync(Token);

        await Task.Delay(250, Token);
        waiting.IsCompleted.ShouldBeFalse();

        stream.AppendOne(new CounterIncremented());
        await holder.SaveChangesAsync(Token);

        await waiting.WaitAsync(DaemonWait.Timeout, Token);
        await AssertIncrementedAsync(id, 1);
    }

    [Fact]
    public async Task readers_are_not_blocked_by_the_lock()
    {
        var id = await StartCounter($"reader-{Guid.NewGuid():N}");

        await using var holder = _store.LightweightSession();
        await holder.Events.FetchForExclusiveWriting<Counter>(id, Token);

        await using var reader = _store.QuerySession();
        var counter = await reader.Events.FetchLatest<Counter>(id, Token)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);

        counter.ShouldNotBeNull();
    }

    [Fact]
    public async Task disposing_without_saving_gives_the_lock_back()
    {
        var id = await StartCounter($"dispose-{Guid.NewGuid():N}");

        await using (var abandoned = _store.LightweightSession())
        {
            var stream = await abandoned.Events.FetchForExclusiveWriting<Counter>(id, Token);
            stream.AppendOne(new CounterIncremented());
        }

        await using var next = _store.LightweightSession();
        var fetched = await next.Events.FetchForExclusiveWriting<Counter>(id, Token)
            .WaitAsync(TimeSpan.FromSeconds(5), Token);
        fetched.AppendOne(new CounterIncremented());
        await next.SaveChangesAsync(Token);

        // The abandoned session's event was rolled back with its lock
        await AssertIncrementedAsync(id, 1);
    }

    [Fact]
    public async Task a_save_with_nothing_to_write_gives_the_lock_back()
    {
        var id = await StartCounter($"empty-{Guid.NewGuid():N}");

        await using var looked = _store.LightweightSession();
        await looked.Events.FetchForExclusiveWriting<Counter>(id, Token);
        await looked.SaveChangesAsync(Token);

        await using var next = _store.LightweightSession();
        next.Events.Append(id, new CounterIncremented());
        await next.SaveChangesAsync(Token).WaitAsync(TimeSpan.FromSeconds(5), Token);

        await AssertIncrementedAsync(id, 1);
    }

    [Fact]
    public async Task a_failed_commit_gives_the_lock_back()
    {
        var id = await StartCounter($"failed-{Guid.NewGuid():N}");

        await using (var failing = _store.LightweightSession())
        {
            await failing.Events.FetchForExclusiveWriting<Counter>(id, Token);

            // Starting a stream that already exists fails the commit inside the held transaction
            failing.Events.StartStream<Counter>(id, new CounterStarted(new CounterName($"dup-{Guid.NewGuid():N}")));

            await Should.ThrowAsync<ExistingStreamIdCollisionException>(() => failing.SaveChangesAsync(Token));
        }

        await using var next = _store.LightweightSession();
        var stream = await next.Events.FetchForExclusiveWriting<Counter>(id, Token)
            .WaitAsync(TimeSpan.FromSeconds(5), Token);
        stream.AppendOne(new CounterIncremented());
        await next.SaveChangesAsync(Token);

        await AssertIncrementedAsync(id, 1);
    }

    [Fact]
    public async Task a_writer_that_cannot_get_the_lock_in_time_gets_a_stream_locked_exception()
    {
        var id = await StartCounter($"timeout-{Guid.NewGuid():N}");

        await using var impatientStore = DocumentStore.For(o =>
        {
            o.ConnectionString =
                new SqliteConnectionStringBuilder(_database.ConnectionString) { DefaultTimeout = 1 }.ToString();
            o.AutoCreateSchemaObjects = AutoCreate.All;
            o.PragmaSettings.BusyTimeout = 250;
            o.Projections.Snapshot<Counter>(SnapshotLifecycle.Inline);
            o.ConfigurePolly(_ => { });
        });

        await using var holder = _store.LightweightSession();
        await holder.Events.FetchForExclusiveWriting<Counter>(id, Token);

        await using var impatient = impatientStore.LightweightSession();

        // Warm the connection first, so the contention is met at BEGIN IMMEDIATE and not while the
        // connection's PRAGMAs are applied
        await impatient.Events.FetchStreamStateAsync(id, Token);

        var locked = await Should.ThrowAsync<StreamLockedException>(
            () => impatient.Events.FetchForExclusiveWriting<Counter>(id, Token));

        locked.StreamIds.ShouldContain(id);
    }

    private async Task<int> RaceAsync(int writers, Func<IDocumentSession, Task<IEventStream<Counter>>> fetch)
    {
        var holders = 0;
        var max = 0;

        await Task.WhenAll(Enumerable.Range(0, writers).Select(_ => Task.Run(async () =>
        {
            await using var session = _store.LightweightSession();
            var stream = await fetch(session);

            RecordHolder(ref holders, ref max);

            await Task.Delay(20, Token);
            stream.AppendOne(new CounterIncremented());

            // Before the commit: the next writer can only get the lock once this one's commit releases it
            Interlocked.Decrement(ref holders);
            await session.SaveChangesAsync(Token);
        }, Token)));

        return max;
    }

    private static void RecordHolder(ref int holders, ref int max)
    {
        var now = Interlocked.Increment(ref holders);
        int seen;
        do
        {
            seen = Volatile.Read(ref max);
        } while (now > seen && Interlocked.CompareExchange(ref max, now, seen) != seen);
    }

    private async Task AssertIncrementedAsync(Guid id, int expected)
    {
        await using var query = _store.QuerySession();
        var counter = await query.Events.FetchLatest<Counter>(id, Token);
        counter.ShouldNotBeNull();
        counter.Count.ShouldBe(expected);

        var state = await query.Events.FetchStreamStateAsync(id, Token);
        state!.Version.ShouldBe(expected + 1);
    }

    public readonly record struct CounterName(string Value);

    public record CounterStarted(CounterName Name);

    public record CounterIncremented;

    public partial class Counter
    {
        public Guid Id { get; set; }

        [NaturalKey]
        public CounterName Name { get; set; }

        public int Count { get; set; }

        [NaturalKeySource]
        public static Counter Create(CounterStarted e) => new() { Name = e.Name };

        public void Apply(CounterIncremented _) => Count++;
    }
}
