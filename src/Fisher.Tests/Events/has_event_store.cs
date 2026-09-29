using JasperFx;
using JasperFx.Events;

namespace Fisher.Tests.Events;

/// <summary>
///     jasperfx#914 — <c>IEventStore.HasEventStore</c>: does this store have an event store at all?
/// </summary>
/// <remarks>
///     The document-only case is the one that matters: the interface default is <c>true</c>, so a store
///     that did not implement the member would pass every other fact here and fail only this one.
/// </remarks>
public class has_event_store
{
    private static DocumentStore Store(TemporaryDatabase database, Action<StoreOptions>? configure = null)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            configure?.Invoke(options);
        });

    [Fact]
    public async Task a_document_only_store_has_no_event_store()
    {
        using var database = TemporaryDatabase.Create("has-event-store-docs");
        await using var store = Store(database);

        ((IEventStore)store).HasEventStore.ShouldBeFalse();
    }

    [Fact]
    public async Task a_registered_event_type_is_an_event_store()
    {
        using var database = TemporaryDatabase.Create("has-event-store-type");
        await using var store = Store(database, o => o.Events.AddEventType(typeof(CoinsEarned)));

        ((IEventStore)store).HasEventStore.ShouldBeTrue();
    }

    [Fact]
    public async Task archived_alone_is_not_an_event_store()
    {
        // Marten's rule: Archived is infrastructure every store knows about, not evidence the
        // application appends events.
        using var database = TemporaryDatabase.Create("has-event-store-archived");
        await using var store = Store(database, o => o.Events.AddEventType(typeof(Archived)));

        ((IEventStore)store).HasEventStore.ShouldBeFalse();
    }

    [Fact]
    public async Task a_registered_projection_is_an_event_store()
    {
        using var database = TemporaryDatabase.Create("has-event-store-projection");
        await using var store = Store(database,
            o => o.Projections.Snapshot<Purse>(JasperFx.Events.Projections.SnapshotLifecycle.Inline));

        ((IEventStore)store).HasEventStore.ShouldBeTrue();
    }

    [Fact]
    public async Task an_event_type_registered_on_first_append_makes_it_one()
    {
        using var database = TemporaryDatabase.Create("has-event-store-lazy");
        await using var store = Store(database);
        ((IEventStore)store).HasEventStore.ShouldBeFalse("precondition");

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Purse>(Guid.NewGuid(), new CoinsEarned(1));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        ((IEventStore)store).HasEventStore.ShouldBeTrue("computed on every read, never cached");
    }
}
