using Fisher.Projections;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.CommandLine;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Projections;

public record ParkedOrderPlaced(string Reference);

public record ParkedOrderTouched;

public class ParkedOrder
{
    public Guid Id { get; set; }

    [NaturalKey] public string Reference { get; set; } = "";

    public int Touches { get; set; }
}

public partial class ParkedOrderProjection : SingleStreamProjection<ParkedOrder, Guid>
{
    [NaturalKeySource]
    public static ParkedOrder Create(ParkedOrderPlaced placed) => new() { Reference = placed.Reference };

    public static void Apply(IEvent<ParkedOrderTouched> e, ParkedOrder order)
    {
        ParkTallyPoison.ThrowIfPoisoned(e);
        order.Touches++;
    }
}

public class KeyedParkedOrder
{
    public string Id { get; set; } = "";

    [NaturalKey] public string Reference { get; set; } = "";

    public int Touches { get; set; }
}

public partial class KeyedParkedOrderProjection : SingleStreamProjection<KeyedParkedOrder, string>
{
    [NaturalKeySource]
    public static KeyedParkedOrder Create(ParkedOrderPlaced placed) => new() { Reference = placed.Reference };

    public static void Apply(IEvent<ParkedOrderTouched> e, KeyedParkedOrder order)
    {
        ParkTallyPoison.ThrowIfPoisoned(e);
        order.Touches++;
    }
}

