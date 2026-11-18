using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Tags;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Events;

public record OrderRegionId(Guid Value);

public record RegionOrderPlaced(string Customer);

/// <summary>
///     fisher#632, port of marten#6080. Under conjoined tenancy, <c>AssignTagWhere</c> tagged every
///     tenant's matching events. Fisher's tag tables have no tenant column, so the other tenant's own tag
///     reads then found the tag.
/// </summary>
public class assign_tag_where_stays_in_its_tenant : IAsyncLifetime
{
    private const string Red = "red";
    private const string Blue = "blue";

    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("assign-tag-tenant");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Events.AddEventType<RegionOrderPlaced>();
            options.Events.RegisterTagType<OrderRegionId>("order_region");
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private string OrderedTypeName => _store.Options.EventGraph.EventMappingFor(typeof(RegionOrderPlaced)).EventTypeName;

    private async Task<Guid> OrderAsync(string tenantId, string customer)
    {
        await using var session = _store.LightweightSession(tenantId);
        var streamId = session.Events.StartStream(new RegionOrderPlaced(customer)).Id;
        await session.SaveChangesAsync(Token);
        return streamId;
    }

    private async Task<string[]> CustomersTaggedAsync(string tenantId, OrderRegionId region)
    {
        await using var query = _store.QuerySession(tenantId);
        var events = await query.Events.QueryByTagsAsync(new EventTagQuery().Or(region), Token);
        return events.Select(x => x.Data).OfType<RegionOrderPlaced>().Select(x => x.Customer).Order().ToArray();
    }

    // Read straight from storage: a tag read filters by tenant, so it does not see a tag on another tenant's event
    private async Task<long> TaggedEventsOfAsync(string tenantId)
    {
        await using var connection = await _store.Database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "select count(*) from fi_event_tag_order_region t join fi_events e on e.seq_id = t.seq_id where e.tenant_id = $tenant";
        command.Parameters.AddWithValue("$tenant", tenantId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Token));
    }

    [Fact]
    public async Task assign_tag_where_tags_only_the_session_tenants_events()
    {
        await OrderAsync(Red, "Alice");
        await OrderAsync(Red, "Bob");
        await OrderAsync(Blue, "Carol");

        var region = new OrderRegionId(Guid.NewGuid());

        // The predicate matches every tenant's events. The "or" is there because the tenant filter is added to it.
        var typeName = OrderedTypeName;
        await using (var session = _store.LightweightSession(Red))
        {
            session.Events.AssignTagWhere(x => x.EventTypeName == typeName || x.EventTypeName == "nothing", region);
            await session.SaveChangesAsync(Token);
        }

        (await TaggedEventsOfAsync(Red)).ShouldBe(2);
        (await TaggedEventsOfAsync(Blue)).ShouldBe(0);
        (await CustomersTaggedAsync(Red, region)).ShouldBe(["Alice", "Bob"]);
        (await CustomersTaggedAsync(Blue, region)).ShouldBeEmpty();

        // Blue's own assignment tags Blue's event and leaves Red's alone
        await using (var session = _store.LightweightSession(Blue))
        {
            session.Events.AssignTagWhere(x => x.EventTypeName == typeName, region);
            await session.SaveChangesAsync(Token);
        }

        (await TaggedEventsOfAsync(Red)).ShouldBe(2);
        (await TaggedEventsOfAsync(Blue)).ShouldBe(1);
        (await CustomersTaggedAsync(Red, region)).ShouldBe(["Alice", "Bob"]);
        (await CustomersTaggedAsync(Blue, region)).ShouldBe(["Carol"]);
    }

    [Fact]
    public async Task assign_tag_where_can_tag_an_archived_event()
    {
        var archived = await OrderAsync(Red, "Alice");
        await OrderAsync(Red, "Bob");

        await using (var session = _store.LightweightSession(Red))
        {
            session.Events.ArchiveStream(archived);
            await session.SaveChangesAsync(Token);
        }

        var region = new OrderRegionId(Guid.NewGuid());
        var typeName = OrderedTypeName;
        await using (var session = _store.LightweightSession(Red))
        {
            session.Events.AssignTagWhere(x => x.EventTypeName == typeName, region);
            await session.SaveChangesAsync(Token);
        }

        // A tag read leaves out archived events
        (await CustomersTaggedAsync(Red, region)).ShouldBe(["Bob"]);

        // and finds the archived one's tag once the stream is back
        await using (var session = _store.LightweightSession(Red))
        {
            session.Events.UnArchiveStream(archived);
            await session.SaveChangesAsync(Token);
        }

        (await CustomersTaggedAsync(Red, region)).ShouldBe(["Alice", "Bob"]);
    }
}
