using Fisher.Projections;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Projections;

/// <summary>
///     What an <see cref="IEvent" /> envelope carries when an inline projection folds it (fisher#72).
/// </summary>
/// <remarks>
///     <para>
///         The string case is the one that was broken. <c>StreamAction.AddEvent</c> stamps
///         <c>StreamId</c> / <c>StreamKey</c> / <c>TenantId</c> onto every event it takes, and the
///         <c>Guid</c> append overload goes through it — but
///         <c>StreamAction.Append(graph, string, …)</c> appended straight to the backing list and did
///         not, so an event appended to a string-identified stream reached a projection with an empty
///         key. Nothing threw; the projection wrote a document with a blank field.
///     </para>
///     <para>
///         <b>These tests now guard the fix rather than a Fisher workaround.</b> Fisher stamped the
///         identity in its own <c>AppendPlanner</c> until jasperfx#663 shipped in JasperFx 2.48.0,
///         which routes both string overloads through <c>AddEvents</c>; the workaround is gone and
///         this is what holds the behaviour. Both identities stay pinned because the asymmetry was
///         upstream's, so a later release must not silently change which half is covered — and because
///         the Guid half passing is what tells a regression here apart from a broken test.
///     </para>
///     <para>
///         The async daemon was never affected: <c>FisherEventLoader</c> hydrates through
///         <c>FisherEventsRowReader.ReadEventAcrossStreams</c>, which takes the identity off the row.
///     </para>
/// </remarks>
public class event_envelope_metadata_in_projections : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("envelope-metadata");
    private DocumentStore _guidStore = null!;
    private DocumentStore _stringStore = null!;

    public async ValueTask InitializeAsync()
    {
        _stringStore = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.DatabaseSchemaName = "strings";
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.StreamIdentity = StreamIdentity.AsString;
            options.Schema.For<EnvelopeSnapshot>();
            options.Projections.Add(new EnvelopeProjection(), ProjectionLifecycle.Inline);
        });

        _guidStore = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.DatabaseSchemaName = "guids";
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<EnvelopeSnapshot>();
            options.Projections.Add(new EnvelopeProjection(), ProjectionLifecycle.Inline);
        });

        await _stringStore.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
        await _guidStore.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _stringStore.DisposeAsync();
        await _guidStore.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task an_appended_event_carries_its_stream_key_into_an_inline_projection()
    {
        await using (var session = _stringStore.LightweightSession())
        {
            session.Events.Append("TestService", new EnvelopeRecorded("first"), new EnvelopeRecorded("second"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _stringStore.LightweightSession();

        var first = await query.LoadAsync<EnvelopeSnapshot>("first", TestContext.Current.CancellationToken);
        var second = await query.LoadAsync<EnvelopeSnapshot>("second", TestContext.Current.CancellationToken);

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();

        first.StreamKey.ShouldBe("TestService");
        second.StreamKey.ShouldBe("TestService");

        first.TenantId.ShouldBe(StorageConstants.DefaultTenantId);
        first.Version.ShouldBe(1);
        second.Version.ShouldBe(2);
    }

    [Fact]
    public async Task a_started_stream_carries_its_stream_key_into_an_inline_projection()
    {
        await using (var session = _stringStore.LightweightSession())
        {
            session.Events.StartStream("StartedService", new EnvelopeRecorded("started"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _stringStore.LightweightSession();

        var entry = await query.LoadAsync<EnvelopeSnapshot>("started", TestContext.Current.CancellationToken);

        entry.ShouldNotBeNull();
        entry.StreamKey.ShouldBe("StartedService");
    }

    [Fact]
    public async Task an_appended_event_carries_its_stream_id_into_an_inline_projection()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _guidStore.LightweightSession())
        {
            session.Events.Append(streamId, new EnvelopeRecorded("guid-first"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _guidStore.LightweightSession();

        var entry = await query.LoadAsync<EnvelopeSnapshot>("guid-first", TestContext.Current.CancellationToken);

        entry.ShouldNotBeNull();
        entry.StreamId.ShouldBe(streamId);
    }

    [Fact]
    public async Task an_appended_event_carries_its_timestamp_into_an_inline_projection()
    {
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);

        await using (var session = _stringStore.LightweightSession())
        {
            session.Events.StartStream("TimestampedService", new EnvelopeRecorded("stamped"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _stringStore.LightweightSession();

        var entry = await query.LoadAsync<EnvelopeSnapshot>("stamped", TestContext.Current.CancellationToken);

        entry.ShouldNotBeNull();

        // The broken behaviour was not a wrong value but the *default* one: the envelope's
        // Timestamp was only ever hydrated on read paths, so an inline projection folded
        // events whose Timestamp was DateTimeOffset.MinValue and baked year-0001 dates
        // into every read model that recorded e.Timestamp.
        entry.Timestamp.ShouldBeGreaterThan(before);
        entry.Timestamp.ShouldBeLessThan(DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task the_inline_timestamp_and_the_persisted_timestamp_agree()
    {
        await using (var session = _stringStore.LightweightSession())
        {
            session.Events.StartStream("AgreeingService", new EnvelopeRecorded("agreeing"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _stringStore.LightweightSession();

        var entry = await query.LoadAsync<EnvelopeSnapshot>("agreeing", TestContext.Current.CancellationToken);
        var replayed = await query.Events.FetchStreamAsync("AgreeingService",
            token: TestContext.Current.CancellationToken);

        entry.ShouldNotBeNull();
        replayed.Count.ShouldBe(1);

        // What the inline projection saw must be what a rebuild will see, or the same
        // projection produces different documents inline vs rebuilt.
        replayed[0].Timestamp.ShouldBe(entry.Timestamp);
    }
}

public record EnvelopeRecorded(string Name);

// partial, because JasperFx's source generator emits the conventional-method dispatcher into it.
public partial class EnvelopeProjection : EventProjection
{
    public EnvelopeSnapshot Create(IEvent<EnvelopeRecorded> e) => new()
    {
        Id = e.Data.Name,
        StreamId = e.StreamId,
        StreamKey = e.StreamKey ?? string.Empty,
        TenantId = e.TenantId ?? string.Empty,
        Version = e.Version,
        Timestamp = e.Timestamp
    };
}

public class EnvelopeSnapshot
{
    public string Id { get; set; } = string.Empty;
    public Guid StreamId { get; set; }
    public string StreamKey { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public long Version { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
