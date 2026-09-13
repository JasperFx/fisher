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
///     <para>
///         <b>Wiring it is also what found jasperfx#829</b>, fixed in JasperFx 2.69.1: a View slice's
///         consumed events carried <c>Archived</c> and <c>Compacted&lt;T&gt;</c>, because
///         <c>AppliedEvents</c> is what the projection <em>handles</em> rather than what the aggregate
///         declares an <c>Apply</c> for. Pinning the exact set here rather than asserting around it is
///         what made the fix visible on the bump.
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

        // Exactly the two the aggregate declares an Apply for -- no more.
        //
        // ⚠️ This asserted the OPPOSITE until JasperFx 2.69.1, and the flip is the point rather than
        // an edit. JasperFxSingleStreamProjectionBase.determineEventTypes() concatenates Archived and
        // Compacted<T> onto every non-empty apply set whether or not the aggregate declares an Apply
        // for them, so this slice reported four event types -- correct about what the projection
        // HANDLES, and not what a canvas means by the events a read model consumes: two orange
        // stickies for events the application never wrote and no command slice emits, linking to
        // nothing. Reported as jasperfx#829 and filtered upstream in ProjectionEventModelSource.ToSlice
        // rather than in the reader that fills AppliedEvents, since a monitoring console asking "what
        // does this projection handle" wants both and only the CANVAS question is narrow.
        //
        // Pinned as an exact set rather than two ShouldContains, which is what made the flip visible
        // on the bump instead of silently passing either way.
        slice.ConsumedEvents.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal)
            .ShouldBe([nameof(Credited), nameof(Debited)]);
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
        ancillary.ConsumedEvents.Select(x => x.Name).ShouldBe([nameof(Filed)]);
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

        // Read through the interface rather than a concrete source type: what a consumer sees is
        // IEventModelDefinitionSource.Subject, and which class produces it is Fisher's business.
        var subjects = provider.GetServices<IEventModelDefinitionSource>()
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
    ///     <b>fisher#271.</b> A host that names its Event Model can tell the store the same name, and
    ///     gets <em>one</em> assembled model rather than two.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The reported symptom was a spec suite going from 18/18 to 15/18 on a version bump alone,
    ///         with <c>Expected exactly one assembled model, but got [Stoat, EventModel]</c>. Slices are
    ///         grouped by model name before merging, so a store still answering with the default name
    ///         does not contribute to the host's canvas at all — it assembles a second one beside it.
    ///     </para>
    ///     <para>
    ///         ⚠️ <b>Both halves are asserted, and the second is the one that makes the first mean
    ///         something.</b> Asserting only that a named store lands on the named model passes against
    ///         a store that ignores the parameter and puts everything on whichever name the host used —
    ///         so <see cref="a_store_left_on_the_default_assembles_a_second_model" /> pins the failure
    ///         this parameter exists to escape, as the behaviour it still is when nobody passes a name.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_named_model_takes_the_stores_slices_rather_than_assembling_a_second()
    {
        await using var provider = BuildNamed("Stoat", eventModelName: "Stoat");

        var model = (await EventModelDiscovery.AssembleAsync(provider, Token)).ShouldHaveSingleItem();

        model.Name.ShouldBe("Stoat");

        // One slice carrying both claims: the host declared the domain, the store derived the read
        // model. Two models would each have half of this and neither would have both.
        var slice = model.Slices.Single(x => x.Name == nameof(ModelLedger));
        slice.Domain.ShouldBe("Finance");
        slice.ReadModelTypes.Select(x => x.Name).ShouldBe([nameof(ModelLedger)]);
        slice.ProvenanceFor(EventModelRole.ReadModelTypes).ShouldBe(EventModelProvenance.Derived);
    }

    /// <summary>
    ///     The bug, as the behaviour it remains for a store nobody names: the host's model and the
    ///     store's default are two models, and the store's slices are on neither of the host's.
    /// </summary>
    /// <remarks>
    ///     Kept rather than left implicit because "there is no way to reach the named canvas" and
    ///     "reaching it needs one argument" are different situations, and only the second one is true
    ///     now. It is also the guard that would fail if the default ever silently followed the host.
    /// </remarks>
    [Fact]
    public async Task a_store_left_on_the_default_assembles_a_second_model()
    {
        await using var provider = BuildNamed("Stoat", eventModelName: null);

        var models = await EventModelDiscovery.AssembleAsync(provider, Token);

        models.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal)
            .ShouldBe([ProjectionEventModelSource.DefaultModelName, "Stoat"]);

        // And the host's own model never sees the read model the store knows about.
        models.Single(x => x.Name == "Stoat").Slices.Single(x => x.Name == nameof(ModelLedger))
            .ReadModelTypes.ShouldBeEmpty();
    }

    /// <summary>
    ///     An ancillary store takes the name too, and has to be told separately.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The old comment on that registration said the model name stays the default because an
    ///         ancillary store's read models belong on the same canvas as the primary's. That is right
    ///         about <em>which store</em> and says nothing about <em>which canvas</em> — so the two
    ///         calls agree on a name rather than on the default, and nothing in Fisher can check that
    ///         they do: <c>AddFisherStore&lt;T&gt;</c> can be called with no <c>AddFisher</c> at all.
    ///     </para>
    ///     <para>
    ///         They still differ by <c>Subject</c>, which is what says which store a slice came from —
    ///         see <see cref="each_store_contributes_a_source_with_its_own_subject" />.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task both_stores_reach_a_named_model_when_both_are_told_its_name()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddFisher(options =>
        {
            options.ConnectionString = _primary.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<ModelLedger>(SnapshotLifecycle.Inline);
            options.EventModelName = "Stoat";
        });

        services.AddFisherStore<ILedgerArchiveStore>(options =>
        {
            options.ConnectionString = _ancillary.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<Manifest>(SnapshotLifecycle.Inline);
            options.EventModelName = "Stoat";
        });

        await using var provider = services.BuildServiceProvider();

        var model = (await EventModelDiscovery.AssembleAsync(provider, Token)).ShouldHaveSingleItem();

        model.Name.ShouldBe("Stoat");
        model.Slices.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal)
            .ShouldBe([nameof(Manifest), nameof(ModelLedger)]);
    }

    /// <summary>
    ///     Passing no name is what every existing host does, and it still means the shared default.
    /// </summary>
    /// <remarks>
    ///     Asserted on the source's own <c>ModelName</c> rather than on an assembled model, because
    ///     the assembled name is the same string either way when nothing else names a model — which
    ///     is exactly the shape that would pass against a regression.
    /// </remarks>
    [Fact]
    public async Task no_name_is_still_the_shared_default()
    {
        await using var provider = BuildBothStores();

        provider.GetServices<IEventModelDefinitionSource>()
            .OfType<ProjectionEventModelSource>()
            .Select(x => x.ModelName)
            .ShouldAllBe(x => x == ProjectionEventModelSource.DefaultModelName);

        (await EventModelDiscovery.AssembleAsync(provider, Token)).ShouldHaveSingleItem()
            .Name.ShouldBe(ProjectionEventModelSource.DefaultModelName);
    }

    /// <summary>
    ///     ⚠️ <b>Declaring the host's model before or after <c>AddFisher</c> gives the same answer</b>
    ///     (fisher#276), which is what reading the name lazily buys.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         fisher#271 recorded "the store cannot infer the name" as a fact about the problem, and
    ///         it was not — it was a consequence of capturing the name when the service was
    ///         registered. <c>FisherProjectionEventModelSource</c> resolves the store inside
    ///         <c>TryCreateAsync</c>, so the options are read when the model is <em>assembled</em>,
    ///         long after every registration has run.
    ///     </para>
    ///     <para>
    ///         Both orders are asserted rather than just the awkward one: a source that somehow went
    ///         looking at registration time would pass model-second and fail model-first, and a test
    ///         carrying only one of them could not tell which.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task the_registration_order_does_not_matter(bool modelFirst)
    {
        await using var provider = BuildNamed("Stoat", eventModelName: "Stoat", modelFirst: modelFirst);

        var model = (await EventModelDiscovery.AssembleAsync(provider, Token)).ShouldHaveSingleItem();

        model.Name.ShouldBe("Stoat");

        var slice = model.Slices.Single(x => x.Name == nameof(ModelLedger));
        slice.Domain.ShouldBe("Finance");
        slice.ReadModelTypes.Select(x => x.Name).ShouldBe([nameof(ModelLedger)]);
    }

    /// <summary>
    ///     ⚠️ <b>With no name set, the store follows the SERVICE name</b> (fisher#280) — so a
    ///     Wolverine-shaped host and its store land on one canvas with nothing configured.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The literal <c>"EventModel"</c> is the one default guaranteed to be wrong for every
    ///         host, and it was the root cause of fisher#271 rather than an incidental choice. Every
    ///         other contributor defaults to something meaningful — Wolverine's chains to
    ///         <c>ServiceName</c>, a Bobcat spec assembly to its own name — so a host that named
    ///         nothing still got two models.
    ///     </para>
    ///     <para>
    ///         Asserted through a real <c>AddEventModel</c> merge rather than on the model's name
    ///         alone: a store that picked up the service name but contributed its slices somewhere
    ///         else would satisfy a name check and still leave the canvas empty.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task with_no_name_the_store_follows_the_service_name()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new JasperFxOptions { ServiceName = "Trawler" });

        services.AddFisher(options =>
        {
            options.ConnectionString = _primary.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<ModelLedger>(SnapshotLifecycle.Inline);
            // Deliberately NOT setting EventModelName.
        });

        services.AddEventModel("Trawler", model => model.Slice(nameof(ModelLedger)).InDomain("Finance"));

        await using var provider = services.BuildServiceProvider();

        var model = (await EventModelDiscovery.AssembleAsync(provider, Token)).ShouldHaveSingleItem();

        model.Name.ShouldBe("Trawler");

        var slice = model.Slices.Single(x => x.Name == nameof(ModelLedger));
        slice.Domain.ShouldBe("Finance");
        slice.ReadModelTypes.Select(x => x.Name).ShouldBe([nameof(ModelLedger)]);
    }

    /// <summary>
    ///     An explicit name still wins over the service name.
    /// </summary>
    /// <remarks>
    ///     Which is what a modular monolith needs: each module's store is genuinely its own bounded
    ///     context and should not be folded onto the service's canvas just because they share a
    ///     process.
    /// </remarks>
    [Fact]
    public async Task an_explicit_name_outranks_the_service_name()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new JasperFxOptions { ServiceName = "Trawler" });

        services.AddFisher(options =>
        {
            options.ConnectionString = _primary.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<ModelLedger>(SnapshotLifecycle.Inline);
            options.EventModelName = "Ledgers";
        });

        await using var provider = services.BuildServiceProvider();

        (await EventModelDiscovery.AssembleAsync(provider, Token)).ShouldHaveSingleItem()
            .Name.ShouldBe("Ledgers");
    }

    /// <summary>
    ///     A host with no JasperFx options at all still gets the shared literal.
    /// </summary>
    /// <remarks>
    ///     A bare <c>ServiceCollection</c> is an ordinary shape rather than a misconfiguration — it is
    ///     what most of Fisher's own tests build — so the source resolves with <c>GetService</c> and
    ///     falls through. Pinned because the obvious implementation reaches for
    ///     <c>GetRequiredService</c> and throws on every one of those hosts.
    /// </remarks>
    [Fact]
    public async Task no_jasperfx_options_falls_back_to_the_shared_literal()
    {
        await using var provider = BuildPrimary();

        (await EventModelDiscovery.AssembleAsync(provider, Token)).ShouldHaveSingleItem()
            .Name.ShouldBe(ProjectionEventModelSource.DefaultModelName);
    }

    /// <summary>
    ///     A blank service name is not a usable model name, and falls through to the literal.
    /// </summary>
    /// <remarks>
    ///     Same reason <see cref="StoreOptions.EventModelName" /> refuses one from its setter: it
    ///     would reproduce fisher#271 with a blank where the name should be. This one cannot be
    ///     refused — the value is the host's, not Fisher's — so it is ignored instead.
    /// </remarks>
    [Fact]
    public async Task a_blank_service_name_falls_through_rather_than_naming_a_model_nothing()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new JasperFxOptions { ServiceName = "   " });

        services.AddFisher(options =>
        {
            options.ConnectionString = _primary.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<ModelLedger>(SnapshotLifecycle.Inline);
        });

        await using var provider = services.BuildServiceProvider();

        (await EventModelDiscovery.AssembleAsync(provider, Token)).ShouldHaveSingleItem()
            .Name.ShouldBe(ProjectionEventModelSource.DefaultModelName);
    }

    /// <summary>
    ///     An empty or whitespace name is refused by the setter rather than taken at its word.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         It is a legal model name, so accepting it would reproduce the very bug the setting
    ///         exists to fix — a second assembled model nothing merges into — with a blank where the
    ///         name should be in the message that reports it. Null is how a caller asks for the
    ///         default.
    ///     </para>
    ///     <para>
    ///         <b>On the setter, which is what moving to <see cref="StoreOptions" /> buys here</b>
    ///         (fisher#276): the 1.8.0 parameter could only be checked inside <c>AddFisher</c>, after
    ///         several singletons had already been registered, so the guard also had to be hoisted to
    ///         keep a refusal from leaving a half-populated collection behind. A property refuses at
    ///         the line that set it and there is nothing to half-populate.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void an_empty_model_name_is_refused(string name)
    {
        Should.Throw<ArgumentException>(() => new StoreOptions { EventModelName = name });

        // And from inside a configuration lambda, which is where a caller actually writes it.
        var services = new ServiceCollection();
        services.AddFisher(options =>
        {
            options.ConnectionString = _primary.ConnectionString;
            options.EventModelName = name;
        });

        Should.Throw<ArgumentException>(() => services.BuildServiceProvider().GetRequiredService<IDocumentStore>());
    }

    /// <summary>
    ///     Null is the way to ask for the default, and is not refused.
    /// </summary>
    /// <remarks>
    ///     Worth its own fact because the guard is one <c>is not null</c> away from rejecting the
    ///     ordinary case, and every other test here would still pass if it did — they all set a name.
    /// </remarks>
    [Fact]
    public void a_null_model_name_is_the_default_and_is_allowed()
    {
        new StoreOptions { EventModelName = null }.EventModelName.ShouldBeNull();
        new StoreOptions().EventModelName.ShouldBeNull();
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

    /// <summary>
    ///     A host that names its Event Model, with the store either set to that name or left on the
    ///     default — the two sides of fisher#271, now configured through
    ///     <see cref="StoreOptions.EventModelName" /> (fisher#276).
    /// </summary>
    /// <param name="modelFirst">
    ///     Whether to declare the host's model BEFORE registering the store. Both orders must give the
    ///     same answer — see <see cref="the_registration_order_does_not_matter" />.
    /// </param>
    private ServiceProvider BuildNamed(string hostModelName, string? eventModelName, bool modelFirst = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // The declaration carries a role the store cannot know — a Domain — so a merged slice is
        // observably richer than either half and an unmerged one observably poorer.
        void DeclareModel() => services.AddEventModel(hostModelName, model
            => model.Slice(nameof(ModelLedger)).InDomain("Finance"));

        if (modelFirst) DeclareModel();

        services.AddFisher(options =>
        {
            options.ConnectionString = _primary.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Projections.Snapshot<ModelLedger>(SnapshotLifecycle.Inline);
            options.EventModelName = eventModelName;
        });

        if (!modelFirst) DeclareModel();

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
