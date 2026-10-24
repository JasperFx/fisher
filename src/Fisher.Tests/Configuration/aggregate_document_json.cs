using JasperFx;
using JasperFx.Documents;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Configuration;

/// <summary>
///     jasperfx#1082: <c>IDocumentStoreDiagnostics.AggregateDocumentJsonAsync</c> — a document rebuilt now
///     from its stream through the registered single-stream projection, rendered as Fisher would store it,
///     with nothing stored — and <c>DocumentTypeRef.SingleStreamProjection</c> naming that projection.
/// </summary>
/// <remarks>
///     The comparison it serves is stored-versus-rebuilt, so the facts that matter are the two ends of it:
///     rebuilt JSON is byte-equal to what the inline projection stored, and differs once the stored row has
///     been tampered with. Byte-equality is only possible because both go through the store's serializer,
///     enum storage and casing included — which is why the store renders it rather than a console.
/// </remarks>
public class aggregate_document_json : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("aggregate-json");
    private DocumentStore _store = null!;

    private CancellationToken Token => TestContext.Current.CancellationToken;
    private IDocumentStoreDiagnostics Diagnostics => _store;

    private static readonly string MooringType = typeof(Mooring).FullName!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;

            options.Schema.For<Lighthouse>();
            options.Schema.For<Mooring>().MultiTenanted();
            options.Schema.For<HarbourTally>().MultiTenanted();

            options.Projections.Snapshot<Mooring>(SnapshotLifecycle.Inline);
            options.Projections.Add(new HarbourTallyProjection(), ProjectionLifecycle.Inline);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<Guid> MoorAsync(string? tenantId, params object[] events)
    {
        var id = Guid.NewGuid();
        await using var session = _store.LightweightSession(tenantId);
        session.Events.StartStream<Mooring>(id, events);
        await session.SaveChangesAsync(Token);
        return id;
    }

    [Fact]
    public async Task the_rebuilt_json_is_exactly_what_the_inline_projection_stored()
    {
        var id = await MoorAsync(null, new VesselMoored("Kittiwake", "Pier 4"), new MooringInspected(MooringCondition.Worn),
            new VesselMoored("Kittiwake", "Pier 7"));

        var stored = await Diagnostics.LoadDocumentAsync(MooringType, id.ToString(), null, Token);
        var rebuilt = await Diagnostics.AggregateDocumentJsonAsync(MooringType, id.ToString(), null, Token);

        stored.ShouldNotBeNull();
        rebuilt.ShouldNotBeNull();
        rebuilt.ShouldBe(stored.Json);

        // The store's serializer, not a default one: camelCase members, and the enum as Fisher stores it.
        rebuilt.ShouldContain("\"berth\":\"Pier 7\"");
        rebuilt.ShouldContain("\"condition\":1");
    }

    [Fact]
    public async Task a_tampered_stored_row_no_longer_matches_and_nothing_is_written()
    {
        var id = await MoorAsync(null, new VesselMoored("Puffin", "Pier 1"));

        await using (var connection = await _store.Database.OpenConnectionAsync(Token))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "update fi_doc_mooring set data = json_set(data, '$.berth', 'Nowhere') where id = $id";
            command.Parameters.AddWithValue("$id", id.ToString());
            (await command.ExecuteNonQueryAsync(Token)).ShouldBe(1);
        }

        var stored = await Diagnostics.LoadDocumentAsync(MooringType, id.ToString(), null, Token);
        var rebuilt = await Diagnostics.AggregateDocumentJsonAsync(MooringType, id.ToString(), null, Token);

        rebuilt.ShouldNotBe(stored!.Json);
        rebuilt.ShouldContain("Pier 1");

        // Rebuilding stored nothing: the tampered row is still there.
        (await Diagnostics.LoadDocumentAsync(MooringType, id.ToString(), null, Token))!.Json
            .ShouldContain("Nowhere");
    }

    [Fact]
    public async Task the_stream_is_the_tenants_own()
    {
        var north = await MoorAsync("north", new VesselMoored("Gannet", "North Quay"));

        (await Diagnostics.AggregateDocumentJsonAsync(MooringType, north.ToString(), "north", Token))
            .ShouldNotBeNull().ShouldContain("North Quay");

        // The same id in the default tenant is no stream at all.
        (await Diagnostics.AggregateDocumentJsonAsync(MooringType, north.ToString(), null, Token)).ShouldBeNull();
        (await Diagnostics.AggregateDocumentJsonAsync(MooringType, north.ToString(), "  ", Token)).ShouldBeNull();
    }

    [Fact]
    public async Task no_stream_is_null()
    {
        (await Diagnostics.AggregateDocumentJsonAsync(MooringType, Guid.NewGuid().ToString(), null, Token))
            .ShouldBeNull();

        // An id that cannot be a stream id is no stream either, as a load answers it.
        (await Diagnostics.AggregateDocumentJsonAsync(MooringType, "not-a-guid", null, Token)).ShouldBeNull();
    }

    [Fact]
    public async Task an_aggregate_the_fold_deletes_is_null()
    {
        var id = await MoorAsync(null, new VesselMoored("Tern", "Pier 2"), new MooringReleased());

        (await Diagnostics.AggregateDocumentJsonAsync(MooringType, id.ToString(), null, Token)).ShouldBeNull();
    }

    [Fact]
    public async Task a_plain_document_type_is_refused_naming_it()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            Diagnostics.AggregateDocumentJsonAsync(typeof(Lighthouse).FullName!, Guid.NewGuid().ToString(), null, Token));

        ex.Message.ShouldContain(typeof(Lighthouse).FullName!);
    }

    [Fact]
    public async Task a_multi_stream_projection_type_is_refused_naming_it()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            Diagnostics.AggregateDocumentJsonAsync(typeof(HarbourTally).FullName!, "Pier 4", null, Token));

        ex.Message.ShouldContain(typeof(HarbourTally).FullName!);
    }

    [Fact]
    public async Task an_unknown_type_is_an_argument_exception()
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            Diagnostics.AggregateDocumentJsonAsync("No.Such.Type", Guid.NewGuid().ToString(), null, Token));
    }

    [Fact]
    public async Task the_type_list_names_the_single_stream_projection_and_nothing_else()
    {
        var types = await Diagnostics.DocumentTypesAsync(Token);

        types.Single(x => x.TypeName == MooringType).SingleStreamProjection.ShouldBe(nameof(Mooring));
        types.Single(x => x.TypeName == typeof(Lighthouse).FullName).SingleStreamProjection.ShouldBeNull();
        types.Single(x => x.TypeName == typeof(HarbourTally).FullName).SingleStreamProjection.ShouldBeNull();
    }
}

public record VesselMoored(string Vessel, string Berth);

public record MooringInspected(MooringCondition Condition);

public record MooringReleased;

public enum MooringCondition
{
    Sound,
    Worn,
    Failed
}

public class Mooring
{
    public Guid Id { get; set; }
    public string Vessel { get; set; } = string.Empty;
    public string Berth { get; set; } = string.Empty;
    public MooringCondition Condition { get; set; }
    public int Moorings { get; set; }

    public void Apply(VesselMoored moored)
    {
        Vessel = moored.Vessel;
        Berth = moored.Berth;
        Moorings++;
    }

    public void Apply(MooringInspected inspected) => Condition = inspected.Condition;

    public bool ShouldDelete(MooringReleased _) => true;
}

public class HarbourTally
{
    public string Id { get; set; } = string.Empty;
    public int Arrivals { get; set; }
}

public partial class HarbourTallyProjection : Fisher.Projections.MultiStreamProjection<HarbourTally, string>
{
    public HarbourTallyProjection()
    {
        Identity<VesselMoored>(x => x.Berth);
    }

    public void Apply(VesselMoored _, HarbourTally tally) => tally.Arrivals++;
}
