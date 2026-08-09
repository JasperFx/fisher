using Fisher.Exceptions;
using JasperFx;
using JasperFx.Events;
using JasperFx.MultiTenancy;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using Fisher.Projections;

namespace Fisher.Tests.Events;

/// <summary>
///     Natural keys — fisher#40.
/// </summary>
/// <remarks>
///     <para>
///         The definition, the attributes and their discovery are all JasperFx's; what Fisher supplies
///         is the storage seam, the same division as the async daemon. So the tests worth having are
///         about the seam: that the key row commits with the events rather than beside them, that a
///         second stream cannot take a key, and that the lookup is scoped and archive-aware.
///     </para>
///     <para>
///         The last one is where Fisher diverges: there is no <c>is_archived</c> column on the lookup
///         table, because the lookup joins <c>fi_streams</c> for the version anyway and reading the
///         flag there makes the streams table the only place that knows.
///     </para>
/// </remarks>
public partial class natural_keys : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("naturalkeys");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = StoreFor(conjoined: false);
        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    private DocumentStore StoreFor(bool conjoined)
        => DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;

            if (conjoined)
            {
                o.DatabaseSchemaName = "tenanted";
                o.Events.TenancyStyle = TenancyStyle.Conjoined;
            }

            o.Projections.Add(new OrderProjection(), ProjectionLifecycle.Inline);
        });

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<Guid> StartOrder(string reference, DocumentStore? store = null,
        string? tenantId = null)
    {
        var streamId = Guid.NewGuid();

        await using var session = (store ?? _store).LightweightSession(tenantId);
        session.Events.StartStream<Order>(streamId, new OrderPlaced(reference));
        await session.SaveChangesAsync(Token);

        return streamId;
    }

    // ---- the round trip ----

    [Fact]
    public async Task fetch_for_writing_by_natural_key()
    {
        var streamId = await StartOrder("ORD-1");

        await using var session = _store.LightweightSession();

        var stream = await session.Events.FetchForWritingByNaturalKey<Order, string>("ORD-1", Token);

        stream.Id.ShouldBe(streamId);
        stream.Aggregate!.Reference.ShouldBe("ORD-1");

        stream.AppendOne(new OrderShipped());
        await session.SaveChangesAsync(Token);

        (await session.Events.FetchLatestByNaturalKey<Order, string>("ORD-1", Token))!
            .Shipped.ShouldBeTrue();
    }

    /// <summary>
    ///     The generic overloads were the one place <c>IEventStoreOperations</c> stayed partial after
    ///     fisher#14 closed the strong-typed half. This is the other half.
    /// </summary>
    [Fact]
    public async Task the_generic_overloads_route_a_natural_key()
    {
        var streamId = await StartOrder("ORD-2");

        await using var session = _store.LightweightSession();

        // A string id on a Guid-identity store is unambiguously a natural key.
        var stream = await session.Events.FetchForWriting<Order, string>("ORD-2", Token);
        stream.Id.ShouldBe(streamId);

        (await session.Events.FetchLatest<Order, string>("ORD-2", Token))!.Reference.ShouldBe("ORD-2");
    }

    /// <summary>
    ///     The stream identity type wins where the two coincide, because reading a Guid as a stream id
    ///     cannot depend on which aggregate types happen to declare a key.
    /// </summary>
    [Fact]
    public async Task the_stream_identity_type_still_wins_in_the_generic_overload()
    {
        var streamId = await StartOrder("ORD-3");

        await using var session = _store.LightweightSession();

        (await session.Events.FetchForWriting<Order, Guid>(streamId, Token)).Id.ShouldBe(streamId);
    }

    // ---- the seam ----

    /// <summary>
    ///     <b>The property with teeth.</b> A key registered outside the append's transaction leaves
    ///     either a stream no key resolves to or a key naming a stream that does not exist.
    /// </summary>
    [Fact]
    public async Task the_key_row_is_absent_after_a_rolled_back_start()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream<Order>(streamId, new OrderPlaced("ORD-ROLLBACK"));

            // Fails the batch from inside its own transaction, so the events roll back — and the key
            // row has to roll back with them rather than being left behind.
            session.QueueSqlCommand("insert into fi_streams (id) values ('boom'), ('boom')");

            await Should.ThrowAsync<Exception>(() => session.SaveChangesAsync(Token));
        }

        await using var check = _store.LightweightSession();

        await Should.ThrowAsync<UnknownNaturalKeyException>(() =>
            check.Events.FetchForWritingByNaturalKey<Order, string>("ORD-ROLLBACK", Token));
    }

    /// <summary>
    ///     Polecat's <c>MERGE</c> repoints the key at the newcomer, so the original stream silently
    ///     becomes unreachable by the identifier it was created with. Fisher refuses.
    /// </summary>
    [Fact]
    public async Task a_second_stream_cannot_take_an_existing_key()
    {
        await StartOrder("ORD-DUP");

        await using var session = _store.LightweightSession();
        session.Events.StartStream<Order>(Guid.NewGuid(), new OrderPlaced("ORD-DUP"));

        var exception = await Should.ThrowAsync<DuplicateNaturalKeyException>(() =>
            session.SaveChangesAsync(Token));

        exception.Key.ShouldBe("ORD-DUP");
        exception.AggregateType.ShouldBe(typeof(Order));
    }

    /// <summary>
    ///     Every event carrying the key rewrites the row, so re-asserting the same mapping has to be
    ///     idempotent — otherwise the second event on a stream would trip the duplicate guard.
    /// </summary>
    [Fact]
    public async Task re_asserting_the_same_mapping_is_idempotent()
    {
        var streamId = await StartOrder("ORD-SAME");

        await using var session = _store.LightweightSession();
        session.Events.Append(streamId, new OrderPlaced("ORD-SAME"), new OrderPlaced("ORD-SAME"));

        await Should.NotThrowAsync(() => session.SaveChangesAsync(Token));

        (await session.Events.FetchForWritingByNaturalKey<Order, string>("ORD-SAME", Token))
            .Id.ShouldBe(streamId);
    }

    /// <summary>
    ///     Archived streams are filtered by joining <c>fi_streams</c> rather than by a flag copied onto
    ///     the lookup table — one source of truth, and no sync step to forget.
    /// </summary>
    [Fact]
    public async Task an_archived_stream_no_longer_resolves()
    {
        var streamId = await StartOrder("ORD-ARCHIVED");

        await using (var session = _store.LightweightSession())
        {
            session.Events.ArchiveStream(streamId);
            await session.SaveChangesAsync(Token);
        }

        await using var check = _store.LightweightSession();

        await Should.ThrowAsync<UnknownNaturalKeyException>(() =>
            check.Events.FetchForWritingByNaturalKey<Order, string>("ORD-ARCHIVED", Token));
    }

    [Fact]
    public async Task an_unknown_key_is_refused_by_name()
    {
        await using var session = _store.LightweightSession();

        var exception = await Should.ThrowAsync<UnknownNaturalKeyException>(() =>
            session.Events.FetchForWritingByNaturalKey<Order, string>("ORD-NOPE", Token));

        exception.Message.ShouldContain("ORD-NOPE");
    }

    [Fact]
    public async Task an_aggregate_with_no_natural_key_is_refused_by_name()
    {
        await using var session = _store.LightweightSession();

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            session.Events.FetchForWritingByNaturalKey<Untracked, string>("anything", Token));

        exception.Message.ShouldContain("declares no natural key");
    }

    /// <summary>
    ///     A conjoined lookup is keyed on <c>(tenant_id, natural_key_value)</c>, so the same business
    ///     identifier may exist once per tenant and must resolve to that tenant's stream in both
    ///     directions.
    /// </summary>
    [Fact]
    public async Task the_lookup_is_isolated_by_tenant()
    {
        await using var store = StoreFor(conjoined: true);
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var shire = await StartOrder("ORD-SHARED", store, "shire");
        var bree = await StartOrder("ORD-SHARED", store, "bree");

        shire.ShouldNotBe(bree);

        await using var shireSession = store.LightweightSession("shire");
        await using var breeSession = store.LightweightSession("bree");

        (await shireSession.Events.FetchForWritingByNaturalKey<Order, string>("ORD-SHARED", Token))
            .Id.ShouldBe(shire);
        (await breeSession.Events.FetchForWritingByNaturalKey<Order, string>("ORD-SHARED", Token))
            .Id.ShouldBe(bree);
    }

    /// <summary>
    ///     The lookup rows go with the event data they describe. Left behind, they would make the
    ///     duplicate guard fire on data that no longer exists — and the compliance fixture cleans
    ///     before every test, so this is the shape that would present as an unexplained failure two
    ///     tests later.
    /// </summary>
    [Fact]
    public async Task cleaning_the_event_data_clears_the_lookup()
    {
        await StartOrder("ORD-CLEANED");

        await _store.Advanced.Clean.DeleteAllEventDataAsync(Token);

        await using var session = _store.LightweightSession();

        await Should.ThrowAsync<UnknownNaturalKeyException>(() =>
            session.Events.FetchForWritingByNaturalKey<Order, string>("ORD-CLEANED", Token));

        // And the key is free again, which is the half a stale row would break.
        await Should.NotThrowAsync(() => StartOrder("ORD-CLEANED"));
    }

    [Fact]
    public async Task the_lookup_table_is_created_with_the_schema()
    {
        await using var session = _store.LightweightSession();

        var columns = await session.AdvancedSql.QueryAsync<string>(
            "select name from pragma_table_info('fi_natural_key_order')", Token);

        columns.ShouldBe(["natural_key_value", "stream_id"], ignoreOrder: true);
    }

    // ---- the model ----

    public record OrderPlaced(string Reference);

    public record OrderShipped;

    public class Order
    {
        public Guid Id { get; set; }

        [NaturalKey] public string Reference { get; set; } = "";

        public bool Shipped { get; set; }
    }

    public class Untracked
    {
        public Guid Id { get; set; }
    }

    /// <summary>
    ///     <c>[NaturalKeySource]</c> is what tells discovery which event carries the key. Without it the
    ///     definition has no event mappings and no row is ever written.
    /// </summary>
    public partial class OrderProjection : SingleStreamProjection<Order, Guid>
    {
        [NaturalKeySource]
        public static Order Create(OrderPlaced placed) => new() { Reference = placed.Reference };

        public static void Apply(OrderShipped _, Order order) => order.Shipped = true;
    }
}
