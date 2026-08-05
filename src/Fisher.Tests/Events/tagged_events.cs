using Fisher.Tests.Schema;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Tags;

namespace Fisher.Tests.Events;

/// <summary>
///     DCB tags end to end: an explicitly tagged append, then reading it back by tag.
/// </summary>
public class tagged_events : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("tagged");
    private DocumentStore _store = null!;

    private readonly TerritoryId _shire = new(Guid.NewGuid());
    private readonly TerritoryId _mordor = new(Guid.NewGuid());
    private readonly CohortId _fellowship = new("fellowship");

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.RegisterTagType<TerritoryId>("territory");
            options.Events.RegisterTagType<CohortId>("cohort");
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<Guid> AppendTaggedAsync(object data, params object[] tags)
    {
        var streamId = Guid.NewGuid();

        await using var session = _store.LightweightSession();
        var @event = session.Events.BuildEvent(data);
        @event.WithTag(tags);
        session.Events.StartStream(streamId, @event);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return streamId;
    }

    [Fact]
    public async Task an_event_can_be_read_back_by_its_tag()
    {
        await AppendTaggedAsync(new QuestStarted("Destroy the ring"), _shire);

        await using var session = _store.LightweightSession();
        var events = await session.Events.QueryByTagsAsync(
            new EventTagQuery().Or<TerritoryId>(_shire), TestContext.Current.CancellationToken);

        events.Count.ShouldBe(1);
        events[0].Data.ShouldBeOfType<QuestStarted>().Name.ShouldBe("Destroy the ring");
    }

    /// <summary>
    ///     The trap this milestone was most likely to hit. A Guid tag value is TEXT, and SQLite's
    ///     default collation is case-sensitive — binding the raw Guid writes a BLOB and binding it
    ///     uppercase writes a row that can never be read back. Both fail by returning nothing.
    /// </summary>
    [Fact]
    public async Task a_guid_tag_value_round_trips()
    {
        await AppendTaggedAsync(new QuestStarted("Guid tagged"), _shire);

        await using var session = _store.LightweightSession();

        (await session.Events.QueryByTagsAsync(new EventTagQuery().Or<TerritoryId>(_shire),
            TestContext.Current.CancellationToken)).Count.ShouldBe(1);
        (await session.Events.QueryByTagsAsync(new EventTagQuery().Or<TerritoryId>(_mordor),
            TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_string_tag_value_round_trips()
    {
        await AppendTaggedAsync(new QuestStarted("String tagged"), _fellowship);

        await using var session = _store.LightweightSession();
        var events = await session.Events.QueryByTagsAsync(
            new EventTagQuery().Or<CohortId>(_fellowship), TestContext.Current.CancellationToken);

        events.Count.ShouldBe(1);
    }

    [Fact]
    public async Task an_event_may_carry_several_tags()
    {
        await AppendTaggedAsync(new QuestStarted("Two tags"), _shire, _fellowship);

        await using var session = _store.LightweightSession();

        (await session.Events.QueryByTagsAsync(new EventTagQuery().Or<TerritoryId>(_shire),
            TestContext.Current.CancellationToken)).Count.ShouldBe(1);
        (await session.Events.QueryByTagsAsync(new EventTagQuery().Or<CohortId>(_fellowship),
            TestContext.Current.CancellationToken)).Count.ShouldBe(1);
    }

    /// <summary>
    ///     Conditions are OR'd, and an event matching two of them still comes back once — which is why
    ///     the SQL uses subselects rather than joining the tag tables.
    /// </summary>
    [Fact]
    public async Task an_event_matching_two_conditions_is_returned_once()
    {
        await AppendTaggedAsync(new QuestStarted("Both"), _shire, _fellowship);

        await using var session = _store.LightweightSession();
        var events = await session.Events.QueryByTagsAsync(
            new EventTagQuery().Or<TerritoryId>(_shire).Or<CohortId>(_fellowship),
            TestContext.Current.CancellationToken);

        events.Count.ShouldBe(1);
    }

    /// <summary>
    ///     A tag query spans streams, so each event's identity has to come off its own row rather than
    ///     from the hydration context the single-stream reads use.
    /// </summary>
    [Fact]
    public async Task events_from_different_streams_keep_their_own_stream_ids()
    {
        var first = await AppendTaggedAsync(new QuestStarted("One"), _shire);
        var second = await AppendTaggedAsync(new QuestStarted("Two"), _shire);

        await using var session = _store.LightweightSession();
        var events = await session.Events.QueryByTagsAsync(
            new EventTagQuery().Or<TerritoryId>(_shire), TestContext.Current.CancellationToken);

        events.Count.ShouldBe(2);
        events.Select(x => x.StreamId).OrderBy(x => x).ShouldBe(new[] { first, second }.OrderBy(x => x));
    }

    /// <summary>
    ///     Ordering is by seq_id: across streams, version is not a global order.
    /// </summary>
    [Fact]
    public async Task results_come_back_in_append_order()
    {
        await AppendTaggedAsync(new QuestStarted("First"), _shire);
        await AppendTaggedAsync(new QuestStarted("Second"), _shire);
        await AppendTaggedAsync(new QuestStarted("Third"), _shire);

        await using var session = _store.LightweightSession();
        var events = await session.Events.QueryByTagsAsync(
            new EventTagQuery().Or<TerritoryId>(_shire), TestContext.Current.CancellationToken);

        events.Select(x => x.Data).Cast<QuestStarted>().Select(x => x.Name)
            .ShouldBe(["First", "Second", "Third"]);
        events.Select(x => x.Sequence).ShouldBeInOrder();
    }

    [Fact]
    public async Task a_condition_can_narrow_to_one_event_type()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            var started = session.Events.BuildEvent(new QuestStarted("Narrowed"));
            started.WithTag(_shire);
            var joined = session.Events.BuildEvent(new MemberJoined("Frodo"));
            joined.WithTag(_shire);

            session.Events.StartStream(streamId, started, joined);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();

        (await query.Events.QueryByTagsAsync(new EventTagQuery().Or<TerritoryId>(_shire),
            TestContext.Current.CancellationToken)).Count.ShouldBe(2);

        var narrowed = await query.Events.QueryByTagsAsync(
            new EventTagQuery().Or<MemberJoined, TerritoryId>(_shire), TestContext.Current.CancellationToken);

        narrowed.Count.ShouldBe(1);
        narrowed[0].Data.ShouldBeOfType<MemberJoined>();
    }

    [Fact]
    public async Task an_untagged_event_matches_nothing()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(Guid.NewGuid(), new QuestStarted("Untagged"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();

        (await query.Events.QueryByTagsAsync(new EventTagQuery().Or<TerritoryId>(_shire),
            TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task events_exist_reports_presence_without_materializing()
    {
        await AppendTaggedAsync(new QuestStarted("Present"), _shire);

        await using var session = _store.LightweightSession();

        (await session.Events.EventsExistAsync(new EventTagQuery().Or<TerritoryId>(_shire),
            TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await session.Events.EventsExistAsync(new EventTagQuery().Or<TerritoryId>(_mordor),
            TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task an_empty_query_matches_nothing_rather_than_everything()
    {
        await AppendTaggedAsync(new QuestStarted("Present"), _shire);

        await using var session = _store.LightweightSession();

        (await session.Events.QueryByTagsAsync(new EventTagQuery(),
            TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await session.Events.EventsExistAsync(new EventTagQuery(),
            TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    /// <summary>
    ///     An unregistered tag type has no table, so it is a configuration error rather than an empty
    ///     result — an empty result would read as "no matching events".
    /// </summary>
    [Fact]
    public async Task querying_an_unregistered_tag_type_throws()
    {
        await using var session = _store.LightweightSession();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await session.Events.QueryByTagsAsync(new EventTagQuery().Or<SeatNumber>(new SeatNumber(1)),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task appending_with_an_unregistered_tag_type_throws()
    {
        await using var session = _store.LightweightSession();

        var @event = session.Events.BuildEvent(new QuestStarted("Bad tag"));
        @event.WithTag(new SeatNumber(4));
        session.Events.StartStream(Guid.NewGuid(), @event);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await session.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    ///     Tags commit with the events that carry them, so a unit of work that fails while writing tags
    ///     leaves neither the tags nor the events behind.
    /// </summary>
    /// <remarks>
    ///     The failure is induced in the tag write itself — an unregistered tag type on the second
    ///     event — because that is the step this milestone added <em>after</em> the batch. If it ran
    ///     outside the transaction, the first event would survive and be visible but untagged, which a
    ///     tag query cannot tell apart from an event that was never tagged.
    /// </remarks>
    [Fact]
    public async Task a_failed_tag_write_rolls_the_whole_unit_of_work_back()
    {
        var goodStream = Guid.NewGuid();

        await using (var doomed = _store.LightweightSession())
        {
            var tagged = doomed.Events.BuildEvent(new QuestStarted("Would have committed"));
            tagged.WithTag(_mordor);
            doomed.Events.StartStream(goodStream, tagged);

            var broken = doomed.Events.BuildEvent(new QuestStarted("Unregistered tag"));
            broken.WithTag(new SeatNumber(7));
            doomed.Events.StartStream(Guid.NewGuid(), broken);

            await Should.ThrowAsync<InvalidOperationException>(async () =>
                await doomed.SaveChangesAsync(TestContext.Current.CancellationToken));
        }

        await using var session = _store.LightweightSession();

        (await session.Events.QueryByTagsAsync(new EventTagQuery().Or<TerritoryId>(_mordor),
            TestContext.Current.CancellationToken)).ShouldBeEmpty();

        // And the events themselves are gone, not merely untagged.
        (await session.Events.FetchStreamAsync(goodStream, token: TestContext.Current.CancellationToken))
            .ShouldBeEmpty();
    }
}
