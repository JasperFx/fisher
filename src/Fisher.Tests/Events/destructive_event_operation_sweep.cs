using Fisher.Tests.Schema;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Tags;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Events;

/// <summary>
///     polecat#546 item 4 — the rest of the destructive event surface, checked the same way as the
///     compaction and masking operations: every statement that deletes or overwrites a row is asked to
///     prove its predicates reach exactly the stream and tenant that was named.
/// </summary>
/// <remarks>
///     <para>
///         <c>ArchiveStream</c> and <c>TombstoneStream</c> live outside
///         <c>Fisher.Events.Protected</c>, but they are the two other operations that write across
///         whole streams, and <c>TombstoneStream</c> is the only one that hard-deletes
///         <c>fi_events</c> rows outside compaction. Their predicates are
///         <c>id/stream_id = @id and tenant_id = @tenant_id</c> unconditionally — not gated on
///         <c>TenancyStyle.Conjoined</c>, which is safe because a single-tenant store still writes the
///         default tenant into the column.
///     </para>
///     <para>
///         <b>The tag-ordering test is the one with teeth.</b> <c>fi_event_tag_*</c> carries a real,
///         enforced foreign key to <c>fi_events(seq_id)</c>, so any statement deleting events has to
///         delete tag rows first — a lesson <c>DeleteEventsOperation</c> and
///         <c>DeleteAllEventDataAsync</c> both record in their remarks. Tombstoning is the third
///         operation in that family and had no test saying it learned it.
///     </para>
/// </remarks>
public class destructive_event_operation_sweep : IAsyncLifetime
{
    private const string Alpha = "alpha";
    private const string Beta = "beta";

    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("destructive-sweep");
    private readonly TerritoryId _shire = new(Guid.NewGuid());

    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Events.RegisterTagType<TerritoryId>("territory");
            options.Events.AddEventType(typeof(CoinsEarned));
            options.Events.AddEventType(typeof(CoinsSpent));
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    ///     The enforced foreign key from the tag table to <c>fi_events(seq_id)</c> means the tag rows
    ///     have to go first, exactly as they do in <c>DeleteEventsOperation</c>.
    /// </summary>
    [Fact]
    public async Task tombstoning_a_tagged_stream_clears_its_tag_rows_too()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession(Alpha))
        {
            var @event = session.Events.BuildEvent(new CoinsEarned(100));
            @event.WithTag(_shire);
            session.Events.StartStream<Purse>(streamId, @event);
            await session.SaveChangesAsync(Token);
        }

        await using (var session = _store.LightweightSession(Alpha))
        {
            session.Events.TombstoneStream(streamId);
            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession(Alpha);

        (await query.Events.FetchStreamAsync(streamId, token: Token)).ShouldBeEmpty();

        // The tag row cannot outlive the event it points at — the foreign key would not allow it, and
        // a tag query answering for a deleted event is the failure this asserts against.
        (await query.Events.QueryByTagsAsync(new EventTagQuery().Or<TerritoryId>(_shire), Token))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task tombstoning_one_tenants_stream_leaves_the_other_tenants_alone()
    {
        var streamId = Guid.NewGuid();

        await StartAsync(Alpha, streamId, new CoinsEarned(100), new CoinsSpent(30));
        await StartAsync(Beta, streamId, new CoinsEarned(7), new CoinsSpent(2));

        await using (var session = _store.LightweightSession(Alpha))
        {
            session.Events.TombstoneStream(streamId);
            await session.SaveChangesAsync(Token);
        }

        await using var alpha = _store.LightweightSession(Alpha);
        await using var beta = _store.LightweightSession(Beta);

        (await alpha.Events.FetchStreamAsync(streamId, token: Token)).ShouldBeEmpty();
        (await beta.Events.FetchStreamAsync(streamId, token: Token)).Count.ShouldBe(2);

        // The streams row went with it for alpha, and stayed for beta.
        (await alpha.Events.FetchStreamStateAsync(streamId, Token)).ShouldBeNull();
        (await beta.Events.FetchStreamStateAsync(streamId, Token)).ShouldNotBeNull();
    }

    [Fact]
    public async Task archiving_one_tenants_stream_leaves_the_other_tenants_alone()
    {
        var streamId = Guid.NewGuid();

        await StartAsync(Alpha, streamId, new CoinsEarned(100));
        await StartAsync(Beta, streamId, new CoinsEarned(7));

        await using (var session = _store.LightweightSession(Alpha))
        {
            session.Events.ArchiveStream(streamId);
            await session.SaveChangesAsync(Token);
        }

        await using var alpha = _store.LightweightSession(Alpha);
        await using var beta = _store.LightweightSession(Beta);

        (await alpha.Events.FetchStreamStateAsync(streamId, Token))!.IsArchived.ShouldBeTrue();
        (await beta.Events.FetchStreamStateAsync(streamId, Token))!.IsArchived.ShouldBeFalse();
    }

    private async Task StartAsync(string tenantId, Guid streamId, params object[] events)
    {
        await using var session = _store.LightweightSession(tenantId);
        session.Events.StartStream<Purse>(streamId, events);
        await session.SaveChangesAsync(Token);
    }
}
