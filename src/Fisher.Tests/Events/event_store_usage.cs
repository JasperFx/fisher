using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Events;

/// <summary>
///     What <c>IEventStore.TryCreateUsage</c> puts on the wire, which is the whole of what a monitoring
///     tool and the shared JasperFx command line know about a Fisher store.
/// </summary>
/// <remarks>
///     <para>
///         <b>fisher#120 is the reason this file exists, and the reason it asserts on presence rather
///         than on shape.</b> Every slot on <see cref="EventStoreUsage" /> is a list or a nullable that
///         starts empty, so a store that never fills one is indistinguishable from a store that has
///         none of that thing — there is no exception, no warning, and nothing in the descriptor
///         saying which of the two it is. That is how a store with twenty registered projections came
///         to render as "No projections in this store." under <c>projections list</c> and to match
///         nothing under <c>projections rebuild</c>.
///     </para>
///     <para>
///         The shared <c>EventStoreExplorerCompliance</c> checks exactly one of these lists
///         (<c>usage.Events</c>) and is explicit that a null usage is allowed, so it cannot catch this
///         class of gap for any store. Until it grows a sibling, these are Fisher's own.
///     </para>
/// </remarks>
public class event_store_usage : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("usage");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            // Both lifecycles, because the failure mode fisher#120 reported was reachable by
            // describing only the async half — a daemon-shaped implementation that walked the shards
            // rather than the registrations would look right and still answer nothing for the store
            // in the issue, whose twenty projections were all Inline.
            options.Projections.Snapshot<SurveyTally>(SnapshotLifecycle.Inline);
            options.Projections.Snapshot<ChartTally>(SnapshotLifecycle.Async);

            options.Events.EnableCorrelationId = true;
            options.Events.EnableUserName = true;
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using var session = _store.LightweightSession();
        session.Events.StartStream<SurveyTally>(Guid.NewGuid(), new SurveyBegun("Astrolabe"), new ChartUpdated("Lisbon"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<EventStoreUsage> TheUsage()
    {
        var usage = await ((IEventStore)_store).TryCreateUsage(Token);
        usage.ShouldNotBeNull();
        return usage;
    }

    // ---- fisher#120 ----

    /// <summary>
    ///     The issue, reduced: an Inline projection has to reach the descriptor. This is the assertion
    ///     that fails against the shipped 1.0.2 behaviour, where <c>Subscriptions</c> was never
    ///     populated at all.
    /// </summary>
    [Fact]
    public async Task an_inline_projection_is_described()
    {
        var usage = await TheUsage();

        var described = usage.Subscriptions.SingleOrDefault(x => x.Name == nameof(SurveyTally));

        described.ShouldNotBeNull();
        described.Lifecycle.ShouldBe(ProjectionLifecycle.Inline);
    }

    /// <summary>
    ///     The async half, which is what <c>projections rebuild</c> matches a target against — by name
    ///     and then by shard.
    /// </summary>
    [Fact]
    public async Task an_async_projection_is_described_with_its_shards()
    {
        var usage = await TheUsage();

        var described = usage.Subscriptions.SingleOrDefault(x => x.Name == nameof(ChartTally));

        described.ShouldNotBeNull();
        described.Lifecycle.ShouldBe(ProjectionLifecycle.Async);
        described.ShardNames.ShouldNotBeEmpty();
    }

    /// <summary>
    ///     A rebuild target is resolved off the descriptor's <c>Name</c>, so the names on the wire have
    ///     to be the names the daemon knows a projection by. Asserted against the store's own
    ///     registry rather than against string literals, or the test would pin the convention rather
    ///     than the agreement.
    /// </summary>
    [Fact]
    public async Task the_described_names_are_the_names_the_store_registers()
    {
        var usage = await TheUsage();

        var described = usage.Subscriptions.Select(x => x.Name).OrderBy(x => x).ToList();
        var registered = _store.Options.Projections.All.Select(x => x.Name).OrderBy(x => x).ToList();

        described.ShouldBe(registered);
    }

    // ---- the rest of the descriptor, gap-audited alongside fisher#120 ----

    /// <summary>
    ///     <see cref="EventStoreUsage" /> carries the event registry twice and a consumer may read
    ///     either. Filling one and not the other is polecat#411, where the unfilled list read as "this
    ///     store has no event types configured".
    /// </summary>
    [Fact]
    public async Task both_event_type_collections_are_filled()
    {
        var usage = await TheUsage();

        var alias = _store.Options.EventGraph.EventMappingFor(typeof(SurveyBegun)).EventTypeName;

        usage.Events.Select(x => x.EventTypeName).ShouldContain(alias);
        usage.RegisteredEventTypes.Select(x => x.Alias).ShouldContain(alias);
    }

    /// <summary>
    ///     The two error policies are separate on purpose: a rebuild stops on an error a normal run
    ///     would skip. A console reading one for the other offers "view related dead letters" for a
    ///     store that halts instead, which is a button that never returns anything.
    /// </summary>
    [Fact]
    public async Task both_projection_error_policies_are_described_and_are_distinct()
    {
        _store.Options.Projections.Errors.SkipApplyErrors = true;
        _store.Options.Projections.RebuildErrors.SkipApplyErrors = false;

        var usage = await TheUsage();

        usage.ProjectionErrors.ShouldNotBeNull();
        usage.ProjectionRebuildErrors.ShouldNotBeNull();

        usage.ProjectionErrors.SkipApplyErrors.ShouldBeTrue();
        usage.ProjectionRebuildErrors.SkipApplyErrors.ShouldBeFalse();
    }

    /// <summary>
    ///     jasperfx#475 — the four event metadata columns are opt-in, so a query facet built over one
    ///     that is switched off would filter on a column the table does not have.
    /// </summary>
    [Fact]
    public async Task the_opt_in_metadata_columns_are_reported_as_configured()
    {
        var usage = await TheUsage();

        usage.EventMetadata.ShouldNotBeNull();
        usage.EventMetadata.StoreType.ShouldBe("Fisher");

        usage.EventMetadata.CorrelationId.ShouldBeTrue();
        usage.EventMetadata.UserName.ShouldBeTrue();
        usage.EventMetadata.CausationId.ShouldBeFalse();
        usage.EventMetadata.Headers.ShouldBeFalse();
    }

    /// <summary>
    ///     Every stream facet is universal in Fisher — <c>fi_streams</c> always carries the aggregate
    ///     type, the version, the timestamp, the tenant and the archived flag — so the capability
    ///     defaults are correct rather than merely unset. Pinned because "left at the default" and
    ///     "checked and true" are indistinguishable from the outside, and a later schema change that
    ///     made one of them optional would need to say so here.
    /// </summary>
    [Fact]
    public async Task every_stream_facet_is_reported_as_captured()
    {
        var usage = await TheUsage();

        usage.EventMetadata.ShouldNotBeNull();
        usage.EventMetadata.StreamAggregateType.ShouldBeTrue();
        usage.EventMetadata.StreamVersion.ShouldBeTrue();
        usage.EventMetadata.StreamTimestamps.ShouldBeTrue();
        usage.EventMetadata.TenantId.ShouldBeTrue();
        usage.EventMetadata.Archived.ShouldBeTrue();
    }

    /// <summary>
    ///     <b>The Fisher-specific fact in this file.</b> The gap between the physical maximum and the
    ///     high-water mark is what CritterWatch#150's second signal renders, and on Fisher there can
    ///     never be one: one writer per file plus <c>BEGIN IMMEDIATE</c> means committed sequences are
    ///     contiguous. Reporting the number is what lets a console see that; leaving it null renders
    ///     as "n/a" and says nothing at all.
    /// </summary>
    [Fact]
    public async Task the_max_event_sequence_is_the_high_water_mark()
    {
        var usage = await TheUsage();

        var highWater = await _store.Database.FetchHighestEventSequenceNumber(Token);

        usage.MaxEventSequence.ShouldNotBeNull();
        usage.MaxEventSequence.Value.ShouldBe(highWater);
    }

    /// <summary>
    ///     A store pointed at before its schema exists is exactly when a monitoring tool is most likely
    ///     to ask, so an unreadable sequence has to cost the optional number rather than the whole
    ///     description.
    /// </summary>
    [Fact]
    public async Task a_store_with_no_schema_still_describes_itself()
    {
        using var empty = TemporaryDatabase.Create("usage-noschema");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = empty.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.None;
            options.Projections.Snapshot<SurveyTally>(SnapshotLifecycle.Inline);
        });

        var usage = await ((IEventStore)store).TryCreateUsage(Token);

        usage.ShouldNotBeNull();
        usage.Subscriptions.ShouldNotBeEmpty();
    }
}

public record SurveyBegun(string Ship);

public record ChartUpdated(string Port);

public class SurveyTally
{
    public Guid Id { get; set; }
    public string Ship { get; set; } = string.Empty;
    public int Ports { get; set; }

    public void Apply(SurveyBegun begun) => Ship = begun.Ship;

    public void Apply(ChartUpdated called) => Ports++;
}

public class ChartTally
{
    public Guid Id { get; set; }
    public int Calls { get; set; }

    public void Apply(ChartUpdated called) => Calls++;
}
