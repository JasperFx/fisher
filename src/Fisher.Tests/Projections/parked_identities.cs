using System.Collections.Concurrent;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Projections;

public record ParkTallied;

public record ParkTallyPoisoned;

/// <summary>The poison switch for the tallies below, by stream id or key.</summary>
public static class ParkTallyPoison
{
    private static readonly ConcurrentDictionary<string, bool> _poisoned = new();

    public static void On(object stream) => _poisoned[keyOf(stream)] = true;

    public static void Off(object stream) => _poisoned.TryRemove(keyOf(stream), out _);

    public static void ThrowIfPoisoned(IEvent @event)
    {
        var key = @event.StreamId != Guid.Empty ? @event.StreamId.ToString("D") : @event.StreamKey ?? "";
        if (_poisoned.ContainsKey(key)) throw new InvalidOperationException($"Stream {key} is poisoned");
    }

    private static string keyOf(object stream) => stream is Guid id ? id.ToString("D") : stream.ToString()!;
}

public class GuidParkTally
{
    public Guid Id { get; set; }
    public List<long> Sequences { get; set; } = [];

    public void Apply(IEvent<ParkTallied> e) => Sequences.Add(e.Sequence);

    public void Apply(IEvent<ParkTallyPoisoned> e)
    {
        ParkTallyPoison.ThrowIfPoisoned(e);
        Sequences.Add(e.Sequence);
    }
}

public class StringParkTally
{
    public string Id { get; set; } = "";
    public List<long> Sequences { get; set; } = [];

    public void Apply(IEvent<ParkTallied> e) => Sequences.Add(e.Sequence);

    public void Apply(IEvent<ParkTallyPoisoned> e)
    {
        ParkTallyPoison.ThrowIfPoisoned(e);
        Sequences.Add(e.Sequence);
    }
}

public class ParkTallyLogEntry
{
    public string Id { get; set; } = "";
}

// partial, because JasperFx's source generator emits the conventional-method dispatcher into this class
public partial class ParkTallyLogProjection : Fisher.Projections.EventProjection
{
    public ParkTallyLogProjection()
    {
        Name = "ParkTallyLog";
    }

    public ParkTallyLogEntry Create(IEvent<ParkTallyPoisoned> e)
    {
        ParkTallyPoison.ThrowIfPoisoned(e);
        return new ParkTallyLogEntry { Id = e.Id.ToString() };
    }
}

/// <summary>A multi-stream projection grouped by a delegate, which only the slice index lets park.</summary>
public partial class IndexedParkTallyProjection : Fisher.Projections.MultiStreamProjection<GuidParkTally, Guid>
{
    public static readonly Guid Shared = Guid.Parse("6a9e05a4-41d1-4b3b-9a4c-6c3c4b1d2e01");

    public IndexedParkTallyProjection()
    {
        Name = "IndexedParkTally";
        Identity<ParkTallied>(_ => Shared);
        Options.UseSliceIndex();
    }

    public void Apply(IEvent<ParkTallied> e, GuidParkTally tally) => tally.Sequences.Add(e.Sequence);
}

