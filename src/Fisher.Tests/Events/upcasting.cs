using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Fisher.Subscriptions;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Upcasting;

namespace Fisher.Tests.Events;

public record LanternLit(Guid LanternId, string Keeper);

public record LanternDimmed(Guid LanternId, int Level);

public record LanternKindled(Guid LanternId, string Keeper, string Status);

public record LanternDarkened(Guid LanternId, double Fraction);

/// <summary>
///     fisher#191 — the Fisher half of the shared event upcasting contract
///     (<c>JasperFx.Events.Upcasting</c>, jasperfx#752).
/// </summary>
/// <remarks>
///     <para>
///         <b>What the shared suite covers and this does not.</b> <c>UpcastingCompliance</c> pins the
///         contract itself — every registration shape, every read path, the marten#4680 authority rule
///         — for all three stores. What is left here is the two things it structurally cannot see:
///         the daemon's <em>server-side</em> event type filter, which is SQL Fisher writes, and a
///         binary event body, which is a Fisher/Polecat feature the shared suite's upcasting fixture
///         knows nothing about.
///     </para>
///     <para>
///         The daemon fact is the one that would otherwise be untested code. The shared suite's daemon
///         fact registers a snapshot projection, whose <c>IncludedEventTypes</c> allow list is empty —
///         so no SQL filter is composed at all and the widening below is never exercised. A
///         subscription that names its types is what reaches it.
///     </para>
/// </remarks>
public class upcasting : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("upcasting");
    private DocumentStore? _store;
    private IProjectionDaemon? _daemon;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_daemon is not null)
        {
            await _daemon.StopAllAsync();
            _daemon.Dispose();
        }

        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<DocumentStore> StoreAsync(Action<StoreOptions> configure)
    {
        var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            configure(options);
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        return store;
    }

    /// <summary>
    ///     Write rows the way the pre-migration application really wrote them — through a store that
    ///     has never heard of the transformation — then dispose it.
    /// </summary>
    private async Task<Guid> AppendLegacyAsync(params object[] events)
    {
        await using var legacy = await StoreAsync(options =>
        {
            options.Events.AddEventType(typeof(LanternLit));
            options.Events.AddEventType(typeof(LanternDimmed));
        });

        var streamId = Guid.NewGuid();

        await using var session = legacy.LightweightSession();
        session.Events.StartStream(streamId, events);
        await session.SaveChangesAsync(Token);

        return streamId;
    }

    private static void RegisterUpcasts(StoreOptions options)
    {
        options.Events.Upcasters.Upcast<LanternLit, LanternKindled>(
            old => new LanternKindled(old.LanternId, old.Keeper, "Lit"));

        options.Events.Upcasters.Upcast<LanternDimmed, LanternDarkened>(
            old => new LanternDarkened(old.LanternId, old.Level / 100.0));
    }

    /// <summary>
    ///     ⚠️ A subscription filtered on the NEW event types still receives the old rows, because the
    ///     daemon's server-side <c>type in (...)</c> filter is widened with every registered
    ///     transformation's SOURCE name.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Without the widening this fails by delivering <b>nothing</b> and reporting the shard
    ///         caught up — the worst shape a filter bug can take, since a subscription that receives no
    ///         events looks exactly like a quiet store. The filter is pushed into SQLite precisely so
    ///         non-matching rows never leave it, which is also why the loader's in-memory check (which
    ///         does see the hydrated, upcast event) cannot rescue it.
    ///     </para>
    ///     <para>
    ///         Not reachable from <c>UpcastingCompliance</c>: its daemon fact registers a snapshot
    ///         projection, whose allow list is empty, so no SQL filter is composed at all.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_subscription_filtered_on_the_new_type_receives_the_upcast_old_rows()
    {
        var streamId = await AppendLegacyAsync(new LanternLit(Guid.NewGuid(), "Beregond"),
            new LanternDimmed(Guid.NewGuid(), 40));

        var subscription = new RecordingSubscription();

        _store = await StoreAsync(options =>
        {
            RegisterUpcasts(options);
            options.Projections.Subscribe(subscription,
                o => o.IncludeType<LanternDarkened>());
        });

        _daemon = await _store.BuildProjectionDaemonAsync();
        await _daemon.StartAllAsync();

        await subscription.WaitForAsync(1);

        var received = subscription.Received.ShouldHaveSingleItem();
        received.Data.ShouldBeOfType<LanternDarkened>().Fraction.ShouldBe(0.4);
        received.StreamId.ShouldBe(streamId);

        // And only that one: widening the filter must not smuggle in the other transformation's
        // source rows, which the subscription did not ask for.
        subscription.Received.ShouldAllBe(x => x.Data is LanternDarkened);
    }

    /// <summary>
    ///     A raw-JSON transformation over an event whose body is a BLOB is refused by name rather than
    ///     handed the <c>{}</c> placeholder that <c>data</c> holds for those rows (fisher#93).
    /// </summary>
    /// <remarks>
    ///     Silent otherwise, and in the worst direction: <c>JsonDocument.Parse("{}")</c> succeeds, so
    ///     the transformation would run against an empty object and produce an event with every member
    ///     at its default — a plausible-looking row rather than an error.
    /// </remarks>
    [Fact]
    public async Task a_raw_json_upcast_over_a_binary_body_is_refused_by_name()
    {
        var streamId = await AppendBinaryLanternAsync();

        _store = await StoreAsync(options =>
        {
            options.Events.UseBinarySerializer<LanternLit>(new GzipJsonSerializer());
            options.Events.Upcasters.Upcast<LanternKindled>(
                JasperFx.Events.EventTypeExtensions.GetEventTypeName<LanternLit>(),
                _ => new LanternKindled(Guid.Empty, "unreachable", "unreachable"));
        });

        await using var session = _store.LightweightSession();

        var ex = await Should.ThrowAsync<UpcastingException>(
            () => session.Events.FetchStreamAsync(streamId, token: Token));

        ex.Message.ShouldContain("data_binary");
    }

    /// <summary>
    ///     A <em>typed</em> transformation over a binary body reads it through the old type's own
    ///     <c>IEventBinarySerializer</c>, so upcasting and binary bodies compose.
    /// </summary>
    [Fact]
    public async Task a_typed_upcast_reads_a_binary_body_through_its_own_serializer()
    {
        var streamId = await AppendBinaryLanternAsync();

        _store = await StoreAsync(options =>
        {
            options.Events.UseBinarySerializer<LanternLit>(new GzipJsonSerializer());
            RegisterUpcasts(options);
        });

        await using var session = _store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(streamId, token: Token);

        events.ShouldHaveSingleItem().Data.ShouldBeOfType<LanternKindled>()
            .Keeper.ShouldBe("Beregond");
    }

    /// <summary>
    ///     The store registers every transformation's TARGET event type at construction, because
    ///     nothing else would: dropping the old type is the point of an upcast, so no
    ///     <c>AddEventType</c>, projection registration or append ever mentions the new one either.
    /// </summary>
    [Fact]
    public async Task the_transformation_target_type_is_registered_by_the_store()
    {
        _store = await StoreAsync(RegisterUpcasts);

        _store.Options.EventGraph.AllKnownEventTypes()
            .Select(x => x.EventType)
            .ShouldContain(typeof(LanternKindled));
    }

    private async Task<Guid> AppendBinaryLanternAsync()
    {
        await using var legacy = await StoreAsync(options =>
            options.Events.UseBinarySerializer<LanternLit>(new GzipJsonSerializer()));

        var streamId = Guid.NewGuid();

        await using var session = legacy.LightweightSession();
        session.Events.StartStream(streamId, new LanternLit(Guid.NewGuid(), "Beregond"));
        await session.SaveChangesAsync(Token);

        return streamId;
    }

    /// <summary>
    ///     Gzipped JSON, which is the shape the shared binary suite uses too — arbitrary bytes are
    ///     exactly what a text round trip would corrupt, and nothing else is.
    /// </summary>
    private sealed class GzipJsonSerializer : IEventBinarySerializer
    {
        public byte[] Serialize(Type type, object data)
        {
            using var output = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(output,
                       System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(data, type);
                gzip.Write(json, 0, json.Length);
            }

            return output.ToArray();
        }

        public object Deserialize(Type type, byte[] data)
        {
            using var input = new MemoryStream(data);
            using var gzip = new System.IO.Compression.GZipStream(input,
                System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);

            return JsonSerializer.Deserialize(Encoding.UTF8.GetString(output.ToArray()), type)!;
        }
    }

    /// <inheritdoc cref="a_subscription_filtered_on_the_new_type_receives_the_upcast_old_rows" />
    private sealed class RecordingSubscription : SubscriptionBase
    {
        private readonly ConcurrentQueue<IEvent> _received = new();

        internal IReadOnlyList<IEvent> Received => _received.ToArray();

        public override Task<IDaemonChangeListener> ProcessEventsAsync(EventRange page,
            ISubscriptionController controller, IDocumentSession operations,
            CancellationToken cancellationToken)
        {
            foreach (var @event in page.Events)
            {
                _received.Enqueue(@event);
            }

            return Task.FromResult<IDaemonChangeListener>(NullDaemonChangeListener.Instance);
        }

        /// <remarks>
        ///     Waits on the subscription's own signal rather than on non-staleness, which is true the
        ///     moment the batch's transaction commits — strictly before anything downstream of it. See
        ///     the note in <c>subscriptions.cs</c>; the same race applies to any post-delivery
        ///     assertion.
        /// </remarks>
        internal async Task WaitForAsync(int count, int timeoutSeconds = 30)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);

            while (DateTimeOffset.UtcNow < deadline)
            {
                if (_received.Count >= count)
                {
                    return;
                }

                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            throw new TimeoutException(
                $"The subscription received {_received.Count} events, expected at least {count}.");
        }
    }
}
