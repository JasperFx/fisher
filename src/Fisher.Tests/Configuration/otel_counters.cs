using System.Diagnostics.Metrics;
using JasperFx;
using JasperFx.Events.Projections;
using JasperFx.Core;
using JasperFx.OpenTelemetry;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#208 — Fisher's <see cref="Meter" /> and the counters an application opts into.
/// </summary>
/// <remarks>
///     <para>
///         <b>The discriminating fact in this file is
///         <c>a_contended_commit_is_visible_in_the_wait_and_invisible_in_the_retries</c>.</b> The
///         obvious instrument for a single-writer store is a <c>SQLITE_BUSY</c> retry counter, and
///         fisher#163 established that it reads zero through the exact incident it exists to diagnose:
///         a contended writer waits inside <c>BEGIN IMMEDIATE</c> under the connection string's busy
///         timeout and eventually succeeds, never reaching the retry. That test drives real contention
///         and asserts both halves at once.
///     </para>
///     <para>
///         <b>A <see cref="MeterListener" /> is process-wide, exactly as an <c>ActivityListener</c>
///         is</b> — the lesson the tracing tests record. Every measurement here is filtered by the
///         <c>fisher.store</c> tag the test's own store sets, so a full-suite run cannot make one class
///         see another's numbers.
///     </para>
/// </remarks>
public class otel_counters : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("otel-counters");
    private const string StoreName = "otel_counters_store";

    private readonly List<Measurement> _measurements = [];
    private MeterListener _listener = null!;
    private DocumentStore _store = null!;

    private CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == Fisher.Services.OpenTelemetryOptions.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };

        _listener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<long>(
            (instrument, value, tags, _) => Record(instrument, value, tags));

        _listener.Start();

        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.StoreName = StoreName;
            options.Schema.For<Dory>();

            options.OpenTelemetry.TrackWriteLockContention();
            options.OpenTelemetry.TrackEventCounters();
            options.OpenTelemetry.TrackDocumentCounters();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        // The migration takes the write lock too, so anything it recorded is not this test's subject.
        lock (_measurements)
        {
            _measurements.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Dispose();
        await _store.DisposeAsync();
        await _database.DisposeAsync();
    }

    private void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct
    {
        var copied = new Dictionary<string, object?>();

        foreach (var tag in tags)
        {
            copied[tag.Key] = tag.Value;
        }

        // The store tag is what makes a process-wide listener safe under xUnit's parallel collections.
        if (copied.GetValueOrDefault("fisher.store") as string != StoreName) return;

        lock (_measurements)
        {
            _measurements.Add(new Measurement(instrument.Name, Convert.ToDouble(value), copied));
        }
    }

    private Measurement[] For(string instrument)
    {
        lock (_measurements)
        {
            return _measurements.Where(x => x.Instrument == instrument).ToArray();
        }
    }

    // ---- the write lock, which is the instrument this node exists for ----

    [Fact]
    public async Task a_commit_records_how_long_it_waited_for_the_write_lock()
    {
        await using var session = _store.LightweightSession();
        session.Store(new Dory { Id = Guid.NewGuid(), Name = "Bonnie" });
        await session.SaveChangesAsync(Token);

        var waits = For("fisher.write_lock.wait");

        waits.ShouldNotBeEmpty();
        waits.ShouldAllBe(x => x.Tags["fisher.write_lock.holder"] as string == "session");
    }

    /// <summary>
    ///     <b>The reason the histogram exists and the retry counter is not enough on its own.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A second connection holds the file's write lock across the whole of this session's
    ///         commit. That commit does not fail and is not retried — it sits inside
    ///         <c>BEGIN IMMEDIATE</c> under the connection string's <c>Default Timeout</c> until the
    ///         lock is free, which is precisely fisher#163's finding: contention here is <em>waiting</em>,
    ///         not retrying.
    ///     </para>
    ///     <para>
    ///         So the assertions are two halves of one claim — a wait that plainly exceeds the hold, and
    ///         a retry count of exactly zero. A store instrumented only on retries would read this
    ///         incident as nothing having happened.
    ///     </para>
    ///     <para>
    ///         The session's connection is opened by a read <em>before</em> the lock is taken, because
    ///         opening one applies the PRAGMA batch and <c>journal_mode</c> wants the write lock — so an
    ///         unopened session would contend one step earlier, where this is not measuring.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_contended_commit_is_visible_in_the_wait_and_invisible_in_the_retries()
    {
        var hold = TimeSpan.FromMilliseconds(400);

        await using var session = _store.LightweightSession();

        // Open the connection and settle the PRAGMAs before anything is contended.
        await session.LoadAsync<Dory>(Guid.NewGuid(), Token);

        session.Store(new Dory { Id = Guid.NewGuid(), Name = "Marlin" });

        await using var blocker = new SqliteConnection(_database.ConnectionString);
        await blocker.OpenAsync(Token);

        var released = new TaskCompletionSource();

        var holding = Task.Run(async () =>
        {
            await using var transaction =
                (SqliteTransaction)await blocker.BeginTransactionAsync(
                    System.Data.IsolationLevel.Serializable, Token);

            await Task.Delay(hold, Token);
            await transaction.RollbackAsync(Token);

            released.SetResult();
        }, Token);

        // Give the blocker time to actually hold the lock before the commit asks for it.
        await Task.Delay(50, Token);

        await session.SaveChangesAsync(Token);

        await released.Task;
        await holding;

        var contended = For("fisher.write_lock.wait")
            .Where(x => x.Tags["fisher.write_lock.holder"] as string == "session")
            .ToArray();

        contended.ShouldNotBeEmpty();

        // Well under the 400ms hold, so the assertion is about the shape rather than about a timer's
        // precision on a loaded CI box.
        contended.Max(x => x.Value).ShouldBeGreaterThan(200d);

        // And the half that makes this test worth having.
        For("fisher.write_lock.retries").ShouldBeEmpty();
    }

    /// <summary>
    ///     The retry counter still exists, and this is what it counts — driven through the store's own
    ///     pipeline with a planted <c>SQLITE_BUSY</c>, the way <c>tracing</c> drives the span event.
    /// </summary>
    [Fact]
    public async Task a_retried_busy_is_counted_beside_the_wait()
    {
        var attempts = 0;

        await _store.Options.ResiliencePipeline.ExecuteAsync(_ =>
        {
            if (++attempts == 1)
            {
                throw new SqliteException("database is locked", 5, 5);
            }

            return ValueTask.CompletedTask;
        }, Token);

        attempts.ShouldBe(2);

        var retries = For("fisher.write_lock.retries");

        retries.Length.ShouldBe(1);
        retries[0].Value.ShouldBe(1d);
        retries[0].Tags["exception.type"].ShouldBe(nameof(SqliteException));
    }

    /// <summary>
    ///     The daemon is one more writer competing for the same lock, and its wait is tagged apart from
    ///     a session's — otherwise "the application is contended" and "the daemon is starving the
    ///     application" look identical.
    /// </summary>
    [Fact]
    public async Task the_daemon_batch_records_its_wait_under_its_own_holder()
    {
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.StoreName = StoreName;
            options.DatabaseSchemaName = "daemonmetrics";
            options.OpenTelemetry.TrackWriteLockContention();
            options.Projections.Snapshot<Shoal>(SnapshotLifecycle.Async);
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var id = Guid.NewGuid();

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Shoal>(id, new FishJoined("Nemo"));
            await session.SaveChangesAsync(Token);
        }

        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();
        await daemon.WaitForNonStaleData(30.Seconds());

        For("fisher.write_lock.wait")
            .ShouldContain(x => x.Tags["fisher.write_lock.holder"] as string == "daemon");
    }

    // ---- the change-set counters ----

    [Fact]
    public async Task appended_events_are_counted_by_type_and_tenant()
    {
        await using var session = _store.LightweightSession();
        session.Events.StartStream<Shoal>(Guid.NewGuid(), new FishJoined("Dory"), new FishJoined("Nemo"));
        await session.SaveChangesAsync(Token);

        var counted = For("fisher.events.appended");

        counted.Length.ShouldBe(2);
        counted.ShouldAllBe(x => x.Tags["fisher.event.type"] as string == "fish_joined");
        counted.ShouldAllBe(x => x.Tags["fisher.tenant"] as string == StorageConstants.DefaultTenantId);
    }

    [Fact]
    public async Task written_documents_are_counted_by_operation()
    {
        var updated = Guid.NewGuid();

        await using (var seed = _store.LightweightSession())
        {
            seed.Store(new Dory { Id = updated, Name = "before" });
            await seed.SaveChangesAsync(Token);
        }

        lock (_measurements)
        {
            _measurements.Clear();
        }

        await using var session = _store.LightweightSession();
        session.Store(new Dory { Id = updated, Name = "after" });
        session.Insert(new Dory { Id = Guid.NewGuid(), Name = "inserted" });
        session.Delete<Dory>(Guid.NewGuid());
        await session.SaveChangesAsync(Token);

        var counted = For("fisher.documents.written");

        counted.Count(x => x.Tags["fisher.document.operation"] as string == "update").ShouldBe(1);
        counted.Count(x => x.Tags["fisher.document.operation"] as string == "insert").ShouldBe(1);
        counted.Count(x => x.Tags["fisher.document.operation"] as string == "delete").ShouldBe(1);
        counted.ShouldAllBe(x => x.Tags["fisher.document.type"] as string == nameof(Dory));
    }

    [Fact]
    public async Task a_custom_counter_over_the_change_set_is_applied()
    {
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.StoreName = StoreName;
            options.DatabaseSchemaName = "customcounter";
            options.Schema.For<Dory>();

            options.OpenTelemetry.ExportCounterOnChangeSets<long>(
                "app.dories.landed", "dories",
                (counter, commit) => counter.Add(commit.Inserted.Count(),
                    new System.Diagnostics.TagList { { "fisher.store", StoreName } }));
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = store.LightweightSession();
        session.Insert(new Dory { Id = Guid.NewGuid(), Name = "one" });
        session.Insert(new Dory { Id = Guid.NewGuid(), Name = "two" });
        await session.SaveChangesAsync(Token);

        For("app.dories.landed").Single().Value.ShouldBe(2d);
    }

    /// <summary>
    ///     A counter that throws must not fail a transaction that has already committed — the caller
    ///     would read it as the write having failed, which it did not.
    /// </summary>
    [Fact]
    public async Task a_failing_counter_does_not_fail_the_commit()
    {
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.DatabaseSchemaName = "failingcounter";
            options.Schema.For<Dory>();

            options.OpenTelemetry.ExportCounterOnChangeSets<long>(
                "app.explodes", "boom", (_, _) => throw new InvalidOperationException("nope"));
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var id = Guid.NewGuid();

        await using var session = store.LightweightSession();
        session.Store(new Dory { Id = id, Name = "survivor" });

        await Should.NotThrowAsync(async () => await session.SaveChangesAsync(Token));

        await using var reader = store.QuerySession();
        (await reader.LoadAsync<Dory>(id, Token)).ShouldNotBeNull();
    }

    // ---- what stays off ----

    /// <summary>
    ///     Nothing is created until something asks, so a store that never opts in publishes no
    ///     instruments at all — which is what makes the recording sites a null check rather than a
    ///     measurement that is thrown away.
    /// </summary>
    [Fact]
    public async Task a_store_that_opts_into_nothing_publishes_no_instruments()
    {
        var published = new List<string>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name != Fisher.Services.OpenTelemetryOptions.MeterName) return;

                lock (published)
                {
                    published.Add(instrument.Name);
                }
            }
        };

        listener.Start();

        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.DatabaseSchemaName = "nocounters";
            options.Schema.For<Dory>();
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = store.LightweightSession();
        session.Store(new Dory { Id = Guid.NewGuid(), Name = "quiet" });
        await session.SaveChangesAsync(Token);

        // Other stores in this class publish instruments on the same meter name, so the assertion is
        // that none was published *by this store* — which is what an empty Applications list and two
        // null instrument fields amount to.
        store.Options.OpenTelemetry.Applications.ShouldBeEmpty();
        store.Options.OpenTelemetry.TracksWriteLock.ShouldBeFalse();
    }

    /// <summary>
    ///     A store with no change-set counters carries no commit listener, so opting into nothing costs
    ///     nothing at commit rather than costing an empty loop.
    /// </summary>
    [Fact]
    public void no_counters_means_no_commit_listener()
    {
        using var bare = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.DatabaseSchemaName = "barelistener";
        });

        bare.Options.Listeners.ShouldBeEmpty();

        using var counted = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.DatabaseSchemaName = "countedlistener";
            options.OpenTelemetry.TrackDocumentCounters();
        });

        counted.Options.Listeners.ShouldHaveSingleItem()
            .ShouldBeOfType<Fisher.Services.FisherCommitMetrics>();
    }

    /// <summary>
    ///     The one inherited member Fisher refuses rather than silently ignores — see the property's
    ///     own remarks for why a Fisher connection count would mean something different from Marten's.
    /// </summary>
    [Fact]
    public void tracking_connections_is_refused_by_name()
    {
        var options = new StoreOptions();

        // None is the default and stays accepted, so the refusal is about asking for the feature
        // rather than about touching the property.
        Should.NotThrow(() => options.OpenTelemetry.TrackConnections = TrackLevel.None);

        var ex = Should.Throw<NotSupportedException>(
            () => options.OpenTelemetry.TrackConnections = TrackLevel.Normal);

        ex.Message.ShouldContain("TrackWriteLockContention");
    }

    [Fact]
    public void the_meter_is_named_for_the_activity_source()
    {
        // One name for both, so an application subscribes with AddSource("Fisher") and
        // AddMeter("Fisher") rather than having to look the second one up.
        Fisher.Services.OpenTelemetryOptions.MeterName.ShouldBe("Fisher");
    }

    [Fact]
    public void opting_in_twice_creates_one_instrument()
    {
        var options = new StoreOptions();

        options.OpenTelemetry.TrackWriteLockContention();
        options.OpenTelemetry.TrackWriteLockContention();

        options.OpenTelemetry.TracksWriteLock.ShouldBeTrue();
    }

    private sealed record Measurement(string Instrument, double Value, Dictionary<string, object?> Tags);

    public class Dory
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public record FishJoined(string Name);

    public class Shoal
    {
        public Guid Id { get; set; }
        public int Count { get; set; }

        public void Apply(FishJoined _) => Count++;
    }
}
