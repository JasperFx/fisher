using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events.Daemon;
using JasperFx.Events.EventModeling;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#251 — <c>AddFisher</c> registers the store-derived rung of the Event Model
///     (jasperfx#825): one <c>SlicePattern.View</c> slice per registered projection, read out of the
///     store's own registry.
/// </summary>
/// <remarks>
///     <para>
///         <b>The gap is that nobody derived from the store.</b> Bobcat declares slices and Wolverine
///         derives Command and Automation slices from its chains, so a View slice — event → projection
///         → read model — reached an Event Model canvas only when a human had written one down. Yet
///         the store knows it exactly: every registered projection, the document it produces, and the
///         event types its <c>Apply</c> / <c>Create</c> / <c>Evolve</c> methods take.
///     </para>
///     <para>
///         <b>Fisher matters disproportionately here, which is why jasperfx#825's acceptance names
///         it.</b> A store-derived Event Model that needs a Postgres or SQL Server container to
///         demonstrate is one nobody exercises while writing the feature. Every test in this file runs
///         against a throwaway SQLite file, which is what makes this the inner-loop store for the
///         whole rung.
///     </para>
///     <para>
///         <b>What is Fisher's here is the registration and nothing else.</b>
///         <see cref="ProjectionEventModelSource" /> reads <c>IEventStore.TryCreateUsage()</c> and
///         <c>SubscriptionDescriptor</c>, which Fisher fills through shared code in
///         <c>JasperFx.Events</c> — so these tests are about the wiring, and about the two things the
///         wiring can get wrong on a store that implements <c>IEventStore</c> explicitly and hands out
///         ancillary stores as <c>DispatchProxy</c> markers.
///     </para>
/// </remarks>
public class event_model_source : IAsyncLifetime
{
    private readonly TemporaryDatabase _primary = TemporaryDatabase.Create("event-model-primary");
    private readonly TemporaryDatabase _ancillary = TemporaryDatabase.Create("event-model-ancillary");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _primary.Dispose();
        _ancillary.Dispose();

        return ValueTask.CompletedTask;
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    ///     One single-stream projection yields one View slice carrying the projection, its document,
    ///     and every applied event — with no container needed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Asserted on the <em>roles</em> rather than on the slice's existence, because a slice
    ///         with the right name and empty roles is what a canvas renders as an unclickable sticky —
    ///         which is the outcome this rung exists to replace, not a partial version of it.
    ///     </para>
    ///     <para>
    ///         ⚠️ <b>The slice is named after the DOCUMENT, not the projection.</b> That is what makes
    ///         it merge with a spec-declared slice of the same name rather than sitting beside it — see
    ///         <see cref="a_derived_slice_merges_with_a_declared_slice_of_the_same_name" />.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_registered_projection_becomes_a_view_slice()
    {
        await using var provider = BuildPrimary();

        var model = await AssembleAsync(provider);

        var slice = model.Slices.Single(x => x.Name == nameof(ModelLedger));

        slice.Pattern.ShouldBe(SlicePattern.View);
        slice.ReadModelTypes.Select(x => x.Name).ShouldBe([nameof(ModelLedger)]);
        slice.ProjectionTypes.ShouldNotBeEmpty();

        var consumed = slice.ConsumedEvents.Select(x => x.Name).ToArray();
        consumed.ShouldContain(nameof(Credited));
        consumed.ShouldContain(nameof(Debited));

        // ⚠️ JasperFx's own lifecycle events come with them, because AppliedEvents is derived from
        // IAggregateProjection.AllEventTypes and every aggregation projection handles these two
        // whether or not the aggregate declares an Apply for them. Pinned rather than filtered:
        // nothing here is Fisher's to change -- the same reader feeds AggregateDescriptor.AppliedEvents
        // -- and a canvas showing stickies for events the application never wrote is worth knowing
        // about rather than quietly asserting around. Reported upstream as jasperfx#829.
        consumed.ShouldContain("Archived");
    }

