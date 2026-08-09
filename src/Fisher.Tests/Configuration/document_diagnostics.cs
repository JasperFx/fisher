using System.Text.Json;
using Fisher.Tests.Events;
using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Documents;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Configuration;

/// <summary>
///     fisher#44 — the document half of the tooling contract: <c>IDocumentStoreUsageSource</c>,
///     <c>IDocumentStoreDiagnostics</c> and projection step-through.
/// </summary>
/// <remarks>
///     <para>
///         Fisher already answered the event half of every question a monitoring console asks and none
///         of the document half — which renders as "no documents" rather than "this store does not
///         answer that". That is the outcome CLAUDE.md's standing discipline exists to prevent,
///         arrived at by a different route: not a member that throws, but an interface that was never
///         implemented.
///     </para>
///     <para>
///         All three surfaces are implemented <b>explicitly</b>, so the tests cast — which is also the
///         check that they did not leak onto <c>IDocumentStore</c>.
///     </para>
/// </remarks>
public class document_diagnostics : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("doc-diagnostics");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            options.Schema.For<Warehouse>();
            options.Schema.For<Crate>().SoftDeleted().Metadata(x => x.CorrelationId.Enabled = true);
            options.Schema.For<Vessel>().MultiTenanted();
            options.Schema.For<Container>().AddSubClassHierarchy();

            options.Projections.Snapshot<CargoTally>(SnapshotLifecycle.Inline);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private IDocumentStoreDiagnostics Diagnostics => _store;
    private IDocumentStoreUsageSource Usage => _store;
    private IEventStore Explorer => _store;

    // ---- usage ----

    [Fact]
    public async Task usage_describes_every_mapping()
    {
        var usage = await Usage.TryCreateUsage(Token);

        usage.ShouldNotBeNull();
        usage!.StoreName.ShouldBe("Main");

        var byAlias = usage.Documents.ToDictionary(x => x.Alias);

        byAlias["crate"].DeleteStyle.ShouldBe(nameof(DeleteStyle.SoftDelete));
        byAlias["vessel"].TenancyStyle.ShouldBe(nameof(TenancyStyle.Conjoined));
        byAlias["container"].SubClassCount.ShouldBe(2);
        byAlias["warehouse"].IdStrategy.ShouldBe(nameof(Guid));

        // The projection's aggregate type is a mapping only once something has asked for it, which on
        // a freshly-booted store is nothing — so the sweep has to force it, or a console sees a store
        // with no snapshot types.
        byAlias.ShouldContainKey("cargotally");
    }

    /// <remarks>
    ///     The DDL is what makes the descriptor useful for a schema diff, and it is the one part that
    ///     can throw. Asserted rather than assumed because a failure here is reported as a SQL comment
    ///     — deliberately, so one bad mapping does not take the whole store's description with it.
    /// </remarks>
    [Fact]
    public async Task usage_carries_the_ddl_each_mapping_would_emit()
    {
        var usage = await Usage.TryCreateUsage(Token);
        var crate = usage!.Documents.Single(x => x.Alias == "crate");

        crate.Ddl.ShouldContain("fi_doc_crate");
        crate.Ddl.ShouldContain("is_deleted");
        crate.Ddl.ShouldNotContain("Failed to generate DDL");
    }

    /// <remarks>
    ///     Reported as null rather than omitted, which is the honest answer and not the same as saying
    ///     nothing: SQLite has no table partitioning, so the field has a value — "none" — rather than
    ///     being unknown.
    /// </remarks>
    [Fact]
    public async Task usage_reports_no_partitioning_rather_than_omitting_it()
    {
        var usage = await Usage.TryCreateUsage(Token);

        usage!.Documents.ShouldAllBe(x => x.PartitioningStrategy == null);
        usage.Documents.ShouldAllBe(x => x.Partitioning == null);
    }

    [Fact]
    public async Task usage_advertises_only_the_metadata_the_store_captures()
    {
        var usage = await Usage.TryCreateUsage(Token);

        usage!.DocumentMetadata!.StoreType.ShouldBe("Fisher");
        usage.DocumentMetadata.CorrelationId.ShouldBeTrue();
        usage.DocumentMetadata.CausationId.ShouldBeFalse();
    }

    // ---- diagnostics ----

    [Fact]
    public async Task document_types_lists_every_mapped_type()
    {
        var types = await Diagnostics.DocumentTypesAsync(Token);

        types.Select(x => x.Alias).ShouldContain("warehouse");
        types.Select(x => x.Alias).ShouldContain("cargotally");
        types.ShouldAllBe(x => x.SchemaName == "main");
    }

    /// <remarks>
    ///     A document table is created on demand at first write, so a registered type with no rows has
    ///     no table — and SQLite resolves a table name when it <em>prepares</em> a statement, so a
    ///     count against it fails before any guard could run. This is the console's first click on a
    ///     freshly-migrated store.
    /// </remarks>
    [Fact]
    public async Task a_type_whose_table_was_never_created_reports_zero_rather_than_failing()
    {
        var result = await Diagnostics.QueryDocumentsAsync(
            typeof(Warehouse).FullName!, new DocumentQueryOptions(1, 10), Token);

        result.TotalCount.ShouldBe(0);
        result.DocumentsJson.ShouldBeEmpty();
    }

    [Fact]
    public async Task documents_page_as_raw_json_with_a_total()
    {
        await using (var session = _store.LightweightSession())
        {
            for (var i = 0; i < 5; i++)
            {
                session.Store(new Warehouse { Id = Guid.NewGuid(), Name = $"Shed {i}" });
            }

            await session.SaveChangesAsync(Token);
        }

        var page = await Diagnostics.QueryDocumentsAsync(
            typeof(Warehouse).FullName!, new DocumentQueryOptions(2, 2), Token);

        page.TotalCount.ShouldBe(5);
        page.DocumentsJson.Count.ShouldBe(2);
        page.DocumentsJson.ShouldAllBe(x => x.StartsWith("{"));
    }

    /// <remarks>
    ///     A console hands an id over as text, and <c>fi_doc_*.id</c> holds the lowercase canonical
    ///     form of a Guid under a case-sensitive collation — so binding the raw string would match
    ///     nothing. Fourth appearance of that trap, hence the uppercase input.
    /// </remarks>
    [Fact]
    public async Task loading_one_document_by_id_is_case_insensitive_for_a_guid()
    {
        var id = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Warehouse { Id = id, Name = "Shed" });
            await session.SaveChangesAsync(Token);
        }

        var json = await Diagnostics.LoadDocumentJsonAsync(
            typeof(Warehouse).FullName!, id.ToString().ToUpperInvariant(), Token);

        json.ShouldNotBeNull();
        json!.ShouldContain("Shed");

        (await Diagnostics.LoadDocumentJsonAsync(typeof(Warehouse).FullName!, Guid.NewGuid().ToString(), Token))
            .ShouldBeNull();
    }

    /// <summary>
    ///     Soft delete, tenancy and the hierarchy discriminator all apply, though this read is
    ///     hand-built SQL rather than a LINQ query.
    /// </summary>
    /// <remarks>
    ///     The fourth-caller shape fisher#51 warns about: a diagnostics read cannot go through
    ///     <c>Query&lt;T&gt;()</c>, because the console names a type as a string and filters on columns
    ///     that are not document members. Each filter is composed from the single place that owns it
    ///     rather than re-spelled — and this is what checks that all three survived.
    /// </remarks>
    [Fact]
    public async Task diagnostics_reads_carry_the_implicit_filters()
    {
        // Soft delete.
        var crateId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Crate { Id = crateId, Label = "Doomed" });
            await session.SaveChangesAsync(Token);
        }

        await using (var deleting = _store.LightweightSession())
        {
            deleting.Delete<Crate>(crateId);
            await deleting.SaveChangesAsync(Token);
        }

        (await Diagnostics.QueryDocumentsAsync(typeof(Crate).FullName!, new DocumentQueryOptions(1, 10), Token))
            .TotalCount.ShouldBe(0);

        // Tenancy, in both directions.
        await using (var north = _store.LightweightSession("north"))
        {
            north.Store(new Vessel { Id = Guid.NewGuid(), Name = "Northern Star" });
            await north.SaveChangesAsync(Token);
        }

        (await Diagnostics.QueryDocumentsAsync(typeof(Vessel).FullName!,
            new DocumentQueryOptions(1, 10) { TenantId = "north" }, Token)).TotalCount.ShouldBe(1);

        (await Diagnostics.QueryDocumentsAsync(typeof(Vessel).FullName!,
            new DocumentQueryOptions(1, 10) { TenantId = "south" }, Token)).TotalCount.ShouldBe(0);

        // The hierarchy discriminator: querying the base returns both, querying a sub-class narrows.
        await using (var session = _store.LightweightSession())
        {
            session.Store<Container>(new ReeferContainer { Id = Guid.NewGuid(), Code = "R1" });
            session.Store<Container>(new TankContainer { Id = Guid.NewGuid(), Code = "T1" });
            await session.SaveChangesAsync(Token);
        }

        (await Diagnostics.QueryDocumentsAsync(typeof(Container).FullName!,
            new DocumentQueryOptions(1, 10), Token)).TotalCount.ShouldBe(2);

        (await Diagnostics.QueryDocumentsAsync(typeof(TankContainer).FullName!,
            new DocumentQueryOptions(1, 10), Token)).TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task an_unknown_type_is_an_empty_page_rather_than_a_throw()
    {
        var result = await Diagnostics.QueryDocumentsAsync("Nope.NotAType", new DocumentQueryOptions(1, 10), Token);

        result.TotalCount.ShouldBe(0);
        result.DocumentsJson.ShouldBeEmpty();
    }

    // ---- projection replay ----

    /// <remarks>
    ///     The claim worth checking is not "it folds" but "it folds to what the daemon would have
    ///     produced", so the expected value is taken from the store's own inline projection over the
    ///     same events rather than written out by hand.
    /// </remarks>
    [Fact]
    public async Task replay_matches_what_the_projection_produces_for_the_same_events()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream<CargoTally>(streamId,
                new CrateLoaded("A"), new CrateLoaded("B"), new CrateUnloaded("A"));

            await session.SaveChangesAsync(Token);
        }

        await using var query = _store.LightweightSession();
        var stored = (await query.LoadAsync<CargoTally>(streamId, Token))!;

        var records = ToRecords(streamId,
            new CrateLoaded("A"), new CrateLoaded("B"), new CrateUnloaded("A"));

        var timeline = await Explorer.RunProjectionAsync<CargoTally>(
            nameof(CargoTally), streamId, records, startingState: null, Token);

        timeline.Steps.Count.ShouldBe(3);
        timeline.FinalState.Loaded.ShouldBe(stored.Loaded);
        timeline.FinalState.Loaded.ShouldBe(1);

        // Per-step, which is the whole point of a step-through.
        timeline.Steps.Select(x => x.After.Loaded).ShouldBe([1, 2, 1]);
        timeline.Steps.ShouldAllBe(x => x.Error == null);
    }

    [Fact]
    public async Task replay_by_name_returns_the_state_as_json()
    {
        var streamId = Guid.NewGuid();
        var records = ToRecords(streamId, new CrateLoaded("A"), new CrateLoaded("B"));

        var timeline = await Explorer.RunProjectionByNameAsync(
            nameof(CargoTally), streamId, records, startingState: null, Token);

        timeline.Steps.Count.ShouldBe(2);
        timeline.FinalState!.Value.GetProperty("Loaded").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task an_unknown_projection_is_refused_by_name()
    {
        var ex = await Should.ThrowAsync<ArgumentException>(async ()
            => await Explorer.RunProjectionByNameAsync("nope", Guid.NewGuid(), [], null, Token));

        ex.Message.ShouldContain("Unknown projection 'nope'");
    }

    /// <remarks>
    ///     Nothing is persisted, which is the contract's first word. Checked by replaying against a
    ///     stream that does not exist and confirming no document appeared.
    /// </remarks>
    [Fact]
    public async Task replay_writes_nothing()
    {
        var streamId = Guid.NewGuid();

        await Explorer.RunProjectionAsync<CargoTally>(nameof(CargoTally), streamId,
            ToRecords(streamId, new CrateLoaded("A")), startingState: null, Token);

        await using var query = _store.LightweightSession();
        (await query.LoadAsync<CargoTally>(streamId, Token)).ShouldBeNull();
    }

    private List<EventRecord> ToRecords(Guid streamId, params object[] bodies)
        => bodies.Select((body, index) => new EventRecord(
                Guid.NewGuid(),
                index + 1,
                index + 1,
                streamId.ToString(),
                _store.Options.EventGraph.EventMappingFor(body.GetType()).EventTypeName,
                JsonSerializer.Deserialize<JsonElement>(_store.Options.Serializer.ToJson(body)),
                null,
                DateTimeOffset.UtcNow,
                StorageConstants.DefaultTenantId,
                null))
            .ToList();
}

public class Warehouse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class Crate
{
    public Guid Id { get; set; }
    public string Label { get; set; } = string.Empty;
}

public class Vessel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class Container
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
}

public class ReeferContainer : Container
{
}

public class TankContainer : Container
{
}

public record CrateLoaded(string Code);

public record CrateUnloaded(string Code);

public class CargoTally
{
    public Guid Id { get; set; }
    public int Loaded { get; set; }

    public void Apply(CrateLoaded loaded) => Loaded++;

    public void Apply(CrateUnloaded unloaded) => Loaded--;
}
