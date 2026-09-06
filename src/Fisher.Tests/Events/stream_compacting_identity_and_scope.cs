using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Protected;

namespace Fisher.Tests.Events;

/// <summary>
///     <see cref="Purse" />'s twin for a string-identified store — a single stream aggregate takes its
///     identity from the stream, so the two have to agree on the type.
/// </summary>
public class Satchel
{
    public string Id { get; set; } = string.Empty;
    public int Balance { get; set; }

    public void Apply(CoinsEarned earned) => Balance += earned.Amount;

    public void Apply(CoinsSpent spent) => Balance -= spent.Amount;
}

/// <summary>
///     polecat#546 item 3 / marten#5244 — compaction must refuse an overload that disagrees with the
///     store's stream identity rather than silently compacting nothing, and must never reach a stream
///     other than the one it was given.
/// </summary>
/// <remarks>
///     <para>
///         Polecat's <c>StreamCompactingExecution</c> branched on the store's configured
///         <c>StreamIdentity</c> and never checked which overload the caller had used. The Guid
///         overload against a string-identified store took the AsString branch, read a null
///         <c>StreamKey</c>, matched no stream, and <b>returned successfully having compacted
///         nothing</b> — indistinguishable from a completed compaction. Fisher's <c>FetchAsync</c>
///         branches the same way, so the same mistake would produce the same silence; what stops it is
///         the <c>AssertGuidIdentity</c>/<c>AssertStringIdentity</c> pair on the entry points, and
///         these tests are what hold that pair in place.
///     </para>
///     <para>
///         The untyped <c>IEventStore</c> overloads are covered too. They reach the assertion by
///         delegation — through <c>FetchStreamStateAsync</c> — which is a real guarantee but an
///         indirect one, and indirect guarantees are the ones that quietly disappear during a
///         refactor.
///     </para>
///     <para>
///         The last two tests cover the within-tenant half of the scoping question that
///         <see cref="cross_tenant_protected_operations" /> answers across tenants: compacting stream A
///         must leave stream B alone, both its events and its compaction watermark.
///     </para>
/// </remarks>
public class stream_compacting_identity_and_scope : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("compact-identity");

    private DocumentStore _guidStore = null!;
    private DocumentStore _stringStore = null!;

    public async ValueTask InitializeAsync()
    {
        _guidStore = StoreFor("guids", StreamIdentity.AsGuid);
        _stringStore = StoreFor("keys", StreamIdentity.AsString);

        await _guidStore.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        await _stringStore.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _guidStore.DisposeAsync();
        await _stringStore.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- identity mismatch on the typed overloads ----

    /// <summary>
    ///     Polecat's exact defect: this combination silently compacted nothing there.
    /// </summary>
    [Fact]
    public async Task the_guid_overload_is_refused_by_a_string_identified_store()
    {
        await using var session = _stringStore.LightweightSession();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            session.Events.CompactStreamAsync<Satchel>(Guid.NewGuid()));

        ex.Message.ShouldContain("StreamIdentity.AsString");
        ex.Message.ShouldContain("string stream key overloads");
    }

    /// <summary>
    ///     The mirror. In Polecat this threw "Nullable object must have a value", which names nothing
    ///     the caller can act on.
    /// </summary>
    [Fact]
    public async Task the_string_overload_is_refused_by_a_guid_identified_store()
    {
        await using var session = _guidStore.LightweightSession();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            session.Events.CompactStreamAsync<Purse>("some-key"));

        ex.Message.ShouldContain("StreamIdentity.AsGuid");
        ex.Message.ShouldContain("Guid stream id overloads");
    }

    // ---- identity mismatch on the untyped tooling entry point ----

    [Fact]
    public async Task the_untyped_guid_entry_point_is_refused_by_a_string_identified_store()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            ((IEventStore)_stringStore).CompactStreamAsync(Guid.NewGuid(), Token));

        ex.Message.ShouldContain("StreamIdentity.AsString");
    }

    [Fact]
    public async Task the_untyped_string_entry_point_is_refused_by_a_guid_identified_store()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            ((IEventStore)_guidStore).CompactStreamAsync("some-key", Token));

        ex.Message.ShouldContain("StreamIdentity.AsGuid");
    }

    /// <summary>
    ///     The refusal has to be a refusal, not a refusal-shaped no-op: a store with the matching
    ///     identity still compacts.
    /// </summary>
    [Fact]
    public async Task a_string_identified_store_still_compacts_through_its_own_overload()
    {
        const string streamKey = "purse/frodo";

        await using (var session = _stringStore.LightweightSession())
        {
            session.Events.StartStream<Satchel>(streamKey, new CoinsEarned(100), new CoinsSpent(30));
            await session.SaveChangesAsync(Token);
        }

        await using (var session = _stringStore.LightweightSession())
        {
            await session.Events.CompactStreamAsync<Satchel>(streamKey);
        }

        await using var query = _stringStore.LightweightSession();
        var events = await query.Events.FetchStreamAsync(streamKey, token: Token);

        events.ShouldHaveSingleItem().Data.ShouldBeOfType<Compacted<Satchel>>().Snapshot.Balance.ShouldBe(70);
    }

    // ---- stream scoping within one tenant ----

    [Fact]
    public async Task compacting_one_stream_leaves_a_neighbouring_stream_untouched()
    {
        var compacted = Guid.NewGuid();
        var neighbour = Guid.NewGuid();

        await StartGuidStreamAsync(compacted, new CoinsEarned(100), new CoinsSpent(30), new CoinsEarned(5));
        await StartGuidStreamAsync(neighbour, new CoinsEarned(7), new CoinsSpent(2), new CoinsEarned(1));

        await using (var session = _guidStore.LightweightSession())
        {
            await session.Events.CompactStreamAsync<Purse>(compacted);
        }

        await using var query = _guidStore.LightweightSession();

        (await query.Events.FetchStreamAsync(compacted, token: Token))
            .ShouldHaveSingleItem().Data.ShouldBeOfType<Compacted<Purse>>().Snapshot.Balance.ShouldBe(75);

        var untouched = await query.Events.FetchStreamAsync(neighbour, token: Token);
        untouched.Count.ShouldBe(3);
        untouched.ShouldAllBe(x => x.Data is CoinsEarned || x.Data is CoinsSpent);
    }

    /// <summary>
    ///     The watermark half of the same question — a stream nobody compacted must still read zero.
    /// </summary>
    [Fact]
    public async Task the_compaction_watermark_lands_only_on_the_compacted_stream()
    {
        var compacted = Guid.NewGuid();
        var neighbour = Guid.NewGuid();

        await StartGuidStreamAsync(compacted, new CoinsEarned(100), new CoinsSpent(30));
        await StartGuidStreamAsync(neighbour, new CoinsEarned(7), new CoinsSpent(2));

        await using (var session = _guidStore.LightweightSession())
        {
            await session.Events.CompactStreamAsync<Purse>(compacted);
        }

        await using var query = _guidStore.LightweightSession();

        (await query.Events.FetchStreamStateAsync(compacted, Token))!.CompactedVersion.ShouldBe(2);
        (await query.Events.FetchStreamStateAsync(neighbour, Token))!.CompactedVersion.ShouldBe(0);
    }

    // ---- helpers ----

    private DocumentStore StoreFor(string schema, StreamIdentity identity)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.DatabaseSchemaName = schema;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.StreamIdentity = identity;
            options.Events.AddEventType(typeof(CoinsEarned));
            options.Events.AddEventType(typeof(CoinsSpent));
        });

    private async Task StartGuidStreamAsync(Guid streamId, params object[] events)
    {
        await using var session = _guidStore.LightweightSession();
        session.Events.StartStream<Purse>(streamId, events);
        await session.SaveChangesAsync(Token);
    }
}
