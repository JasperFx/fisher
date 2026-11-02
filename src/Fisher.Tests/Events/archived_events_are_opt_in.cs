// Deliberately NOT importing the Fisher.Linq namespace, for the reason stream_state_queries gives: the
// shared JasperFx.Events.Documents terminators are the ones the read-only queryables execute through.
using Fisher.Batching;
using Fisher.Events.Archiving;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Documents;
using JasperFx.Events.Projections;
using JasperFx.Events.Protected;

namespace Fisher.Tests.Events;

/// <summary>A body with an <c>IsArchived</c> of its own, which says nothing about the row.</summary>
public record CaseFiled(string Reference, bool IsArchived);

/// <summary>
///     Archived events are left out of every event read unless the caller opts in, as on Marten and
///     Polecat. Fisher used to return them.
/// </summary>
/// <remarks>
///     Every default read is asked about a stream that has live events beside it, so a read that
///     returned nothing at all would still fail.
/// </remarks>
public class archived_events_are_opt_in : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("archived-opt-in");
    private readonly CountingAggregateWriteCache _cache = new();
    private DocumentStore _store = null!;
    private Guid _archived;
    private Guid _live;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IReadOnlyEventStore Reader => ((IEventStore)_store).OpenReadOnlyEventStore();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            options.Events.AddEventType(typeof(CustomerRegistered));
            options.Events.AddEventType(typeof(CoinsEarned));
            options.Events.AddEventType(typeof(CoinsSpent));
            options.Events.AddEventType(typeof(CaseFiled));

            options.Events.AddMaskingRuleForProtectedInformation<CustomerRegistered>(
                x => x with { Name = "REDACTED", Email = "REDACTED" });

            options.Projections.Snapshot<BarTab>(SnapshotLifecycle.Inline);
            options.Events.AggregateWriteCaching.Cache = _cache;
            options.Events.CacheAggregatesForWriting<BarTab>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        _archived = await aStreamAsync("Find the ring");
        _live = await aStreamAsync("Guard the shire");
        await archiveAsync(_archived);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<Guid> aStreamAsync(string name)
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Events.StartStream<Quest>(streamId, new QuestStarted(name), new MemberJoined("Frodo"));
        await session.SaveChangesAsync(Token);

        return streamId;
    }

    private async Task<Guid> aTabAsync()
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Events.StartStream<BarTab>(streamId, new TabOpened("Hilda"), new TabCharged(100));
        await session.SaveChangesAsync(Token);

        return streamId;
    }

    private async Task archiveAsync(Guid streamId)
    {
        await using var session = _store.LightweightSession();
        session.Events.ArchiveStream(streamId);
        await session.SaveChangesAsync(Token);
    }

    private async Task unarchiveAsync(Guid streamId)
    {
        await using var session = _store.LightweightSession();
        session.Events.UnArchiveStream(streamId);
        await session.SaveChangesAsync(Token);
    }

    private async Task<IReadOnlyList<IEvent>> everyEventOfAsync(Guid streamId)
    {
        await using var query = _store.LightweightSession();
        return await query.Events.QueryEventsAsync(x => x.MaybeArchived() && x.StreamId == streamId, Token);
    }

    // ---- the default reads ----

    [Fact]
    public async Task fetching_a_stream_leaves_them_out()
    {
        await using var query = _store.LightweightSession();

        (await query.Events.FetchStreamAsync(_archived, token: Token)).ShouldBeEmpty();
        (await query.Events.FetchStreamAsync(_live, token: Token)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task fetching_a_string_identified_stream_leaves_them_out()
    {
        using var database = TemporaryDatabase.Create("archived-opt-in-string");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.StreamIdentity = StreamIdentity.AsString;
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = store.LightweightSession();
        session.Events.StartStream<Quest>("archived", new QuestStarted("Find the ring"));
        session.Events.StartStream<Quest>("live", new QuestStarted("Guard the shire"));
        await session.SaveChangesAsync(Token);

        session.Events.ArchiveStream("archived");
        await session.SaveChangesAsync(Token);

        (await session.Events.FetchStreamAsync("archived", token: Token)).ShouldBeEmpty();
        (await session.Events.FetchStreamAsync("live", token: Token)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task the_read_only_tier_leaves_them_out()
        => (await Reader.FetchStreamAsync(_archived, token: Token)).ShouldBeEmpty();

    [Fact]
    public async Task a_batched_stream_read_leaves_them_out()
    {
        await using var query = _store.LightweightSession();

        (await new FetchStreamPlan(_archived).Fetch(query, Token)).ShouldBeEmpty();
    }

    [Fact]
    public async Task loading_one_of_them_by_id_still_finds_it()
    {
        var @event = (await everyEventOfAsync(_archived))[0];

        await using var query = _store.LightweightSession();

        var loaded = await query.Events.LoadAsync(@event.Id, Token);

        loaded.ShouldNotBeNull();
        loaded.IsArchived.ShouldBeTrue();
    }

    [Fact]
    public async Task live_aggregation_folds_to_nothing()
    {
        var streamId = await aTabAsync();
        await archiveAsync(streamId);

        await using var query = _store.LightweightSession();

        (await query.Events.AggregateStreamAsync<BarTab>(streamId, token: Token)).ShouldBeNull();
        (await query.Events.AggregateStreamToLastKnownAsync<BarTab>(streamId, token: Token)).ShouldBeNull();
    }

    [Fact]
    public async Task live_aggregation_hands_back_the_state_it_was_given()
    {
        var streamId = await aTabAsync();
        await archiveAsync(streamId);

        var state = new BarTab { Owner = "Hilda" };

        await using var query = _store.LightweightSession();

        (await query.Events.AggregateStreamAsync(streamId, state: state, token: Token)).ShouldBeSameAs(state);
    }

    [Fact]
    public async Task a_metadata_predicate_leaves_them_out()
    {
        await using var query = _store.LightweightSession();

        var events = await query.Events.QueryEventsAsync(x => x.Version == 1, Token);

        events.ShouldHaveSingleItem().StreamId.ShouldBe(_live);
    }

    [Fact]
    public async Task an_either_or_predicate_cannot_step_around_the_filter()
    {
        await using var query = _store.LightweightSession();

        var events = await query.Events.QueryEventsAsync(
            x => x.StreamId == _archived || x.StreamId == _live, Token);

        events.ShouldAllBe(x => x.StreamId == _live);
    }

    [Fact]
    public async Task an_event_query_leaves_them_out_of_the_page_and_the_count()
    {
        var page = await Reader.QueryEventsAsync(new EventQuery());

        page.Events.ShouldAllBe(x => x.StreamId == _live);
        page.TotalCount.ShouldBe(2);

        var named = await Reader.QueryEventsAsync(new EventQuery { StreamId = _archived.ToString() });

        named.Events.ShouldBeEmpty();
        named.TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task an_event_body_predicate_leaves_them_out()
    {
        await using var query = _store.LightweightSession();

        var started = await query.Events.QueryEventDataAsync<QuestStarted>(x => x.Name != "", Token);

        started.ShouldHaveSingleItem().Name.ShouldBe("Guard the shire");
    }

    [Fact]
    public async Task the_event_data_queryable_leaves_them_out()
    {
        var started = Reader.QueryEventData<QuestStarted>();

        (await started.ToListAsync(Token)).ShouldHaveSingleItem().Name.ShouldBe("Guard the shire");
        (await started.CountAsync(Token)).ShouldBe(1);
        (await started.Where(x => x.Name == "Find the ring").AnyAsync(Token)).ShouldBeFalse();
    }

    [Fact]
    public async Task the_raw_event_queryable_leaves_them_out()
    {
        var events = Reader.QueryRawEvents();

        (await events.ToListAsync(Token)).ShouldAllBe(x => x.StreamId == _live);
        (await events.CountAsync(Token)).ShouldBe(2);
        (await events.Where(x => x.StreamId == _archived).AnyAsync(Token)).ShouldBeFalse();
    }

    // ---- what opts in ----

    [Fact]
    public async Task maybe_archived_brings_them_back_flagged()
    {
        var events = await everyEventOfAsync(_archived);

        events.Count.ShouldBe(2);
        events.ShouldAllBe(x => x.IsArchived);
    }

    [Fact]
    public async Task maybe_archived_reads_live_and_archived_events_together()
    {
        await using var query = _store.LightweightSession();

        var events = await query.Events.QueryEventsAsync(x => x.MaybeArchived(), Token);

        events.Count.ShouldBe(4);
    }

    [Fact]
    public async Task naming_is_archived_reads_only_the_archived_events()
    {
        await using var query = _store.LightweightSession();

        var events = await query.Events.QueryEventsAsync(x => x.IsArchived, Token);

        events.Count.ShouldBe(2);
        events.ShouldAllBe(x => x.StreamId == _archived && x.IsArchived);
    }

    [Fact]
    public async Task naming_is_not_archived_reads_only_the_live_events()
    {
        await using var query = _store.LightweightSession();

        (await query.Events.QueryEventsAsync(x => !x.IsArchived, Token)).ShouldAllBe(x => x.StreamId == _live);
        (await query.Events.QueryEventsAsync(x => x.IsArchived == false, Token))
            .ShouldAllBe(x => x.StreamId == _live);
    }

    /// <summary>
    ///     Anywhere in the predicate counts, as it does on Marten: the default filter would otherwise
    ///     sit beside this and take away the half the caller asked for.
    /// </summary>
    [Fact]
    public async Task naming_is_archived_on_one_side_of_an_or_still_opts_in()
    {
        await using var query = _store.LightweightSession();

        var events = await query.Events.QueryEventsAsync(x => x.IsArchived || x.Version == 1, Token);

        events.Count.ShouldBe(3);
    }

    [Fact]
    public async Task the_raw_event_queryable_opts_in_the_same_way()
    {
        var events = Reader.QueryRawEvents();

        (await events.Where(x => x.MaybeArchived()).CountAsync(Token)).ShouldBe(4);
        (await events.Where(x => x.IsArchived).CountAsync(Token)).ShouldBe(2);
        (await events.Where(x => !x.IsArchived).CountAsync(Token)).ShouldBe(2);

        // A later Where is the same query, however the caller split it up.
        var composed = events.Where(x => x.MaybeArchived()).Where(x => x.StreamId == _archived);
        (await composed.ToListAsync(Token)).Count.ShouldBe(2);
    }

    /// <summary>A body's own member says nothing about the row. Only <c>IEvent</c>'s does.</summary>
    [Fact]
    public async Task a_body_member_called_is_archived_does_not_opt_in()
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Events.StartStream<Quest>(streamId, new CaseFiled("A-1", true));
        await session.SaveChangesAsync(Token);

        session.Events.ArchiveStream(streamId);
        await session.SaveChangesAsync(Token);

        (await Reader.QueryEventData<CaseFiled>().Where(x => x.IsArchived).AnyAsync(Token)).ShouldBeFalse();
        (await session.Events.QueryEventDataAsync<CaseFiled>(x => x.IsArchived, Token)).ShouldBeEmpty();
    }

    /// <summary>An event in hand that happens to be archived is a value, not a statement about the rows.</summary>
    [Fact]
    public async Task a_captured_event_s_flag_does_not_opt_in()
    {
        var captured = (await everyEventOfAsync(_archived))[0];
        var version = captured.IsArchived ? 1L : 2L;

        await using var query = _store.LightweightSession();

        var events = await query.Events.QueryEventsAsync(
            x => x.Version == (captured.IsArchived ? version : 2L), Token);

        events.ShouldHaveSingleItem().StreamId.ShouldBe(_live);
    }

    [Fact]
    public async Task the_stream_state_queryable_keeps_its_archived_rows()
    {
        var states = Reader.QueryStreamStates();

        (await states.CountAsync(Token)).ShouldBe(2);
        (await states.Where(x => x.IsArchived).ToListAsync(Token)).ShouldHaveSingleItem().Id.ShouldBe(_archived);
    }

    // ---- reads built on the default ----

    /// <summary>
    ///     The version survives, because the guard on the append needs it, and the commit is what
    ///     refuses. Only the aggregate goes.
    /// </summary>
    [Fact]
    public async Task fetching_for_writing_finds_no_aggregate_and_still_refuses_the_append()
    {
        var streamId = await aTabAsync();
        await archiveAsync(streamId);

        await using var session = _store.LightweightSession();

        var stream = await session.Events.FetchForWriting<BarTab>(streamId, Token);

        stream.Aggregate.ShouldBeNull();
        stream.StartingVersion.ShouldBe(2);

        stream.AppendOne(new TabCharged(10));

        await Should.ThrowAsync<ArchivedStreamException>(() => session.SaveChangesAsync(Token));
    }

    [Fact]
    public async Task fetching_many_for_writing_finds_no_aggregate_for_an_archived_stream()
    {
        var archived = await aTabAsync();
        var live = await aTabAsync();
        await archiveAsync(archived);

        await using var session = _store.LightweightSession();

        var streams = await session.Events.FetchManyForWriting<BarTab>([archived, live], Token);

        streams[0].Aggregate.ShouldBeNull();
        streams[0].StartingVersion.ShouldBe(2);
        streams[1].Aggregate.ShouldNotBeNull().Balance.ShouldBe(100);
    }

    /// <summary>
    ///     The trap in filtering the fold. A cached baseline behind the stream reads no delta once the
    ///     delta is archived, and the baseline would come back labelled with the stream's version, and
    ///     be written back under it, so the events in between would never be folded after an
    ///     un-archive. Polecat discards the baseline for this reason.
    /// </summary>
    [Fact]
    public async Task a_cached_baseline_behind_an_archived_stream_is_not_handed_out()
    {
        var streamId = await aTabAsync();

        // The write-back caches the aggregate at version 2, behind the commit this makes.
        await using (var session = _store.LightweightSession())
        {
            var stream = await session.Events.FetchForWriting<BarTab>(streamId, Token);
            stream.AppendOne(new TabCharged(10));
            await session.SaveChangesAsync(Token);
        }

        await archiveAsync(streamId);

        await using (var session = _store.LightweightSession())
        {
            (await session.Events.FetchForWriting<BarTab>(streamId, Token)).Aggregate.ShouldBeNull();
        }

        await using (var session = _store.LightweightSession())
        {
            var streams = await session.Events.FetchManyForWriting<BarTab>([streamId], Token);
            streams[0].Aggregate.ShouldBeNull();
        }

        await unarchiveAsync(streamId);

        await using var query = _store.LightweightSession();

        var restored = await query.Events.FetchForWriting<BarTab>(streamId, Token);
        restored.Aggregate.ShouldNotBeNull().Balance.ShouldBe(110);
    }

    /// <summary>
    ///     What a rebuild leaves behind, since the daemon never replays an archived stream.
    /// </summary>
    [Fact]
    public async Task rebuilding_a_single_stream_removes_the_document()
    {
        var streamId = await aTabAsync();
        await archiveAsync(streamId);

        await _store.Advanced.RebuildSingleStreamAsync<BarTab>(streamId, token: Token);

        await using var query = _store.LightweightSession();

        (await query.LoadAsync<BarTab>(streamId, Token)).ShouldBeNull();
    }

    // ---- what has to keep reaching them ----

    private async Task<Guid> anArchivedCustomerAsync()
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        session.Events.StartStream(streamId, new CustomerRegistered("Frodo", "frodo@shire.test"));
        await session.SaveChangesAsync(Token);

        await archiveAsync(streamId);

        return streamId;
    }

    private async Task theCustomerShouldBeMaskedAsync(Guid streamId)
        => ((CustomerRegistered)(await everyEventOfAsync(streamId))[0].Data).Name.ShouldBe("REDACTED");

    private async Task theCustomerShouldNotBeMaskedAsync(Guid streamId)
        => ((CustomerRegistered)(await everyEventOfAsync(streamId))[0].Data).Name.ShouldBe("Frodo");

    [Fact]
    public async Task masking_an_included_stream_reaches_them()
    {
        var streamId = await anArchivedCustomerAsync();

        await _store.Advanced.ApplyEventDataMaskingAsync(x => x.IncludeStream(streamId), Token);

        await theCustomerShouldBeMaskedAsync(streamId);
    }

    [Fact]
    public async Task masking_a_filtered_stream_reaches_them()
    {
        var streamId = await anArchivedCustomerAsync();

        await _store.Advanced.ApplyEventDataMaskingAsync(
            x => x.IncludeStream(streamId, e => e.Version == 1), Token);

        await theCustomerShouldBeMaskedAsync(streamId);
    }

    [Fact]
    public async Task masking_a_string_identified_stream_reaches_them()
    {
        using var database = TemporaryDatabase.Create("archived-opt-in-mask-string");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.StreamIdentity = StreamIdentity.AsString;
            options.Events.AddMaskingRuleForProtectedInformation<CustomerRegistered>(
                x => x with { Name = "REDACTED" });
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = store.LightweightSession();
        session.Events.StartStream("frodo", new CustomerRegistered("Frodo", "frodo@shire.test"));
        await session.SaveChangesAsync(Token);

        session.Events.ArchiveStream("frodo");
        await session.SaveChangesAsync(Token);

        await store.Advanced.ApplyEventDataMaskingAsync(x => x.IncludeStream("frodo"), Token);
        await store.Advanced.ApplyEventDataMaskingAsync(
            x => x.IncludeStream("frodo", e => e.Version == 1), Token);

        var events = await session.Events.QueryEventsAsync(x => x.MaybeArchived(), Token);
        ((CustomerRegistered)events.ShouldHaveSingleItem().Data).Name.ShouldBe("REDACTED");
    }

    [Fact]
    public async Task an_event_filter_reaches_them()
    {
        var streamId = await anArchivedCustomerAsync();

        await _store.Advanced.ApplyEventDataMaskingAsync(x => x.IncludeEvents(e => e.StreamId == streamId), Token);

        await theCustomerShouldBeMaskedAsync(streamId);
    }

    /// <summary>The filter is still the caller's, so it can still rule them back out.</summary>
    [Fact]
    public async Task an_event_filter_that_names_is_archived_still_narrows()
    {
        var streamId = await anArchivedCustomerAsync();

        await _store.Advanced.ApplyEventDataMaskingAsync(
            x => x.IncludeEvents(e => !e.IsArchived && e.StreamId == streamId), Token);

        await theCustomerShouldNotBeMaskedAsync(streamId);

        await _store.Advanced.ApplyEventDataMaskingAsync(
            x => x.IncludeEvents(e => e.IsArchived && e.StreamId == streamId), Token);

        await theCustomerShouldBeMaskedAsync(streamId);
    }

    /// <summary>
    ///     The read that reaches archived rows is not a way around the identity check, or a Guid on a
    ///     string store would match nothing and the pass would report success. marten#5797 measured
    ///     exactly that.
    /// </summary>
    [Fact]
    public async Task naming_a_stream_with_the_wrong_identity_type_is_still_refused()
    {
        await Should.ThrowAsync<Exception>(() => _store.Advanced.ApplyEventDataMaskingAsync(
            x => x.IncludeStream("not-a-guid"), Token));
    }

    [Fact]
    public async Task compacting_reaches_them()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(streamId, new CoinsEarned(100), new CoinsSpent(30), new CoinsEarned(5));
            await session.SaveChangesAsync(Token);
        }

        await archiveAsync(streamId);

        await using (var session = _store.LightweightSession())
        {
            await session.Events.CompactStreamAsync<Purse>(streamId);
        }

        var events = await everyEventOfAsync(streamId);
        var snapshot = events.ShouldHaveSingleItem();

        snapshot.IsArchived.ShouldBeTrue();
        ((Compacted<Purse>)snapshot.Data).Snapshot.Balance.ShouldBe(75);

        await unarchiveAsync(streamId);

        await using var query = _store.LightweightSession();

        (await query.Events.AggregateStreamAsync<Purse>(streamId, token: Token)).ShouldNotBeNull().Balance.ShouldBe(75);
    }

    [Fact]
    public async Task tombstoning_removes_them()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Events.TombstoneStream(_archived);
            await session.SaveChangesAsync(Token);
        }

        (await everyEventOfAsync(_archived)).ShouldBeEmpty();

        await using var query = _store.LightweightSession();

        (await query.Events.FetchStreamStateAsync(_archived, Token)).ShouldBeNull();
    }

    [Fact]
    public async Task unarchiving_returns_them_to_the_default_reads()
    {
        await unarchiveAsync(_archived);

        await using var query = _store.LightweightSession();

        var events = await query.Events.FetchStreamAsync(_archived, token: Token);

        events.Count.ShouldBe(2);
        events.ShouldAllBe(x => !x.IsArchived);
    }
}