/// <summary>
///     fisher#652 / marten#6003, Fisher's side of parking a failing projection identity. The shared
///     <c>ParkedIdentityCompliance</c> and <c>MultiStreamParkedIdentityCompliance</c> suites cover the behaviour; these
///     cover what is Fisher's own: the 2.0 default, <c>RestoreV1Defaults()</c>, the fetch APIs over a string stream
///     identity as well as a Guid one, and where parked rows and slice index rows go when the projection is rewound,
///     ejected, torn down, or its tenant's data is wiped.
/// </summary>
public class parked_identities : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("parked-identities");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private DocumentStore guidStore(Action<StoreOptions>? alter = null) => DocumentStore.For(options =>
    {
        options.ConnectionString = _database.ConnectionString;
        options.AutoCreateSchemaObjects = AutoCreate.All;
        options.Projections.Snapshot<GuidParkTally>(SnapshotLifecycle.Async);
        alter?.Invoke(options);
    });

    private DocumentStore stringStore() => DocumentStore.For(options =>
    {
        options.ConnectionString = _database.ConnectionString;
        options.AutoCreateSchemaObjects = AutoCreate.All;
        options.Events.StreamIdentity = StreamIdentity.AsString;
        options.Projections.Snapshot<StringParkTally>(SnapshotLifecycle.Async);
    });

    private static async Task appendAsync(IDocumentStore store, object stream, params object[] events)
    {
        await using var session = store.LightweightSession();
        if (stream is Guid id) session.Events.Append(id, events);
        else session.Events.Append((string)stream, events);
        await session.SaveChangesAsync(Token);
    }

    private static async Task<IReadOnlyList<ParkedIdentity>> parkedAsync(DocumentStore store)
        => await store.Database.FetchParkedIdentitiesAsync(new ParkedIdentityQuery(), Token);

    private static async Task<IReadOnlyList<long>> sequencesOfAsync(DocumentStore store, object stream)
    {
        await using var query = store.QuerySession();
        var events = stream is Guid id
            ? await query.Events.FetchStreamAsync(id, token: Token)
            : await query.Events.FetchStreamAsync((string)stream, token: Token);

        return events.Select(x => x.Sequence).ToList();
    }

    private static async Task<long> countAsync(DocumentStore store, string table)
    {
        await using var connection = await store.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"select count(*) from {table}";
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }

    [Theory]
    [InlineData("guid")]
    [InlineData("string")]
    public async Task fetching_for_writing_and_fetching_latest_see_the_right_aggregate_around_a_park(string kind)
    {
        var isGuid = kind == "guid";
        await using var store = isGuid ? guidStore() : stringStore();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        object stream = isGuid ? Guid.NewGuid() : "tally-" + Guid.NewGuid().ToString("N");
        object healthy = isGuid ? Guid.NewGuid() : "tally-" + Guid.NewGuid().ToString("N");
        await appendAsync(store, stream, new ParkTallied(), new ParkTallied());
        await appendAsync(store, healthy, new ParkTallied());

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();
        await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

        async Task<(List<long>? Writing, List<long>? Latest, List<long>? Many)> fetchAsync()
        {
            await using var session = store.LightweightSession();
            if (isGuid)
            {
                var id = (Guid)stream;
                var writing = await session.Events.FetchForWriting<GuidParkTally>(id, Token);
                var latest = await session.Events.FetchLatest<GuidParkTally>(id, Token);
                var many = await session.Events.FetchManyForWriting<GuidParkTally>([id], Token);
                return (writing.Aggregate?.Sequences, latest?.Sequences, many.Single().Aggregate?.Sequences);
            }
            else
            {
                var key = (string)stream;
                var writing = await session.Events.FetchForWriting<StringParkTally>(key, Token);
                var latest = await session.Events.FetchLatest<StringParkTally>(key, Token);
                var many = await session.Events.FetchManyForWriting<StringParkTally>([key], Token);
                return (writing.Aggregate?.Sequences, latest?.Sequences, many.Single().Aggregate?.Sequences);
            }
        }

        async Task<List<long>?> storedAsync()
        {
            await using var query = store.QuerySession();
            return isGuid
                ? (await query.LoadAsync<GuidParkTally>((Guid)stream, Token))?.Sequences
                : (await query.LoadAsync<StringParkTally>((string)stream, Token))?.Sequences;
        }

        try
        {
            // Before the park
            var good = await sequencesOfAsync(store, stream);
            var before = await fetchAsync();
            before.Writing.ShouldBe(good);
            before.Latest.ShouldBe(good);
            before.Many.ShouldBe(good);

            // The 2.0 default parks, with nothing configured
            ParkTallyPoison.On(stream);
            await appendAsync(store, stream, new ParkTallyPoisoned(), new ParkTallied());
            await appendAsync(store, healthy, new ParkTallied());
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

            var row = (await parkedAsync(store)).ShouldHaveSingleItem();
            row.IdentityText.ShouldBe(isGuid ? ((Guid)stream).ToString("D") : (string)stream);
            row.IdentityType.ShouldBe(isGuid ? "guid" : "string");
            row.Identity.ShouldBe(stream);
            row.Category.ShouldBe(ShardFailureCategory.ApplyEvent);

            // The parked stream's document is frozen and the healthy one went on
            (await storedAsync()).ShouldBe(good);

            // A fetch folds the poisoned event and fails the way the projection did
            await Should.ThrowAsync<Exception>(fetchAsync);

            // Fixed: a fetch folds everything while the stored document is still frozen
            ParkTallyPoison.Off(stream);
            var expected = await sequencesOfAsync(store, stream);
            var during = await fetchAsync();
            during.Writing.ShouldBe(expected);
            during.Latest.ShouldBe(expected);
            during.Many.ShouldBe(expected);
            (await storedAsync()).ShouldBe(good);

            // After the replay the stored document agrees, and the row is gone
            var name = store.Advanced.AllAsyncProjectionShardNames().Single().Name;
            (await daemon.ReplayParkedIdentityAsync(name, null, stream, Token)).Outcome
                .ShouldBe(ParkedReplayOutcome.Replayed);

            var after = await fetchAsync();
            after.Writing.ShouldBe(expected);
            after.Latest.ShouldBe(expected);
            after.Many.ShouldBe(expected);
            (await storedAsync()).ShouldBe(expected);
            (await parkedAsync(store)).ShouldBeEmpty();
        }
        finally
        {
            ParkTallyPoison.Off(stream);
            await daemon.StopAllAsync();
        }
    }

    [Fact]
    public async Task restoring_the_v1_defaults_skips_the_event_again()
    {
        await using var store = guidStore(options => options.RestoreV1Defaults());
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        store.Options.Projections.Errors.ApplyErrors.ShouldBe(ProjectionErrorAction.Skip);
        store.Options.Projections.Errors.SerializationErrors.ShouldBe(ProjectionErrorAction.Skip);

        var stream = Guid.NewGuid();
        ParkTallyPoison.On(stream);

        using var daemon = await store.BuildProjectionDaemonAsync();
        try
        {
            await appendAsync(store, stream, new ParkTallied(), new ParkTallyPoisoned(), new ParkTallied());
            await daemon.StartAllAsync();
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

            (await parkedAsync(store)).ShouldBeEmpty();

            var shard = store.Advanced.AllAsyncProjectionShardNames().Single();
            (await store.Database.CountDeadLetterEventsAsync(shard, Token)).ShouldBe(1);

            // Skipped: the events either side of the poisoned one were applied
            var sequences = await sequencesOfAsync(store, stream);
            await using var query = store.QuerySession();
            (await query.LoadAsync<GuidParkTally>(stream, Token))!.Sequences.ShouldBe([sequences[0], sequences[2]]);
        }
        finally
        {
            ParkTallyPoison.Off(stream);
            await daemon.StopAllAsync();
        }
    }

    [Fact]
    public async Task the_2_0_default_parks_and_describes_it()
    {
        await using var store = guidStore();

        store.Options.Projections.Errors.ApplyErrors.ShouldBe(ProjectionErrorAction.Park);
        store.Options.Projections.Errors.SerializationErrors.ShouldBe(ProjectionErrorAction.Park);

        var usage = await ((IEventStore)store).TryCreateUsage(Token);
        usage.ShouldNotBeNull();
        usage.ProjectionErrors.ShouldNotBeNull();
        usage.ProjectionErrors.ApplyErrors.ShouldBe("Park");
        usage.ProjectionErrors.MaxParkedIdentitiesPerShard.ShouldBe(1000);
    }

    [Fact]
    public async Task an_event_projection_pauses_under_the_2_0_default()
    {
        await using var store = guidStore(options =>
            options.Projections.Add(new ParkTallyLogProjection(), ProjectionLifecycle.Async));
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var stream = Guid.NewGuid();
        ParkTallyPoison.On(stream);

        using var daemon = await store.BuildProjectionDaemonAsync();
        try
        {
            var log = store.Advanced.AllAsyncProjectionShardNames().Single(x => x.Name == "ParkTallyLog");
            var paused = daemon.Tracker.WaitForShardCondition(
                state => state.ShardName == log.Identity && state.Action == ShardAction.Paused,
                $"{log.Identity} is paused", DaemonWait.Timeout);

            await appendAsync(store, stream, new ParkTallied(), new ParkTallyPoisoned());
            await daemon.StartAllAsync();

            await paused;

            // The tally parks its stream; the event projection can't park, so it paused and parked nothing
            using var timeout = new CancellationTokenSource(DaemonWait.Timeout);
            IReadOnlyList<ParkedIdentity> parked;
            while ((parked = await parkedAsync(store)).Count == 0)
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Delay(50, Token);
            }

            parked.ShouldHaveSingleItem().ProjectionName.ShouldNotBe("ParkTallyLog");
        }
        finally
        {
            ParkTallyPoison.Off(stream);
            await daemon.StopAllAsync();
        }
    }

    [Fact]
    public async Task a_rewind_below_the_failure_and_an_eject_take_the_parked_row()
    {
        await using var store = guidStore();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var stream = Guid.NewGuid();
        ParkTallyPoison.On(stream);

        using var daemon = await store.BuildProjectionDaemonAsync();
        try
        {
            await appendAsync(store, stream, new ParkTallied(), new ParkTallyPoisoned());
            await daemon.StartAllAsync();
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

            var row = (await parkedAsync(store)).ShouldHaveSingleItem();
            var shard = store.Advanced.AllAsyncProjectionShardNames().Single();
            await daemon.StopAllAsync();

            // A rewind to the failing event's own position keeps the row: that identity is still frozen
            var eventStore = (IEventStore<IDocumentSession, IQuerySession>)store;
            await eventStore.RewindAgentProgressAsync(store.Database, shard.Identity, Token, row.FirstFailingSequence);
            (await parkedAsync(store)).ShouldHaveSingleItem();

            // Below it, the row goes: the shard parks it again when it gets there
            await eventStore.RewindAgentProgressAsync(store.Database, shard.Identity, Token,
                row.FirstFailingSequence - 1);
            (await parkedAsync(store)).ShouldBeEmpty();

            await daemon.StartAllAsync();
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);
            (await parkedAsync(store)).ShouldHaveSingleItem();
            await daemon.StopAllAsync();

            // Ejecting the shard takes it too
            await store.Database.DeleteProjectionProgressByShardNameAsync(shard.Identity, Token);
            (await parkedAsync(store)).ShouldBeEmpty();
        }
        finally
        {
            ParkTallyPoison.Off(stream);
            await daemon.StopAllAsync();
        }
    }

    [Fact]
    public async Task the_extended_progression_row_carries_the_parked_count()
    {
        await using var store = guidStore(options => options.Events.EnableExtendedProgressionTracking = true);
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var stream = Guid.NewGuid();
        ParkTallyPoison.On(stream);

        using var daemon = await store.BuildProjectionDaemonAsync();
        try
        {
            await appendAsync(store, Guid.NewGuid(), new ParkTallied());
            await appendAsync(store, stream, new ParkTallied(), new ParkTallyPoisoned());
            await daemon.StartAllAsync();
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

            var shard = store.Advanced.AllAsyncProjectionShardNames().Single();

            using var timeout = new CancellationTokenSource(DaemonWait.Timeout);
            long? count = null;
            while (count != 1)
            {
                timeout.Token.ThrowIfCancellationRequested();

                await using var connection = await store.Database.OpenConnectionAsync(Token);
                await using var command = connection.CreateCommand();
                command.CommandText = "select parked_count from fi_event_progression where name = @name";
                command.Parameters.AddWithValue("@name", shard.Identity);
                var raw = await command.ExecuteScalarAsync(Token);
                count = raw is null or DBNull ? null : (long)raw;

                if (count != 1) await Task.Delay(50, Token);
            }

            (await store.Database.FetchParkedCountsAsync(null, Token)).ShouldHaveSingleItem().Count.ShouldBe(1);
        }
        finally
        {
            ParkTallyPoison.Off(stream);
            await daemon.StopAllAsync();
        }
    }

    private static async Task waitForParkedCountAsync(DocumentStore store, ShardName shard, int expected)
    {
        using var timeout = new CancellationTokenSource(DaemonWait.Timeout);
        while (true)
        {
            var state = (await store.Advanced.AllProjectionProgress(token: Token))
                .SingleOrDefault(x => x.ShardName == shard.Identity);
            if (state?.ParkedCount == expected) return;

            if (timeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"{shard.Identity} never showed {expected} parked in ShardState; it last read {state?.ParkedCount?.ToString() ?? "null"}");
            }

            await Task.Delay(50, Token);
        }
    }

    private static async Task setParkedCountColumnAsync(DocumentStore store, ShardName shard, int? count)
    {
        await using var connection = await store.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "update fi_event_progression set parked_count = @count where name = @name";
        command.Parameters.AddWithValue("@count", (object?)count ?? DBNull.Value);
        command.Parameters.AddWithValue("@name", shard.Identity);
        (await command.ExecuteNonQueryAsync(Token)).ShouldBe(1);
    }

    private static async Task<int?> parkedCountInShardStateAsync(DocumentStore store, ShardName shard)
        => (await store.Advanced.AllProjectionProgress(token: Token))
            .Single(x => x.ShardName == shard.Identity).ParkedCount;

    /// <summary>
    ///     fisher#654: the count is set by the projection batch, in the transaction that parks or unparks, so it is
    ///     right with periodic heartbeats off, which is when JasperFx's writer dropped the Parked and Unparked
    ///     publications before 3.11.0 (jasperfx#1255, fixed upstream).
    /// </summary>
    [Fact]
    public async Task all_projection_progress_reads_the_parked_count_back_into_shard_state()
    {
        await using var store = guidStore(options =>
        {
            options.Events.EnableExtendedProgressionTracking = true;
            options.Projections.ExtendedProgressionHeartbeatInterval = TimeSpan.Zero;
        });
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var replayed = Guid.NewGuid();
        var discarded = Guid.NewGuid();
        ParkTallyPoison.On(replayed);
        ParkTallyPoison.On(discarded);

        using var daemon = await store.BuildProjectionDaemonAsync();
        try
        {
            await appendAsync(store, Guid.NewGuid(), new ParkTallied());
            await appendAsync(store, replayed, new ParkTallied(), new ParkTallyPoisoned());
            await appendAsync(store, discarded, new ParkTallied(), new ParkTallyPoisoned());
            await daemon.StartAllAsync();
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

            var shard = store.Advanced.AllAsyncProjectionShardNames().Single();
            (await parkedAsync(store)).Count.ShouldBe(2);
            await waitForParkedCountAsync(store, shard, 2);

            // A replay that succeeds takes one off the count
            ParkTallyPoison.Off(replayed);
            (await daemon.ReplayParkedIdentityAsync(shard.Name, null, replayed, Token)).Outcome
                .ShouldBe(ParkedReplayOutcome.Replayed);
            await waitForParkedCountAsync(store, shard, 1);

            // A discard takes the other, and the count is 0 rather than null: the shard still parks
            (await daemon.DiscardParkedIdentityAsync(shard.Name, null, discarded, Token)).Outcome
                .ShouldBe(ParkedReplayOutcome.Discarded);
            await waitForParkedCountAsync(store, shard, 0);
            (await parkedAsync(store)).ShouldBeEmpty();

            // The daemon stopping publishes its own count; it lands on the batch's, not over it
            await daemon.StopAllAsync();
            (await parkedCountInShardStateAsync(store, shard)).ShouldBe(0);

            // A stale state from the progression writer never overwrites a count the batch wrote...
            var sequence = await store.Database.ProjectionProgressFor(shard, Token);
            await store.Database.WriteExtendedProgressionAsync(
                new ShardState(shard.Identity, sequence) { AgentStatus = "Running", ParkedCount = 2 }, Token);
            (await parkedCountInShardStateAsync(store, shard)).ShouldBe(0);

            // ...and only fills the column in while nothing has set it
            await setParkedCountColumnAsync(store, shard, null);
            await store.Database.WriteExtendedProgressionAsync(
                new ShardState(shard.Identity, sequence) { AgentStatus = "Running", ParkedCount = 0 }, Token);
            (await parkedCountInShardStateAsync(store, shard)).ShouldBe(0);
        }
        finally
        {
            ParkTallyPoison.Off(replayed);
            ParkTallyPoison.Off(discarded);
            await daemon.StopAllAsync();
        }
    }

    [Fact]
    public async Task a_rewind_and_a_tenant_wipe_take_the_parked_count_with_the_rows()
    {
        await using var store = guidStore(options =>
        {
            options.Events.EnableExtendedProgressionTracking = true;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Schema.For<GuidParkTally>().MultiTenanted();
        });
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var red = Guid.NewGuid();
        var blue = Guid.NewGuid();
        ParkTallyPoison.On(red);
        ParkTallyPoison.On(blue);

        async Task appendForAsync(string tenant, Guid stream)
        {
            await using var session = store.LightweightSession(tenant);
            session.Events.Append(stream, new ParkTallied(), new ParkTallyPoisoned());
            await session.SaveChangesAsync(Token);
        }

        using var daemon = await store.BuildProjectionDaemonAsync();
        try
        {
            await appendForAsync("red", red);
            await appendForAsync("blue", blue);
            await daemon.StartAllAsync();
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

            var shard = store.Advanced.AllAsyncProjectionShardNames().Single();
            await waitForParkedCountAsync(store, shard, 2);
            await daemon.StopAllAsync();

            // Wiping one tenant's data takes its parked row, and the count with it
            await store.Advanced.DeleteAllTenantDataAsync("red", Token);
            (await parkedAsync(store)).ShouldHaveSingleItem().TenantId.ShouldBe("blue");
            (await parkedCountInShardStateAsync(store, shard)).ShouldBe(1);

            // A rewind below the failure takes the other
            var row = (await parkedAsync(store)).Single();
            await ((IEventStore<IDocumentSession, IQuerySession>)store).RewindAgentProgressAsync(store.Database,
                shard.Identity, Token, row.FirstFailingSequence - 1);
            (await parkedAsync(store)).ShouldBeEmpty();
            (await parkedCountInShardStateAsync(store, shard)).ShouldBe(0);
        }
        finally
        {
            ParkTallyPoison.Off(red);
            ParkTallyPoison.Off(blue);
            await daemon.StopAllAsync();
        }
    }

    [Fact]
    public async Task without_extended_progression_shard_state_has_no_parked_count()
    {
        // Extended tracking is on by default since fisher#664, so this store turns it off itself.
        await using var store = guidStore(options => options.Events.EnableExtendedProgressionTracking = false);
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var stream = Guid.NewGuid();
        ParkTallyPoison.On(stream);

        using var daemon = await store.BuildProjectionDaemonAsync();
        try
        {
            await appendAsync(store, stream, new ParkTallied(), new ParkTallyPoisoned());
            await daemon.StartAllAsync();
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);
            (await parkedAsync(store)).ShouldHaveSingleItem();

            // parked_count is neither written nor read without extended tracking, so it stays null
            var shard = store.Advanced.AllAsyncProjectionShardNames().Single();
            var state = (await store.Advanced.AllProjectionProgress(token: Token))
                .Single(x => x.ShardName == shard.Identity);
            state.Sequence.ShouldBeGreaterThan(0);
            state.ParkedCount.ShouldBeNull();
        }
        finally
        {
            ParkTallyPoison.Off(stream);
            await daemon.StopAllAsync();
        }
    }

    [Fact]
    public async Task the_slice_index_is_written_with_the_batch_and_cleared_with_the_projection()
    {
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Policies.AllDocumentsAreMultiTenanted();
            options.Projections.Add(new IndexedParkTallyProjection(), ProjectionLifecycle.Async);
        });
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        const string table = "fi_slice_index_indexedparktally";

        using var daemon = await store.BuildProjectionDaemonAsync();
        try
        {
            await using (var session = store.LightweightSession("red"))
            {
                session.Events.Append(Guid.NewGuid(), new ParkTallied(), new ParkTallied());
                await session.SaveChangesAsync(Token);
            }

            await using (var session = store.LightweightSession("blue"))
            {
                session.Events.Append(Guid.NewGuid(), new ParkTallied());
                await session.SaveChangesAsync(Token);
            }

            await daemon.StartAllAsync();
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);
            (await countAsync(store, table)).ShouldBe(3);

            // Reading one identity's events goes through the index
            var page = await ((IEventStore<IDocumentSession, IQuerySession>)store).LoadIdentityEventsAsync(
                store.Database, new IdentityEventsRequest
                {
                    ProjectionName = "IndexedParkTally",
                    Identity = IndexedParkTallyProjection.Shared,
                    TenantId = "red"
                }, Token);
            page.Source.ShouldBe(IdentityEventSource.SliceIndex);
            page.Events.Count.ShouldBe(2);
            await daemon.StopAllAsync();

            // A rewind deletes the rows above the floor
            var shard = store.Advanced.AllAsyncProjectionShardNames().Single();
            await ((IEventStore<IDocumentSession, IQuerySession>)store).RewindAgentProgressAsync(store.Database,
                shard.Identity, Token, 2);
            (await countAsync(store, table)).ShouldBe(2);

            // Wiping a tenant's data takes its rows
            await store.Advanced.DeleteAllTenantDataAsync("red", Token);
            (await countAsync(store, table)).ShouldBe(0);

            await daemon.StartAllAsync();
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);
            await daemon.StopAllAsync();

            // So does a teardown, and cleaning the event data
            await using (var session = store.LightweightSession("blue"))
            {
                session.Events.Append(Guid.NewGuid(), new ParkTallied());
                await session.SaveChangesAsync(Token);
            }

            await daemon.StartAllAsync();
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);
            (await countAsync(store, table)).ShouldBeGreaterThan(0);
            await daemon.StopAllAsync();

            await ((IEventStore<IDocumentSession, IQuerySession>)store).TeardownExistingProjectionStateAsync(
                store.Database, "IndexedParkTally", Token);
            (await countAsync(store, table)).ShouldBe(0);

            await daemon.StartAllAsync();
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);
            await daemon.StopAllAsync();
            (await countAsync(store, table)).ShouldBeGreaterThan(0);

            await store.Advanced.Clean.DeleteAllEventDataAsync(Token);
            (await countAsync(store, table)).ShouldBe(0);
        }
        finally
        {
            await daemon.StopAllAsync();
        }
    }
}
