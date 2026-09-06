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

    /*
     * a_second_stream_cannot_take_an_existing_key retired in fisher#184. jasperfx#764 ruled FISHER'S
     * WAY — a second claimant is refused, where Polecat's MERGE repointed the key and left the
     * original stream unreachable by the identifier it was created with — so the behaviour this
     * pinned is now NaturalKeyCompliance.a_second_stream_cannot_claim_a_live_natural_key, which is
     * strictly stronger: it also reads the original mapping back afterwards.
     *
     * Fisher's DuplicateNaturalKeyException subclasses the lifted JasperFx.Events one (fisher#178),
     * which the suite catches, so the exception type is pinned by the same fact.
     */

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

    /*
     * the_lookup_is_isolated_by_tenant retired in fisher#184, superseded by
     * NaturalKeyCompliance.the_natural_key_lookup_is_isolated_by_tenant, which checks the same
     * property through the shared FetchForWriting<T, TId> overload in both directions.
     */

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

    // ---- the replay path (fisher#206) ----

    /*
     * These four are the whole reason the lookup moved from a session-side writer to an inline
     * projection with a daemon hook. Every one of them fails against the old shape by construction
     * rather than by accident: the writer drove off the unit of work's StreamActions, and a replay
     * appends no streams, so nothing it did was reachable from a rebuild.
     *
     * They need two stores over one file. The lookup table is created by the migration only when an
     * aggregate declaring a key is registered, so the "before" half has to be appended through a store
     * that does *not* register the projection — which is exactly the real situation being modelled:
     * streams that already existed when somebody declared a natural key on their aggregate.
     */

    /// <summary>
    ///     A natural key declared over history that already exists is backfilled by a rebuild.
    /// </summary>
    /// <remarks>
    ///     The capability the append path structurally could not have. Before the conversion the only
    ///     stream reachable by its key was one appended after the key was declared, so adopting natural
    ///     keys on a live store meant its existing streams were permanently unreachable by the
    ///     identifier they were created with.
    /// </remarks>
    [Fact]
    public async Task a_natural_key_is_backfilled_onto_streams_that_already_existed()
    {
        await using var asyncStore = StoreForAsyncProjection();
        await asyncStore.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var streamId = await AppendWithNoNaturalKeyDeclared("ORD-BACKFILL");

        // Nothing wrote a lookup row, because nothing on the append path knew there was a key.
        await using (var before = asyncStore.LightweightSession())
        {
            await Should.ThrowAsync<UnknownNaturalKeyException>(() =>
                before.Events.FetchForWritingByNaturalKey<Order, string>("ORD-BACKFILL", Token));
        }

        await RunDaemonToHeadAsync(asyncStore);

        await using var after = asyncStore.LightweightSession();
        var stream = await after.Events.FetchForWritingByNaturalKey<Order, string>("ORD-BACKFILL", Token);

        stream.Id.ShouldBe(streamId);
    }

    /// <summary>
    ///     A lookup emptied out from under the store is repopulated by a rebuild.
    /// </summary>
    /// <remarks>
    ///     Fisher's rebuild teardown does not clear the lookup — it sweeps mapped document types and
    ///     <c>IPublishesTables</c>, and the lookup is neither — so this plants the emptied state
    ///     directly rather than waiting for a teardown to produce it. That is deliberate: the property
    ///     worth having is "a replay can rebuild this table", not "teardown happens to leave it
    ///     alone", and the second is what a rebuild-then-fetch test would actually be asserting.
    /// </remarks>
    [Fact]
    public async Task a_rebuild_repopulates_a_lookup_that_was_emptied()
    {
        await using var asyncStore = StoreForAsyncProjection();
        await asyncStore.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var streamId = await StartOrder("ORD-EMPTIED", asyncStore);

        await using (var wipe = asyncStore.LightweightSession())
        {
            wipe.QueueSqlCommand("delete from fi_natural_key_order");
            await wipe.SaveChangesAsync(Token);
        }

        await using (var gone = asyncStore.LightweightSession())
        {
            await Should.ThrowAsync<UnknownNaturalKeyException>(() =>
                gone.Events.FetchForWritingByNaturalKey<Order, string>("ORD-EMPTIED", Token));
        }

        await RunDaemonToHeadAsync(asyncStore);

        await using var back = asyncStore.LightweightSession();
        (await back.Events.FetchForWritingByNaturalKey<Order, string>("ORD-EMPTIED", Token))
            .Id.ShouldBe(streamId);
    }

    /// <summary>
    ///     A replay is last-writer-wins and re-adjudicates nothing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The marten#4966 ruling, and the reason the two paths do not share one statement: a
    ///         rebuild that refused a duplicate would turn a pre-existing data condition into a shard
    ///         that can never advance again, with no caller present to correct the key derivation the
    ///         refusal blames.
    ///     </para>
    ///     <para>
    ///         Two streams carrying one key is not reachable through the append path — the second
    ///         append is refused and rolls its own events back — so the condition is planted the only
    ///         way it can occur in the wild: events written while no key was declared. The assertion is
    ///         that the daemon reaches the head, not what the lookup settles on.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_replay_does_not_re_adjudicate_a_duplicate_key()
    {
        await using var asyncStore = StoreForAsyncProjection();
        await asyncStore.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await AppendWithNoNaturalKeyDeclared("ORD-DOUBLE");
        var second = await AppendWithNoNaturalKeyDeclared("ORD-DOUBLE");

        await RunDaemonToHeadAsync(asyncStore);

        await using var session = asyncStore.LightweightSession();

        // Last writer wins, so the later stream owns it — and, more to the point, the daemon got here.
        (await session.Events.FetchForWritingByNaturalKey<Order, string>("ORD-DOUBLE", Token))
            .Id.ShouldBe(second);
    }

    /// <summary>
    ///     A replay does not bring an archived stream's key back, and it is doubly safe from doing so.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The first guard is the daemon's, and it was not designed for this.</b> An event
    ///         loader excludes an archived stream's events unless a shard asks for them
    ///         (<c>IncludeArchivedEvents</c>), so the replay never sees the stream at all and writes no
    ///         row — which is why the assertion below is that the table stays empty rather than that
    ///         the row comes back rewritten. Worth pinning as behaviour rather than left implicit,
    ///         because a shard that <em>does</em> include archived events would put the row back and
    ///         nothing about the lookup would notice.
    ///     </para>
    ///     <para>
    ///         <b>And the second guard is what makes that harmless.</b> Fisher's lookup carries no
    ///         <c>is_archived</c> column — the flag is read off the join to <c>fi_streams</c> — so a
    ///         row's presence says nothing about whether the key resolves. Polecat and Marten keep the
    ///         flag on the row, where a replay that rewrote it would have to be careful; here there is
    ///         nothing to be careful about.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_replay_cannot_resurrect_an_archived_streams_key()
    {
        await using var asyncStore = StoreForAsyncProjection();
        await asyncStore.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        var streamId = await StartOrder("ORD-ARCHIVED-REPLAY", asyncStore);

        await using (var archive = asyncStore.LightweightSession())
        {
            archive.Events.ArchiveStream(streamId);
            await archive.SaveChangesAsync(Token);
        }

        await using (var wipe = asyncStore.LightweightSession())
        {
            wipe.QueueSqlCommand("delete from fi_natural_key_order");
            await wipe.SaveChangesAsync(Token);
        }

        await RunDaemonToHeadAsync(asyncStore);

        await using var session = asyncStore.LightweightSession();

        // The replay never saw the archived stream, so nothing was written back...
        var rows = await session.AdvancedSql.QueryAsync<string>(
            "select natural_key_value from fi_natural_key_order", Token);
        rows.ShouldBeEmpty();

        // ...and the key resolves to nothing either way, because the answer comes off fi_streams.
        await Should.ThrowAsync<UnknownNaturalKeyException>(() =>
            session.Events.FetchForWritingByNaturalKey<Order, string>("ORD-ARCHIVED-REPLAY", Token));
    }

    /// <summary>
    ///     The append path's refusal is still a property of the statement, not of a read before it.
    /// </summary>
    /// <remarks>
    ///     marten#5349's property, carried through the conversion: a probing SELECT would race, so two
    ///     sessions could both find the key free and the loser's upsert would repoint the row exactly
    ///     as an unguarded one does. The behavioural half is
    ///     <c>NaturalKeyCompliance.a_second_stream_cannot_claim_a_live_natural_key</c>, which reads the
    ///     original mapping back after the throw; this asserts the mechanism, because a store could
    ///     pass that fact with a pre-flight read and be wrong only under contention.
    /// </remarks>
    [Fact]
    public void the_claim_refuses_in_the_statement_rather_than_after_a_read()
    {
        var operation = new Fisher.Events.Storage.NaturalKeyClaimOperation(_store.Options.EventGraph,
            typeof(Order), "ORD-SQL", Guid.NewGuid().ToString("D"), "*DEFAULT*");

        var builder = new Weasel.Sqlite.CommandBuilder();
        operation.ConfigureCommand(builder, (Fisher.Internal.FisherSession)_store.LightweightSession());

        var sql = builder.Compile().CommandText;

        // The guard is on the conflict clause, and the statement reports what it settled on.
        sql.ShouldContain("on conflict do update set");
        sql.ShouldContain("where fi_natural_key_order.stream_id = excluded.stream_id");
        sql.ShouldContain("returning stream_id");

        // And nothing reads first — the whole command is the write and the retirement beside it.
        sql.ShouldNotContain("select");
    }

    // ---- helpers for the replay tests ----

    /// <summary>
    ///     A second store over the same file with the projection registered <c>Async</c>, so there is a
    ///     shard for the daemon to run and therefore pages for the replay hook to see.
    /// </summary>
    private DocumentStore StoreForAsyncProjection()
        => DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
            o.Projections.Add(new OrderProjection(), ProjectionLifecycle.Async);
        });

    /// <summary>
    ///     Append through a store that declares no natural key at all, which is the only way to produce
    ///     events whose keys were never claimed.
    /// </summary>
    private async Task<Guid> AppendWithNoNaturalKeyDeclared(string reference)
    {
        await using var bare = DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
        });

        var streamId = Guid.NewGuid();

        await using var session = bare.LightweightSession();
        session.Events.StartStream<Order>(streamId, new OrderPlaced(reference));
        await session.SaveChangesAsync(Token);

        return streamId;
    }

    private static async Task RunDaemonToHeadAsync(DocumentStore store)
    {
        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();
        await store.Database.WaitForNonStaleProjectionDataAsync(TimeSpan.FromSeconds(30));
        await daemon.StopAllAsync();
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
