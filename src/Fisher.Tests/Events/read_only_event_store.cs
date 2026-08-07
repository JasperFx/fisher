using JasperFx;
using JasperFx.Events;

namespace Fisher.Tests.Events;

/// <summary>
///     <c>IEventStore.OpenReadOnlyEventStore()</c> and its paged <c>EventQuery</c> read — fisher#15.
/// </summary>
/// <remarks>
///     No shared compliance suite covers <see cref="IReadOnlyEventStore" />, so this is the whole of its
///     coverage. CritterWatch's Event Explorer is the caller, which is why the assertions are about what
///     a monitoring tool would render: the total across pages, an unknown filter narrowing to nothing
///     rather than erroring, and a filter on a column the store was not configured to write being
///     ignored rather than throwing.
/// </remarks>
public class read_only_event_store : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("readonly_events");
    private DocumentStore _store = null!;
    private readonly Guid _questId = Guid.NewGuid();
    private readonly Guid _otherId = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.EnableCorrelationId = true;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using var session = _store.LightweightSession();
        ((Fisher.Internal.FisherSession)session).CorrelationId = "corr-1";
        session.Events.StartStream<Quest>(_questId,
            new QuestStarted("Find the ring"),
            new MemberJoined("Frodo"),
            new MemberJoined("Sam"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var second = _store.LightweightSession();
        ((Fisher.Internal.FisherSession)second).CorrelationId = "corr-2";
        second.Events.StartStream<Quest>(_otherId, new QuestStarted("Guard the shire"));
        await second.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private IReadOnlyEventStore TheReadStore => ((IEventStore)_store).OpenReadOnlyEventStore();

    [Fact]
    public void opening_a_read_only_store_no_longer_throws()
    {
        // fisher#15's headline: this was the last NotSupportedException in Fisher.
        TheReadStore.ShouldNotBeNull();
    }

    [Fact]
    public async Task the_four_stream_reads_answer_through_a_session()
    {
        var store = TheReadStore;

        var events = await store.FetchStreamAsync(_questId, token: TestContext.Current.CancellationToken);
        events.Count.ShouldBe(3);

        var state = await store.FetchStreamStateAsync(_questId, TestContext.Current.CancellationToken);
        state.ShouldNotBeNull();
        state.Version.ShouldBe(3);
    }

    /// <summary>
    ///     Each call opens and disposes its own session, so a read store is usable more than once — the
    ///     property that makes owning session lifetime worth the divergence from Polecat.
    /// </summary>
    [Fact]
    public async Task a_read_store_serves_more_than_one_call()
    {
        var store = TheReadStore;

        for (var i = 0; i < 3; i++)
        {
            var page = await store.QueryEventsAsync(new EventQuery(), TestContext.Current.CancellationToken);
            page.TotalCount.ShouldBe(4);
        }
    }

    [Fact]
    public async Task an_unfiltered_query_pages_every_event_in_sequence_order()
    {
        var page = await TheReadStore.QueryEventsAsync(new EventQuery(),
            TestContext.Current.CancellationToken);

        page.TotalCount.ShouldBe(4);
        page.PageNumber.ShouldBe(1);
        page.PageSize.ShouldBe(50);
        page.Events.Count.ShouldBe(4);
        page.Events.Select(x => x.Sequence).ShouldBe(new long[] { 1, 2, 3, 4 });
    }

    /// <summary>
    ///     The assertion the count exists for: <c>TotalCount</c> is the whole matching set, not the
    ///     page's own length, or a tool cannot render "page 1 of n".
    /// </summary>
    [Fact]
    public async Task paging_reports_the_total_rather_than_the_page_length()
    {
        var first = await TheReadStore.QueryEventsAsync(
            new EventQuery { PageNumber = 1, PageSize = 3 }, TestContext.Current.CancellationToken);

        first.Events.Count.ShouldBe(3);
        first.TotalCount.ShouldBe(4);

        var second = await TheReadStore.QueryEventsAsync(
            new EventQuery { PageNumber = 2, PageSize = 3 }, TestContext.Current.CancellationToken);

        second.Events.Count.ShouldBe(1);
        second.TotalCount.ShouldBe(4);
        second.Events.Single().Sequence.ShouldBe(4);
    }

    /// <summary>
    ///     A page past the end is empty but still reports the real total. This is what a
    ///     <c>count(*) over ()</c> window function would get wrong — no rows means no window row to read
    ///     the total from, so the tool would be told there are zero events.
    /// </summary>
    [Fact]
    public async Task a_page_past_the_end_still_reports_the_total()
    {
        var page = await TheReadStore.QueryEventsAsync(
            new EventQuery { PageNumber = 9, PageSize = 3 }, TestContext.Current.CancellationToken);

        page.Events.ShouldBeEmpty();
        page.TotalCount.ShouldBe(4);
    }

    [Fact]
    public async Task a_non_positive_page_number_or_size_falls_back_to_the_defaults()
    {
        var page = await TheReadStore.QueryEventsAsync(
            new EventQuery { PageNumber = 0, PageSize = 0 }, TestContext.Current.CancellationToken);

        page.PageNumber.ShouldBe(1);
        page.PageSize.ShouldBe(50);
        page.Events.Count.ShouldBe(4);
    }

    [Fact]
    public async Task filtering_by_event_type_name_narrows_the_result()
    {
        var page = await TheReadStore.QueryEventsAsync(
            new EventQuery { EventTypeName = "member_joined" }, TestContext.Current.CancellationToken);

        page.TotalCount.ShouldBe(2);
        page.Events.ShouldAllBe(x => x.Data is MemberJoined);
    }

    [Fact]
    public async Task filtering_by_stream_id_narrows_the_result()
    {
        var page = await TheReadStore.QueryEventsAsync(
            new EventQuery { StreamId = _otherId.ToString() }, TestContext.Current.CancellationToken);

        page.TotalCount.ShouldBe(1);
        page.Events.Single().StreamId.ShouldBe(_otherId);
    }

    /// <summary>
    ///     The SQLite trap, and the reason the filter parses the incoming string rather than binding it.
    ///     <c>fi_events.stream_id</c> holds the lowercase canonical form and SQLite's default collation is
    ///     case-sensitive, so an uppercase Guid string would match nothing at all — and a monitoring tool
    ///     would render an existing stream as empty. Same trap as
    ///     <c>event_store_explorer.stream_metadata_is_found_regardless_of_guid_casing</c>.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task a_stream_id_filter_matches_regardless_of_guid_casing(bool uppercase)
    {
        var id = uppercase ? _questId.ToString().ToUpperInvariant() : _questId.ToString().ToLowerInvariant();

        var page = await TheReadStore.QueryEventsAsync(
            new EventQuery { StreamId = id }, TestContext.Current.CancellationToken);

        page.TotalCount.ShouldBe(3);
    }

    /// <summary>
    ///     An unparseable stream id under Guid identity matches nothing rather than throwing. A
    ///     monitoring tool passes whatever was typed into a search box, and an exception there is a
    ///     worse answer than an empty result.
    /// </summary>
    [Fact]
    public async Task an_unparseable_stream_id_matches_nothing_rather_than_throwing()
    {
        var page = await TheReadStore.QueryEventsAsync(
            new EventQuery { StreamId = "not-a-guid" }, TestContext.Current.CancellationToken);

        page.TotalCount.ShouldBe(0);
        page.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task filtering_by_correlation_id_narrows_the_result()
    {
        var page = await TheReadStore.QueryEventsAsync(
            new EventQuery { CorrelationId = "corr-2" }, TestContext.Current.CancellationToken);

        page.TotalCount.ShouldBe(1);
        page.Events.Single().StreamId.ShouldBe(_otherId);
    }

    [Fact]
    public async Task filters_compose()
    {
        var page = await TheReadStore.QueryEventsAsync(
            new EventQuery { StreamId = _questId.ToString(), EventTypeName = "member_joined" },
            TestContext.Current.CancellationToken);

        page.TotalCount.ShouldBe(2);
    }

    /// <summary>
    ///     The gate that matters. <c>causation_id</c> and <c>user_name</c> are not on this store's
    ///     <c>fi_events</c> at all, because the options that create them are off — so applying the filter
    ///     would be a <c>no such column</c> error rather than an empty result. <c>EventQuery</c> says such
    ///     a filter is only honoured when the store captures the column, so it is ignored.
    /// </summary>
    [Fact]
    public async Task a_filter_on_a_column_this_store_does_not_write_is_ignored()
    {
        var page = await TheReadStore.QueryEventsAsync(
            new EventQuery { CausationId = "never-written", UserName = "nobody" },
            TestContext.Current.CancellationToken);

        page.TotalCount.ShouldBe(4);
    }
}
