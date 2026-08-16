using Fisher.Linq;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Events;

public record ServiceRegistered(string Name, string Uri);

public record AlertRaised(string Reason);

public record AlertCleared(string Reason);

/// <summary>
///     Cares about service registration, and nothing else.
/// </summary>
public partial class ServiceSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Uri { get; set; } = "";

    public static ServiceSummary Create(ServiceRegistered e) => new() { Name = e.Name, Uri = e.Uri };
}

/// <summary>
///     A self-aggregating type whose only handler is a catch-all <c>Evolve(IEvent)</c> — the shape
///     that surfaces fisher#88. A catch-all accepts every event type at the method level, so nothing
///     in the aggregation path filters by event applicability: the aggregator default-constructs an
///     instance and the switch inside simply matches nothing.
///     <para>
///         <see cref="IsActive" /> defaults to <c>true</c> on purpose, as in the reported aggregate.
///         A default-constructed phantom does not read as "empty" — it reads as an <em>active
///         alert</em>.
///     </para>
/// </summary>
public partial class AlertRecord
{
    public string Id { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool IsActive { get; set; } = true;

    public void Evolve(IEvent e)
    {
        switch (e.Data)
        {
            case AlertRaised raised:
                Reason = raised.Reason;
                IsActive = true;
                break;

            case AlertCleared cleared:
                Reason = cleared.Reason;
                IsActive = false;
                break;
        }
    }
}

/// <summary>
///     A Live aggregate over the same events, to pin that the document read is <c>Inline</c> only.
/// </summary>
public partial class LiveAlertRecord
{
    public string Id { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool IsActive { get; set; } = true;

    public void Evolve(IEvent e)
    {
        if (e.Data is AlertRaised raised)
        {
            Reason = raised.Reason;
            IsActive = true;
        }
    }
}

/// <summary>
///     fisher#88 — <c>FetchLatest&lt;T&gt;(id)</c> on a stream that exists but holds no event
///     <c>T</c> handles returned a non-null, default-constructed aggregate where Marten and Polecat
///     return null.
/// </summary>
/// <remarks>
///     <para>
///         <c>FetchLatest&lt;T&gt;(id) is null</c> is the idiomatic "does this aggregate exist?"
///         probe that code branching between <c>StartStream</c> and <c>Append</c> depends on. Under
///         the old behaviour that probe was satisfied by any stream id holding events at all, so the
///         answer depended on whether some other aggregate happened to share the id space — and
///         because a default is not neutral, the phantom read as an <em>active</em> alert.
///     </para>
///     <para>
///         The fix is Marten's mechanism rather than a filter bolted onto aggregation: an Inline
///         aggregate is read from its projected document, which is what the write side already
///         believed — the inline projection screens out streams it does not own, which is why no row
///         was ever written for them. This is polecat#463 / polecat#467, same shape.
///     </para>
/// </remarks>
public class fetch_latest_for_unhandled_streams : IDisposable
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("fetch-latest-88");

    public void Dispose() => _database.Dispose();