    /// <summary>
    ///     The claim is <c>Derived</c>, which is what lets a store-read role outrank a declaration
    ///     that disagrees with it.
    /// </summary>
    /// <remarks>
    ///     Read off the assembled model rather than off the source, because the provenance is stamped
    ///     by <c>EventModelDiscovery</c> rather than carried on each slice — so asserting it on the
    ///     source would pass against a registration the discovery pipeline never reached.
    /// </remarks>
    [Fact]
    public async Task the_store_derived_slice_claims_derived_provenance()
    {
        await using var provider = BuildPrimary();

        var slice = (await AssembleAsync(provider)).Slices.Single(x => x.Name == nameof(ModelLedger));

        slice.ProvenanceFor(EventModelRole.ReadModelTypes).ShouldBe(EventModelProvenance.Derived);
        slice.ProvenanceFor(EventModelRole.ProjectionTypes).ShouldBe(EventModelProvenance.Derived);
    }

    /// <summary>
    ///     A registered <em>subscription</em> gets no slice, and neither does an inline projection's
    ///     absence of a document.
    /// </summary>
    /// <remarks>
    ///     A subscription has no read model to click through to, so inventing a green sticky for it
    ///     would put something on the canvas a reader cannot follow. Carried here rather than left to
    ///     the upstream unit tests because it is the one omission a Fisher reader would otherwise read
    ///     as the registration having missed something.
    /// </remarks>
    [Fact]
    public async Task a_subscription_gets_no_slice()
    {
        await using var provider = BuildPrimary(options
            => options.Projections.Subscribe(new LedgerAudit()));

        var model = await AssembleAsync(provider);

        model.Slices.Select(x => x.Name).ShouldNotContain(nameof(LedgerAudit));
        model.Slices.Select(x => x.Name).ShouldContain(nameof(ModelLedger));
    }

    /// <summary>
    ///     <b>An ancillary store registers its own source, and its projections reach the same model.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠️ <b>This is the registration most likely to be wrong on Fisher specifically.</b> An
    ///         ancillary store arrives as a <c>DispatchProxy</c> marker, which implements only the
    ///         interfaces it was asked for — so it is <em>not</em> an <c>IEventStore</c>, and a source
    ///         handed the proxy rather than the store behind it fails at discovery time. The same
    ///         unwrapping the <c>IEventStore</c> bridge does, for the same reason.
    ///     </para>
    ///     <para>
    ///         The two sources contribute to <b>one model</b>, deliberately: they differ by
    ///         <c>Subject</c>, which says which store the slices came from, and agree on the model
    ///         name, because slices are grouped by name before merging and an ancillary store's read
    ///         models belong on the same canvas as the primary's.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task an_ancillary_stores_projections_reach_the_same_model()
    {
        await using var provider = BuildBothStores();

        var models = await EventModelDiscovery.AssembleAsync(provider, Token);

        var model = models.ShouldHaveSingleItem();

        model.Slices.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal)
            .ShouldBe([nameof(Manifest), nameof(ModelLedger)]);

