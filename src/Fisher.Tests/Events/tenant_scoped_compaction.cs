using JasperFx;
using JasperFx.Events;
using JasperFx.MultiTenancy;

namespace Fisher.Tests.Events;

/// <summary>
///     jasperfx#910 — <c>IEventStore.CompactStreamAsync(stream, tenantId)</c>, the untyped compaction run
///     in one tenant's scope.
/// </summary>
/// <remarks>
///     Every fact writes the <b>same stream id in both tenants</b>, following
///     <see cref="tenant_scoped_read_only_event_store" />: a compaction that ignored the tenant would still
///     compact SOME stream with that id, so distinct ids per tenant would pass against exactly the
///     defect under test.
/// </remarks>
public class tenant_scoped_compaction
{
    private const string North = "north";
    private const string South = "south";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(TemporaryDatabase database, DocumentStore store)> ConjoinedStoreAsync()
    {
        var database = TemporaryDatabase.Create("compaction-conjoined");

        var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Events.AddEventType(typeof(CoinsEarned));
            options.Events.AddEventType(typeof(CoinsSpent));
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        return (database, store);
    }

    private static async Task StartAsync(DocumentStore store, string tenantId, Guid streamId, params object[] events)
    {
        await using var session = store.LightweightSession(tenantId);
        session.Events.StartStream<Purse>(streamId, events);
        await session.SaveChangesAsync(Token);
    }

    private static async Task<IReadOnlyList<IEvent>> FetchAsync(DocumentStore store, string tenantId, Guid streamId)
    {
        await using var session = store.LightweightSession(tenantId);
        return await session.Events.FetchStreamAsync(streamId, token: Token);
    }

    [Fact]
    public async Task compacts_the_tenant_s_stream_and_leaves_the_other_tenant_s_alone()
    {
        var (database, store) = await ConjoinedStoreAsync();

        try
        {
            var shared = Guid.NewGuid();
            await StartAsync(store, North, shared, new CoinsEarned(100), new CoinsSpent(30));
            await StartAsync(store, South, shared, new CoinsEarned(5), new CoinsEarned(6), new CoinsSpent(1));

            await ((IEventStore)store).CompactStreamAsync(shared, North, Token);

            (await FetchAsync(store, North, shared)).ShouldHaveSingleItem().Data
                .ShouldBeOfType<Compacted<Purse>>().Snapshot.Balance.ShouldBe(70);

            // The other tenant's stream of the SAME id is untouched.
            (await FetchAsync(store, South, shared)).Count.ShouldBe(3);
        }
        finally
        {
            await store.DisposeAsync();
            database.Dispose();
        }
    }

    /// <summary>
    ///     The failure this exists to remove: the tenant-less overload reads stream state in the default
    ///     scope, where a tenant's stream is not, and says it does not exist.
    /// </summary>
    [Fact]
    public async Task the_tenant_less_overload_cannot_see_a_tenant_s_stream()
    {
        var (database, store) = await ConjoinedStoreAsync();

        try
        {
            var streamId = Guid.NewGuid();
            await StartAsync(store, North, streamId, new CoinsEarned(100), new CoinsSpent(30));

            await Should.ThrowAsync<InvalidOperationException>(() =>
                ((IEventStore)store).CompactStreamAsync(streamId, Token));

            (await FetchAsync(store, North, streamId)).Count.ShouldBe(2);
        }
        finally
        {
            await store.DisposeAsync();
            database.Dispose();
        }
    }

    [Fact]
    public async Task a_tenant_on_a_store_that_is_not_multi_tenanted_is_refused()
    {
        var database = TemporaryDatabase.Create("compaction-untenanted");
        var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
        });

        try
        {
            await Should.ThrowAsync<NotSupportedException>(() =>
                ((IEventStore)store).CompactStreamAsync(Guid.NewGuid(), North, Token));
        }
        finally
        {
            await store.DisposeAsync();
            database.Dispose();
        }
    }
}
