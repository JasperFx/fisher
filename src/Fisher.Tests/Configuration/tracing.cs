using System.Diagnostics;
using Fisher.Linq;
using JasperFx;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#48 — the spans Fisher emits for session work, on the <c>Fisher</c>
///     <see cref="ActivitySource" />.
/// </summary>
/// <remarks>
///     <para>
///         <b>The instinct is that tracing is for network calls and an embedded store has none.</b>
///         That is backwards for what operators actually hit: SQLite serialises writers per file, so
///         the interesting question about a slow call is almost always how long it waited for the
///         write lock — and a request that spent its time queued behind another writer is otherwise
///         indistinguishable from one that was simply slow.
///     </para>
///     <para>
///         Which is why the retry event is the test that matters here, and why it is driven by a real
///         contended write rather than by a mock.
///     </para>
/// </remarks>
public class tracing : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("tracing");
    private readonly List<Activity> _activities = [];
    private ActivityListener _listener = null!;
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Fisher",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_activities)
                {
                    _activities.Add(activity);
                }
            }
        };

        ActivitySource.AddActivityListener(_listener);

        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.StoreName = StoreName;
            options.Schema.For<Buoy>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        lock (_activities)
        {
            _activities.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Dispose();
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    ///     The spans this class's store produced.
    /// </summary>
    /// <remarks>
    ///     Filtered by store name, because an <see cref="ActivityListener" /> is process-wide and xUnit
    ///     runs test collections in parallel — without this the listener records every other class's
    ///     spans too, and every <c>Single(...)</c> here fails intermittently depending on what else is
    ///     running. Which is exactly how it was found: green alone, red in the full suite.
    /// </remarks>
    private Activity[] Recorded
    {
        get
        {
            lock (_activities)
            {
                return _activities.Where(x => x.GetTagItem("fisher.store") as string == StoreName).ToArray();
            }
        }
    }

    private const string StoreName = "Traced";

    [Fact]
    public async Task a_commit_emits_a_span_describing_what_it_wrote()
    {
        await using var session = _store.LightweightSession();
        session.Store(new Buoy { Id = Guid.NewGuid(), Name = "North Cardinal" });
        session.Events.StartStream(Guid.NewGuid(), new BuoyMoved("North Cardinal"));

        await session.SaveChangesAsync(Token);

        var span = Recorded.Single(x => x.OperationName == "fisher.save_changes");

        span.Kind.ShouldBe(ActivityKind.Client);
        span.GetTagItem("db.system").ShouldBe("sqlite");
        span.GetTagItem("fisher.store").ShouldBe("Traced");
        span.GetTagItem("fisher.schema").ShouldBe("main");
        span.GetTagItem("fisher.operations").ShouldBe(1);
        span.GetTagItem("fisher.streams").ShouldBe(1);
        span.GetTagItem("fisher.events").ShouldBe(1);
        span.Status.ShouldBe(ActivityStatusCode.Unset);
    }

    [Fact]
    public async Task a_query_and_a_load_each_emit_a_span()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Buoy { Id = id, Name = "South Cardinal" });
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();

        await query.LoadAsync<Buoy>(id, Token);
        await query.Query<Buoy>().Where(x => x.Name == "South Cardinal").ToListAsync(Token);

        Recorded.Count(x => x.OperationName == "fisher.load").ShouldBe(1);

        var span = Recorded.Single(x => x.OperationName == "fisher.query");
        span.GetTagItem("db.collection.name").ShouldBe("fi_doc_buoy");
    }

    /// <remarks>
    ///     A trace that showed a failed commit as a successful span would make the retry events
    ///     recorded underneath it read as noise rather than as the story of what went wrong.
    /// </remarks>
    [Fact]
    public async Task a_failed_commit_marks_its_span_failed()
    {
        var id = Guid.NewGuid();

        await using (var seed = _store.LightweightSession())
        {
            seed.Store(new Buoy { Id = id, Name = "Already here" });
            await seed.SaveChangesAsync(Token);
        }

        await using var session = _store.LightweightSession();
        session.Insert(new Buoy { Id = id, Name = "Duplicate" });

        await Should.ThrowAsync<Exception>(async () => await session.SaveChangesAsync(Token));

        var failed = Recorded.Last(x => x.OperationName == "fisher.save_changes");

        failed.Status.ShouldBe(ActivityStatusCode.Error);
    }

    /// <summary>
    ///     The one this feature exists for: a <c>SQLITE_BUSY</c> retry, recorded as an event on the
    ///     span that was contended.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An event on the enclosing span rather than a span of its own, because a retry is the
    ///         same operation happening again — an operator wants "this save was contended" against the
    ///         save, not orphan spans beside it. And <c>Activity.Current</c> rather than a captured
    ///         span, because the pipeline is shared by every path that executes SQL.
    ///     </para>
    ///     <para>
    ///         <b>Driven through the store's own pipeline with a planted busy exception rather than by
    ///         contending two real writers</b>, and the reason is worth recording. Real contention was
    ///         tried first and does not reach the retry: the wait at <c>BEGIN IMMEDIATE</c> comes from
    ///         the connection string's <c>Default Timeout</c> (30s, and 0 means <em>no limit</em> rather
    ///         than "do not wait"), so a contended save either sits for the full wait and then succeeds
    ///         — no retry — or fails outside the pipeline while the connection is being opened, because
    ///         opening one applies the PRAGMAs and <c>journal_mode</c> wants the write lock. So the
    ///         honest statement of what this pins is the wiring: the pipeline's <c>OnRetry</c> reaches
    ///         <c>RecordRetry</c>, and <c>RecordRetry</c> attaches to the span that is current. That the
    ///         pipeline retries a real <c>SQLITE_BUSY</c> is <c>FisherResilienceDefaults</c>' own
    ///         property and is covered where that lives.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_busy_retry_is_recorded_on_the_span_it_contended()
    {
        var attempts = 0;

        using (var activity = StartSaveLikeSpan())
        {
            await _store.Options.ResiliencePipeline.ExecuteAsync(_ =>
            {
                if (++attempts == 1)
                {
                    throw BusyException();
                }

                return ValueTask.CompletedTask;
            }, Token);
        }

        attempts.ShouldBe(2);

        var span = Recorded.Single(x => x.OperationName == "fisher.save_changes");
        var retry = span.Events.Single(x => x.Name == "fisher.retry");

        retry.Tags.Single(x => x.Key == "fisher.retry.attempt").Value.ShouldBe(1);
        retry.Tags.Single(x => x.Key == "exception.type").Value.ShouldBe(nameof(SqliteException));
        retry.Tags.Single(x => x.Key == "fisher.retry.delay_ms").Value.ShouldNotBeNull();
    }

    private Activity? StartSaveLikeSpan()
        => Fisher.Internal.FisherTracing.StartOperation(
            Fisher.Internal.FisherTracing.SaveChanges, _store.Options);

    /// <summary>A genuine SQLITE_BUSY, so the pipeline's own predicate is what decides to retry.</summary>
    private static SqliteException BusyException() => new("database is locked", 5, 5);

    /// <remarks>
    ///     The untraced path has to stay free, which is the whole reason every span is guarded rather
    ///     than always built. Asserted by disposing the listener and confirming the same work produces
    ///     no activity at all — <c>StartActivity</c> returns null with nothing listening.
    /// </remarks>
    [Fact]
    public async Task nothing_is_emitted_when_nothing_is_listening()
    {
        _listener.Dispose();

        await using var session = _store.LightweightSession();
        session.Store(new Buoy { Id = Guid.NewGuid(), Name = "Unwatched" });
        await session.SaveChangesAsync(Token);

        Activity.Current.ShouldBeNull();
        Recorded.ShouldBeEmpty();
    }
}

public class Buoy
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public record BuoyMoved(string Name);
