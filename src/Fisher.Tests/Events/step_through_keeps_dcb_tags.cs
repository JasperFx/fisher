using System.Text.Json;
using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Events;

public record StepTallyId(Guid Value);

public record StepTicked(int Amount);

public class StepTagTally
{
    public Guid Id { get; set; }
    public int Total { get; set; }
}

/// <summary>Grouped only by the events' DCB tags, so it groups nothing when a stepped event loses them.</summary>
public partial class StepTagTallyProjection : Fisher.Projections.MultiStreamProjection<StepTagTally, Guid>
{
    public const string ProjectionName = "StepTagTally";

    public StepTagTallyProjection()
    {
        Name = ProjectionName;
        IdentityFromTag<StepTallyId>();
        IncludeType<StepTicked>();
    }

    public void Apply(StepTicked e, StepTagTally tally) => tally.Total += e.Amount;
}

/// <summary>
///     fisher#654: the step-through's <c>EventRecord -> IEvent</c> conversion copies <c>EventRecord.Tags</c> onto the
///     event, so a projection grouped by <c>IdentityFromTag</c> finds its identities when it is stepped from records.
/// </summary>
/// <remarks>
///     A record names a tag by its CLR type name or its table suffix, the two spellings <c>FindByTagName</c> takes,
///     with the tag's simple value as text. The records here use both.
/// </remarks>
public class step_through_keeps_dcb_tags : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("step-through-tags");
    private DocumentStore _store = null!;

    private readonly Guid _red = Guid.NewGuid();
    private readonly Guid _blue = Guid.NewGuid();
    private long _sequence;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.RegisterTagType<StepTallyId>("step_tally");
            options.Events.AddEventType(typeof(StepTicked));
            options.Projections.Add(new StepTagTallyProjection(), ProjectionLifecycle.Async);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private EventRecord ticked(int amount, IReadOnlyDictionary<string, string>? tags)
    {
        var sequence = ++_sequence;
        return new EventRecord(Guid.NewGuid(), sequence, 1, Guid.NewGuid().ToString(),
            _store.Options.EventGraph.EventMappingFor(typeof(StepTicked)).EventTypeName,
            JsonSerializer.Deserialize<JsonElement>($$"""{"amount":{{amount}}}"""), null, DateTimeOffset.UtcNow,
            StorageConstants.DefaultTenantId, tags);
    }

    private IReadOnlyList<EventRecord> records() =>
    [
        ticked(1, new Dictionary<string, string> { ["StepTallyId"] = _red.ToString() }),
        ticked(10, new Dictionary<string, string> { ["step_tally"] = _blue.ToString() }),
        ticked(2, new Dictionary<string, string> { ["step_tally"] = _red.ToString() }),
        ticked(3, null)
    ];

    [Fact]
    public async Task stepping_one_identity_from_records_finds_it_by_its_tag()
    {
        var timeline = await ((IEventStore)_store).RunMultiStreamProjectionForIdentityAsync(
            StepTagTallyProjection.ProjectionName, _red, records(), startingState: null, Token);

        timeline.Steps.ShouldAllBe(x => x.Error == null);
        timeline.Steps.Select(x => x.Event.Sequence).ShouldBe([1L, 3L]);

        var state = timeline.FinalState.ShouldNotBeNull();
        state.GetProperty("id").GetGuid().ShouldBe(_red);
        state.GetProperty("total").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task stepping_one_identity_from_a_starting_state_folds_onto_it()
    {
        var starting = JsonSerializer.Deserialize<JsonElement>($$"""{"id":"{{_blue}}","total":100}""");

        var timeline = await ((IEventStore)_store).RunMultiStreamProjectionForIdentityAsync(
            StepTagTallyProjection.ProjectionName, _blue, records(), starting, Token);

        timeline.Steps.Select(x => x.Event.Sequence).ShouldBe([2L]);
        timeline.FinalState.ShouldNotBeNull().GetProperty("total").GetInt32().ShouldBe(110);
    }

    [Fact]
    public async Task stepping_every_identity_from_records_fans_out_by_tag()
    {
        var result = await ((IEventStore)_store).RunMultiStreamProjectionAsync(
            StepTagTallyProjection.ProjectionName, records(), Token);

        result.AggregatesByIdentity.Keys.OrderBy(x => x)
            .ShouldBe(new[] { _red.ToString(), _blue.ToString() }.OrderBy(x => x));
        result.AggregatesByIdentity[_red.ToString()].FinalState!.Value.GetProperty("total").GetInt32().ShouldBe(3);
        result.AggregatesByIdentity[_blue.ToString()].FinalState!.Value.GetProperty("total").GetInt32().ShouldBe(10);
    }

    [Fact]
    public async Task a_record_without_tags_gives_a_tag_grouped_projection_nothing()
    {
        // Not vacuous: the same events without their tags group nowhere
        var untagged = records().Select(x => x with { Tags = null }).ToList();

        var timeline = await ((IEventStore)_store).RunMultiStreamProjectionForIdentityAsync(
            StepTagTallyProjection.ProjectionName, _red, untagged, startingState: null, Token);

        timeline.Steps.ShouldBeEmpty();
        timeline.FinalState.ShouldBeNull();
    }

    [Fact]
    public async Task a_tag_this_store_has_not_registered_is_left_off()
    {
        var events = new[]
        {
            ticked(4, new Dictionary<string, string> { ["SomebodyElsesTag"] = "x", ["StepTallyId"] = _red.ToString() })
        };

        var timeline = await ((IEventStore)_store).RunMultiStreamProjectionForIdentityAsync(
            StepTagTallyProjection.ProjectionName, _red, events, startingState: null, Token);

        timeline.FinalState.ShouldNotBeNull().GetProperty("total").GetInt32().ShouldBe(4);
    }

    [Fact]
    public async Task a_tag_value_that_does_not_parse_is_refused_by_name()
    {
        var events = new[] { ticked(4, new Dictionary<string, string> { ["StepTallyId"] = "not-a-guid" }) };

        var e = await Should.ThrowAsync<ArgumentException>(() => ((IEventStore)_store)
            .RunMultiStreamProjectionForIdentityAsync(StepTagTallyProjection.ProjectionName, _red, events,
                startingState: null, Token));

        e.Message.ShouldContain("StepTallyId = 'not-a-guid'");
    }
}