/// <summary>
///     fisher#667 (jasperfx#1273, port of marten#6132): a parked slice whose aggregate has a natural key shows that key
///     beside its stream id, in <c>FetchParkedIdentitiesAsync</c> and so in the <c>projections parked</c> listing.
/// </summary>
public class parked_identities_with_natural_keys : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("parked-natural-keys");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // A keyed aggregate and a keyless one, both async, so one parked list holds both kinds
    private DocumentStore mixedStore() => DocumentStore.For(options =>
    {
        options.ConnectionString = _database.ConnectionString;
        options.AutoCreateSchemaObjects = AutoCreate.All;
        options.Projections.Add(new ParkedOrderProjection(), ProjectionLifecycle.Async);
        options.Projections.Snapshot<GuidParkTally>(SnapshotLifecycle.Async);
    });

    private sealed record Streams(Guid Keyed, Guid KeyedWithoutKey, Guid Keyless);

    // Parks three slices: an order with a natural key, an order whose stream never got one, and a keyless tally
    private static async Task<Streams> parkThreeAsync(DocumentStore store)
    {
        var streams = new Streams(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        ParkTallyPoison.On(streams.Keyed);
        ParkTallyPoison.On(streams.KeyedWithoutKey);
        ParkTallyPoison.On(streams.Keyless);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<ParkedOrder>(streams.Keyed, new ParkedOrderPlaced("ORD-1001"),
                new ParkedOrderTouched());
            session.Events.StartStream<ParkedOrder>(streams.KeyedWithoutKey, new ParkedOrderTouched());
            session.Events.StartStream<GuidParkTally>(streams.Keyless, new ParkTallied(), new ParkTallyPoisoned());
            await session.SaveChangesAsync(Token);
        }

        return streams;
    }

    private static void unpoison(Streams streams)
    {
        ParkTallyPoison.Off(streams.Keyed);
        ParkTallyPoison.Off(streams.KeyedWithoutKey);
        ParkTallyPoison.Off(streams.Keyless);
    }

    [Fact]
    public async Task the_parked_list_carries_the_natural_key_only_where_the_stream_has_one()
    {
        await using var store = mixedStore();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var streams = await parkThreeAsync(store);
        using var daemon = await store.BuildProjectionDaemonAsync();

        try
        {
            await daemon.StartAllAsync();
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

            var parked = await store.Database.FetchParkedIdentitiesAsync(new ParkedIdentityQuery(), Token);
            parked.Count.ShouldBe(3);

            parked.Single(x => x.Identity.Equals(streams.Keyed)).NaturalKey.ShouldBe("ORD-1001");

            // A keyed aggregate whose stream never wrote a key, and an aggregate with no natural key at all
            parked.Single(x => x.Identity.Equals(streams.KeyedWithoutKey)).NaturalKey.ShouldBeNull();
            parked.Single(x => x.Identity.Equals(streams.Keyless)).NaturalKey.ShouldBeNull();

            // Filtering by projection still fills it
            var orders = await store.Database.FetchParkedIdentitiesAsync(
                new ParkedIdentityQuery { ProjectionName = "ParkedOrder" }, Token);
            orders.Select(x => x.NaturalKey).OrderBy(x => x).ShouldBe([null, "ORD-1001"]);
        }
        finally
        {
            unpoison(streams);
            await daemon.StopAllAsync();
        }
    }

    [Fact]
    public async Task the_projections_parked_listing_shows_the_natural_key()
    {
        await using var store = mixedStore();
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var streams = await parkThreeAsync(store);
        using var daemon = await store.BuildProjectionDaemonAsync();

        try
        {
            await daemon.StartAllAsync();
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

            // The command finds the store by the usage's SubjectUri, which is the store's Subject (fisher#671)
            var view = new RecordingParkedView();
            var controller = new ParkedIdentityController([store], view);

            (await controller.ExecuteAsync(new ProjectionInput { Action = ProjectionAction.parked }, Token))
                .ShouldBeTrue();

            view.Errors.ShouldBeEmpty();
            view.Messages.ShouldBeEmpty();
            var listed = view.Parked.ToDictionary(x => x.Parked.IdentityText, x => x.Parked.NaturalKey);
            listed.Count.ShouldBe(3);
            listed[streams.Keyed.ToString("D")].ShouldBe("ORD-1001");
            listed[streams.KeyedWithoutKey.ToString("D")].ShouldBeNull();
            listed[streams.Keyless.ToString("D")].ShouldBeNull();
        }
        finally
        {
            unpoison(streams);
            await daemon.StopAllAsync();
        }
    }

    /// <summary>
    ///     Under conjoined tenancy the same natural key can exist once per tenant, and the same stream key once per
    ///     tenant too, so the lookup matches on the tenant as well as the stream.
    /// </summary>
    [Fact]
    public async Task under_conjoined_tenancy_each_tenants_slice_shows_its_own_key()
    {
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.DatabaseSchemaName = "tenanted";
            options.Events.StreamIdentity = StreamIdentity.AsString;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Schema.For<KeyedParkedOrder>().MultiTenanted();
            options.Projections.Add(new KeyedParkedOrderProjection(), ProjectionLifecycle.Async);
        });
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        const string stream = "order-1";
        ParkTallyPoison.On(stream);

        using var daemon = await store.BuildProjectionDaemonAsync();

        try
        {
            foreach (var (tenant, reference) in new[] { ("a", "ORD-A"), ("b", "ORD-B") })
            {
                await using var session = store.LightweightSession(tenant);
                session.Events.StartStream<KeyedParkedOrder>(stream, new ParkedOrderPlaced(reference),
                    new ParkedOrderTouched());
                await session.SaveChangesAsync(Token);
            }

            // The same stream key in a third tenant with no natural key
            await using (var session = store.LightweightSession("c"))
            {
                session.Events.StartStream<KeyedParkedOrder>(stream, new ParkedOrderTouched());
                await session.SaveChangesAsync(Token);
            }

            await daemon.StartAllAsync();
            await store.Database.WaitForNonStaleProjectionDataAsync(DaemonWait.Timeout);

            var parked = await store.Database.FetchParkedIdentitiesAsync(new ParkedIdentityQuery(), Token);
            parked.Count.ShouldBe(3);
            parked.ShouldAllBe(x => x.IdentityText == stream);
            parked.ToDictionary(x => x.TenantId, x => x.NaturalKey)
                .ShouldBe(new Dictionary<string, string?> { ["a"] = "ORD-A", ["b"] = "ORD-B", ["c"] = null },
                    ignoreOrder: true);

            // And for one tenant
            (await store.Database.FetchParkedIdentitiesAsync(new ParkedIdentityQuery { TenantId = "b" }, Token))
                .ShouldHaveSingleItem().NaturalKey.ShouldBe("ORD-B");
        }
        finally
        {
            ParkTallyPoison.Off(stream);
            await daemon.StopAllAsync();
        }
    }

    private sealed class RecordingParkedView : IParkedIdentityView
    {
        public List<ParkedIdentityRow> Parked { get; } = [];
        public List<string> Errors { get; } = [];

        public void WriteParked(IReadOnlyList<ParkedIdentityRow> rows) => Parked.AddRange(rows);

        public List<string> Messages { get; } = [];

        public void WriteMessage(string message) => Messages.Add(message);

        public void WriteError(string message) => Errors.Add(message);

        public bool Confirm(string question) => false;
    }
}