    private DocumentStore StoreWith(Action<StoreOptions>? extra = null)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.StreamIdentity = StreamIdentity.AsString;
            options.Projections.Snapshot<ServiceSummary>(SnapshotLifecycle.Inline);
            options.Projections.Snapshot<AlertRecord>(SnapshotLifecycle.Inline);
            extra?.Invoke(options);
        });

    private static async Task StartServiceStream(DocumentStore store, string key)
    {
        await using var session = store.LightweightSession();
        session.Events.StartStream<ServiceSummary>(key,
            new ServiceRegistered(key, "rabbitmq://queue/test_service"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    // ===== The reported case =====

    [Fact]
    public async Task fetch_latest_is_null_for_a_stream_the_aggregate_does_not_handle()
    {
        await using var store = StoreWith();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
        await StartServiceStream(store, "TestService");

        await using var session = store.LightweightSession();

        var alert = await session.Events.FetchLatest<AlertRecord>("TestService",
            TestContext.Current.CancellationToken);

        // Before the fix this was a default-valued AlertRecord — and since IsActive defaults to
        // true, the phantom read as an active alert rather than as absence.
        alert.ShouldBeNull();
    }

    /// <summary>
    ///     The issue verified this already passed. Keeping it is what pins the two halves together:
    ///     the read path now agrees with what the persistence path always did.
    /// </summary>
    [Fact]
    public async Task no_document_is_written_for_a_stream_the_aggregate_does_not_handle()
    {
        await using var store = StoreWith();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
        await StartServiceStream(store, "NoDocService");

        await using var query = store.QuerySession();

        (await query.Query<AlertRecord>().CountAsync(TestContext.Current.CancellationToken))
            .ShouldBe(0);
    }

    // ===== The aggregate that does own the stream is unaffected =====

    [Fact]
    public async Task fetch_latest_still_returns_the_aggregate_that_handles_the_stream()
    {
        await using var store = StoreWith();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
        await StartServiceStream(store, "OwnedService");

        await using var session = store.LightweightSession();

        var summary = await session.Events.FetchLatest<ServiceSummary>("OwnedService",
            TestContext.Current.CancellationToken);

        summary.ShouldNotBeNull();
        summary.Name.ShouldBe("OwnedService");
        summary.Uri.ShouldBe("rabbitmq://queue/test_service");
    }

    [Fact]
    public async Task fetch_latest_returns_the_alert_when_the_stream_does_hold_its_events()
    {
        await using var store = StoreWith();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<AlertRecord>("RealAlert", new AlertRaised("disk full"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = store.LightweightSession();

        var alert = await query.Events.FetchLatest<AlertRecord>("RealAlert",
            TestContext.Current.CancellationToken);

        alert.ShouldNotBeNull();
        alert.Reason.ShouldBe("disk full");
        alert.IsActive.ShouldBeTrue();
    }

    /// <summary>
    ///     The document read has to reflect later events, not just the creating one — otherwise
    ///     "reads its document" would be indistinguishable from "reads a stale first write".
    /// </summary>
    [Fact]
    public async Task fetch_latest_reflects_every_event_the_aggregate_handles()
    {
        await using var store = StoreWith();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<AlertRecord>("Clearing", new AlertRaised("disk full"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var session = store.LightweightSession())
        {
            session.Events.Append("Clearing", new AlertCleared("disk reclaimed"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = store.LightweightSession();

        var alert = await query.Events.FetchLatest<AlertRecord>("Clearing",
            TestContext.Current.CancellationToken);

        alert.ShouldNotBeNull();
        alert.Reason.ShouldBe("disk reclaimed");
        alert.IsActive.ShouldBeFalse();
    }

    // ===== The gates =====

    /// <summary>
    ///     Inline only, mirroring JasperFx's <c>InlineFetchPlanner</c>. A Live aggregate has no
    ///     document to read, so it still folds the stream — which for a catch-all aggregate means it
    ///     still synthesises. That is not the bug being fixed: a Live aggregate is a fold by
    ///     definition, and this pins the gate rather than the phantom.
    /// </summary>
    [Fact]
    public async Task a_live_aggregate_still_folds_the_stream()
    {
        await using var store = StoreWith();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<AlertRecord>("LiveFold", new AlertRaised("still burning"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = store.LightweightSession();

        var live = await query.Events.FetchLatest<LiveAlertRecord>("LiveFold",
            TestContext.Current.CancellationToken);

        live.ShouldNotBeNull();
        live.Reason.ShouldBe("still burning");
    }

    /// <summary>
    ///     A stream that was never started is null either way, so this would pass without the fix.
    ///     It is here because the document path must not turn "no stream" into a throw about a
    ///     missing row.
    /// </summary>
    [Fact]
    public async Task fetch_latest_is_null_for_a_stream_that_does_not_exist()
    {
        await using var store = StoreWith();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using var session = store.LightweightSession();

        (await session.Events.FetchLatest<AlertRecord>("NeverStarted",
            TestContext.Current.CancellationToken)).ShouldBeNull();
    }
}
