using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Events;

/// <summary>
///     fisher#685 — <c>ProjectLatestForCommit</c>, Marten's marten#6149: the aggregate as <c>SaveChangesAsync()</c>
///     will write it, with the versions, timestamps and metadata the commit keeps.
/// </summary>
/// <remarks>
///     The property under test is agreement: what the call returned, what the commit stored (inline snapshot, live
///     aggregation, the daemon's async snapshot) and what a later read folds must all be the same aggregate, metadata
///     included. Each fact compares the returned aggregate with one of those rather than with a literal.
/// </remarks>
public class project_latest_for_commit
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static DocumentStore StoreFor(TemporaryDatabase database, Action<StoreOptions>? configure = null)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.EnableAllEventMetadata();
            configure?.Invoke(options);
        });

    private static async Task<Guid> StartAsync(DocumentStore store)
    {
        var id = Guid.NewGuid();
        await using var session = store.LightweightSession();
        session.Events.StartStream<CommitTrace>(id, new TraceOpened("first"));
        await session.SaveChangesAsync(Token);
        return id;
    }

    [Theory]
    [InlineData(ProjectionLifecycle.Inline)]
    [InlineData(ProjectionLifecycle.Live)]
    public async Task returns_the_aggregate_the_commit_writes(ProjectionLifecycle lifecycle)
    {
        using var database = TemporaryDatabase.Create("plfc-agree");
        await using var store = StoreFor(database, options =>
        {
            if (lifecycle == ProjectionLifecycle.Inline)
            {
                options.Projections.Snapshot<CommitTrace>(SnapshotLifecycle.Inline);
            }
        });

        var id = await StartAsync(store);

        CommitTrace returned;
        await using (var session = store.LightweightSession())
        {
            session.CorrelationId = "corr-685";
            session.CausationId = "cause-685";
            session.CurrentUserName = "jane";

            var stream = await session.Events.FetchForWriting<CommitTrace>(id, Token);
            stream.AppendMany(new TraceScanned("dock"), new TraceScanned("truck"));

            returned = (await session.Events.ProjectLatestForCommit<CommitTrace>(id, Token))!;
            await session.SaveChangesAsync(Token);
        }

        returned.LastVersion.ShouldBe(3);
        returned.Scans.ShouldBe(["dock@2", "truck@3"]);
        returned.LastCorrelationId.ShouldBe("corr-685");
        returned.LastUserName.ShouldBe("jane");
        returned.LastTimestamp.ShouldNotBe(default);

        await using var query = store.QuerySession();
        var folded = (await query.Events.AggregateStreamAsync<CommitTrace>(id, token: Token))!;
        folded.ShouldBeEquivalentTo(returned);

        if (lifecycle == ProjectionLifecycle.Inline)
        {
            (await query.LoadAsync<CommitTrace>(id, Token)).ShouldBeEquivalentTo(returned);
        }
    }

    [Fact]
    public async Task an_async_snapshot_built_by_the_daemon_equals_the_returned_aggregate()
    {
        using var database = TemporaryDatabase.Create("plfc-async");
        await using var store = StoreFor(database,
            options => options.Projections.Snapshot<CommitTrace>(SnapshotLifecycle.Async));
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var id = await StartAsync(store);

        CommitTrace returned;
        await using (var session = store.LightweightSession())
        {
            session.CorrelationId = "async-685";
            var stream = await session.Events.FetchForWriting<CommitTrace>(id, Token);
            stream.AppendOne(new TraceScanned("hub"));

            returned = (await session.Events.ProjectLatestForCommit<CommitTrace>(id, Token))!;
            await session.SaveChangesAsync(Token);
        }

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();
        await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);
        await daemon.StopAllAsync();

        await using var query = store.QuerySession();
        (await query.LoadAsync<CommitTrace>(id, Token)).ShouldBeEquivalentTo(returned);
    }

    /// <summary>The commit keeps what the call assigned, though the clock and the session moved on.</summary>
    [Fact]
    public async Task the_commit_keeps_the_values_the_call_assigned()
    {
        using var database = TemporaryDatabase.Create("plfc-keeps");
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero));
        await using var store = StoreFor(database, options => options.EventGraph.TimeProvider = clock);

        var id = await StartAsync(store);

        CommitTrace returned;
        await using (var session = store.LightweightSession())
        {
            session.CorrelationId = "at-the-call";
            var stream = await session.Events.FetchForWriting<CommitTrace>(id, Token);
            stream.AppendOne(new TraceScanned("gate"));

            returned = (await session.Events.ProjectLatestForCommit<CommitTrace>(id, Token))!;

            clock.Now = clock.Now.AddHours(3);
            session.CorrelationId = "after-the-call";
            await session.SaveChangesAsync(Token);
        }

        returned.LastTimestamp.ShouldBe(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero));

        await using var query = store.QuerySession();
        var stored = (await query.Events.FetchStreamAsync(id, token: Token)).Last();
        stored.Timestamp.ShouldBe(returned.LastTimestamp);
        stored.CorrelationId.ShouldBe("at-the-call");
    }

    [Fact]
    public async Task session_metadata_unset_at_the_call_is_filled_at_commit()
    {
        using var database = TemporaryDatabase.Create("plfc-unset");
        await using var store = StoreFor(database);
        var id = await StartAsync(store);

        await using (var session = store.LightweightSession())
        {
            var stream = await session.Events.FetchForWriting<CommitTrace>(id, Token);
            stream.AppendOne(new TraceScanned("yard"));

            (await session.Events.ProjectLatestForCommit<CommitTrace>(id, Token))!.LastCorrelationId.ShouldBeNull();

            session.CorrelationId = "set-later";
            await session.SaveChangesAsync(Token);
        }

        await using var query = store.QuerySession();
        (await query.Events.FetchStreamAsync(id, token: Token)).Last().CorrelationId.ShouldBe("set-later");
    }

    [Fact]
    public async Task a_started_stream_numbers_from_one()
    {
        using var database = TemporaryDatabase.Create("plfc-start");
        await using var store = StoreFor(database);
        var id = Guid.NewGuid();

        await using var session = store.LightweightSession();
        session.Events.StartStream<CommitTrace>(id, new TraceOpened("new"), new TraceScanned("one"));

        var returned = (await session.Events.ProjectLatestForCommit<CommitTrace>(id, Token))!;
        returned.LastVersion.ShouldBe(2);
        returned.Scans.ShouldBe(["one@2"]);

        await session.SaveChangesAsync(Token);
        (await session.Events.FetchStreamAsync(id, token: Token)).Select(x => x.Version).ShouldBe([1L, 2L]);
    }

    [Fact]
    public async Task a_string_keyed_stream()
    {
        using var database = TemporaryDatabase.Create("plfc-string");
        await using var store = StoreFor(database, options => options.Events.StreamIdentity = StreamIdentity.AsString);
        var key = $"trace-{Guid.NewGuid():N}";

        await using (var start = store.LightweightSession())
        {
            start.Events.StartStream<KeyedCommitTrace>(key, new TraceOpened("k"));
            await start.SaveChangesAsync(Token);
        }

        await using var session = store.LightweightSession();
        var stream = await session.Events.FetchForWriting<KeyedCommitTrace>(key, Token);
        stream.AppendOne(new TraceScanned("dock"));

        var returned = (await session.Events.ProjectLatestForCommit<KeyedCommitTrace>(key, Token))!;
        returned.LastVersion.ShouldBe(2);

        await session.SaveChangesAsync(Token);
        (await session.Events.AggregateStreamAsync<KeyedCommitTrace>(key, token: Token)).ShouldBeEquivalentTo(returned);
    }

    [Fact]
    public async Task without_pending_events_it_is_project_latest()
    {
        using var database = TemporaryDatabase.Create("plfc-nothing");
        await using var store = StoreFor(database);
        var id = await StartAsync(store);

        await using var session = store.LightweightSession();
        (await session.Events.ProjectLatestForCommit<CommitTrace>(id, Token))
            .ShouldBeEquivalentTo(await session.Events.ProjectLatest<CommitTrace>(id, Token));
    }

    [Fact]
    public async Task refuses_a_stream_with_no_expected_version_and_leaves_its_events_alone()
    {
        using var database = TemporaryDatabase.Create("plfc-refuse");
        await using var store = StoreFor(database);
        var id = await StartAsync(store);

        await using var session = store.LightweightSession();
        session.Events.Append(id, new TraceScanned("loose"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await session.Events.ProjectLatestForCommit<CommitTrace>(id, Token));
        ex.Message.ShouldContain("FetchForWriting()");

        var pending = session.Events.PendingStreams.Single().Events.Single();
        pending.Version.ShouldBe(0);
        pending.Timestamp.ShouldBe(default);

        // Without the call the session commits as ever
        await session.SaveChangesAsync(Token);
    }

    [Fact]
    public async Task the_commit_refuses_a_stream_whose_expected_version_changed_after_the_call()
    {
        using var database = TemporaryDatabase.Create("plfc-changed");
        await using var store = StoreFor(database);
        var id = await StartAsync(store);

        await using var session = store.LightweightSession();
        session.Events.Append(id, 2, new TraceScanned("first"));
        await session.Events.ProjectLatestForCommit<CommitTrace>(id, Token);

        // A new expected version for the same pending stream
        session.Events.Append(id, 4, new TraceScanned("second"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => session.SaveChangesAsync(Token));
        ex.Message.ShouldContain("after ProjectLatestForCommit()");

        // Nothing was written
        await using var query = store.QuerySession();
        (await query.Events.FetchStreamAsync(id, token: Token)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task appending_without_moving_the_expected_version_still_commits()
    {
        using var database = TemporaryDatabase.Create("plfc-more");
        await using var store = StoreFor(database);
        var id = await StartAsync(store);

        await using (var session = store.LightweightSession())
        {
            var stream = await session.Events.FetchForWriting<CommitTrace>(id, Token);
            stream.AppendOne(new TraceScanned("one"));
            await session.Events.ProjectLatestForCommit<CommitTrace>(id, Token);

            // Not covered by the call, and not a change of expected version
            stream.AppendOne(new TraceScanned("two"));
            await session.SaveChangesAsync(Token);
        }

        await using var query = store.QuerySession();
        (await query.Events.AggregateStreamAsync<CommitTrace>(id, token: Token))!.Scans.ShouldBe(["one@2", "two@3"]);
    }

    [Fact]
    public async Task ejecting_the_pending_changes_forgets_the_call()
    {
        using var database = TemporaryDatabase.Create("plfc-eject");
        await using var store = StoreFor(database);
        var id = await StartAsync(store);

        await using var session = store.LightweightSession();
        session.Events.Append(id, 2, new TraceScanned("dropped"));
        await session.Events.ProjectLatestForCommit<CommitTrace>(id, Token);

        session.EjectAllPendingChanges();

        session.Events.Append(id, 2, new TraceScanned("kept"));
        await session.SaveChangesAsync(Token);

        (await session.Events.AggregateStreamAsync<CommitTrace>(id, token: Token))!.Scans.ShouldBe(["kept@2"]);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

public record TraceOpened(string Name);

public record TraceScanned(string Place);

/// <summary>Records each event's envelope, so a test can compare what the call and the commit each produced.</summary>
public class CommitTrace
{
    public Guid Id { get; set; }
    public List<string> Scans { get; set; } = [];
    public long LastVersion { get; set; }
    public DateTimeOffset LastTimestamp { get; set; }
    public string? LastCorrelationId { get; set; }
    public string? LastUserName { get; set; }

    public static CommitTrace Create(IEvent<TraceOpened> e) => new CommitTrace().Record(e);

    public void Apply(IEvent<TraceScanned> e)
    {
        Scans.Add($"{e.Data.Place}@{e.Version}");
        Record(e);
    }

    private CommitTrace Record(IEvent e)
    {
        LastVersion = e.Version;
        LastTimestamp = e.Timestamp;
        LastCorrelationId = e.CorrelationId;
        LastUserName = e.UserName;
        return this;
    }
}

public class KeyedCommitTrace
{
    public string Id { get; set; } = "";
    public long LastVersion { get; set; }

    public static KeyedCommitTrace Create(IEvent<TraceOpened> e) => new() { LastVersion = e.Version };

    public void Apply(IEvent<TraceScanned> e) => LastVersion = e.Version;
}