        var ancillary = model.Slices.Single(x => x.Name == nameof(Manifest));
        ancillary.Pattern.ShouldBe(SlicePattern.View);
        ancillary.ConsumedEvents.Select(x => x.Name).ShouldContain(nameof(Filed));
        ancillary.ReadModelTypes.Select(x => x.Name).ShouldBe([nameof(Manifest)]);
    }

    /// <summary>
    ///     Each store's source carries a <c>Subject</c> of its own.
    /// </summary>
    /// <remarks>
    ///     Two Fisher stores are usually two <em>files</em>, so collapsing them onto one subject uri
    ///     would leave a consumer unable to say which store a slice was read out of. The same
    ///     distinction <c>FisherSystemPart&lt;T&gt;</c> draws for the command line (fisher#172), and it
    ///     matters more here than on either sibling for the same reason.
    /// </remarks>
    [Fact]
    public async Task each_store_contributes_a_source_with_its_own_subject()
    {
        await using var provider = BuildBothStores();

        var subjects = provider.GetServices<IEventModelDefinitionSource>()
            .OfType<ProjectionEventModelSource>()
            .Select(x => x.Subject.ToString())
            .OrderBy(x => x)
            .ToArray();

        // The trailing slash on the first is Uri's own rendering of an authority with an empty path,
        // not something either registration wrote.
        subjects.ShouldBe([
            "event-model://projections/",
            "event-model://projections/iledgerarchivestore"
        ]);
    }

    /// <summary>
    ///     <b>The payoff.</b> A store-derived slice and a spec-declared one of the same name become
    ///     <em>one</em> slice carrying both claims — not two stickies that say the same thing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is why the slice is named after the document type rather than after the projection
    ///         class: Bobcat's <c>{readmodel}</c> capture and the curated model file already name View
    ///         slices that way, so the two merge by name. A mis-named slice would not merge, which is
    ///         the one thing this source exists to get right.
    ///     </para>
    ///     <para>
    ///         The declaration here deliberately carries a role the store cannot know — a
    ///         <c>Domain</c> — so the merged slice is observably richer than either half. Asserting
    ///         only that one slice came back would pass against a merge that silently discarded the
    ///         declared side.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_derived_slice_merges_with_a_declared_slice_of_the_same_name()
    {
        var builder = Host.CreateDefaultBuilder().ConfigureServices(services =>
        {
            services.AddFisher(options =>
            {
                options.ConnectionString = _primary.ConnectionString;
                options.AutoCreateSchemaObjects = AutoCreate.All;
                options.Projections.Snapshot<ModelLedger>(SnapshotLifecycle.Inline);
            });

            services.AddEventModel(ProjectionEventModelSource.DefaultModelName, model
                => model.Slice(nameof(ModelLedger)).InDomain("Finance"));
        });

        using var host = await builder.StartAsync(Token);

        var model = (await EventModelDiscovery.AssembleAsync(host.Services, Token)).ShouldHaveSingleItem();

        var slice = model.Slices.Single(x => x.Name == nameof(ModelLedger));

        // One slice, both claims.
        slice.Domain.ShouldBe("Finance");
        slice.ReadModelTypes.Select(x => x.Name).ShouldBe([nameof(ModelLedger)]);
        slice.ProvenanceFor(EventModelRole.ReadModelTypes).ShouldBe(EventModelProvenance.Derived);

        await host.StopAsync(Token);
    }

    /// <summary>
    ///     A store with no projections at all contributes nothing, rather than an empty model.
    /// </summary>
    /// <remarks>
    ///     "This store has no projections" and "this store was never asked" are different claims, and
    ///     an empty descriptor would make the second read as the first on a canvas.
    /// </remarks>
    [Fact]
    public async Task a_store_with_no_projections_contributes_nothing()
    {
        await using var provider = BuildPrimary(configure: null, withProjection: false);

        (await EventModelDiscovery.AssembleAsync(provider, Token)).ShouldBeEmpty();
    }

    private ServiceProvider BuildPrimary(Action<StoreOptions>? configure = null, bool withProjection = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFisher(options =>
        {
            options.ConnectionString = _primary.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            if (withProjection)
            {
                options.Projections.Snapshot<ModelLedger>(SnapshotLifecycle.Inline);
            }

            configure?.Invoke(options);
        });

        return services.BuildServiceProvider();
    }

    private ServiceProvider BuildBothStores()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddFisher(options =>
        {
            options.ConnectionString = _primary.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<ModelLedger>(SnapshotLifecycle.Inline);
        });

        services.AddFisherStore<ILedgerArchiveStore>(options =>
        {
            options.ConnectionString = _ancillary.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<Manifest>(SnapshotLifecycle.Inline);
        });

        return services.BuildServiceProvider();
    }

    private async Task<EventModelDescriptor> AssembleAsync(IServiceProvider provider)
        => (await EventModelDiscovery.AssembleAsync(provider, Token)).ShouldHaveSingleItem();
}

public interface ILedgerArchiveStore : IDocumentStore;

public record Credited(decimal Amount);

public record Debited(decimal Amount);

public record Filed(string Port);

public class ModelLedger
{
    public Guid Id { get; set; }
    public decimal Balance { get; set; }

    public void Apply(Credited e) => Balance += e.Amount;

    public void Apply(Debited e) => Balance -= e.Amount;
}

public class Manifest
{
    public Guid Id { get; set; }
    public string Port { get; set; } = string.Empty;

    public void Apply(Filed e) => Port = e.Port;
}

public class LedgerAudit : Fisher.Subscriptions.SubscriptionBase
{
    public override Task<IDaemonChangeListener> ProcessEventsAsync(EventRange page,
        ISubscriptionController controller, IDocumentSession operations, CancellationToken cancellationToken)
        => Task.FromResult<IDaemonChangeListener>(NullDaemonChangeListener.Instance);
}
