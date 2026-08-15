using Fisher.Projections;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Projections;

/// <summary>
///     <c>Projections.Add&lt;T&gt;(lifecycle)</c> — registering a projection by type (fisher#76).
/// </summary>
/// <remarks>
///     The overload existed, inherited from <c>ProjectionGraph</c>, and went straight to
///     <c>All.Add</c> — so it skipped both halves of what registering a projection means on Fisher: the
///     event types were never added to the graph, and the published document type was never mapped, so
///     its table was never created. Both silent at registration. These tests are therefore about
///     equivalence rather than existence: what the generic form registers has to be what the instance
///     form registers.
/// </remarks>
public class registering_a_projection_by_type : IDisposable
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("generic-add");

    public void Dispose() => _database.Dispose();

    private DocumentStore StoreWith(Action<StoreOptions> configure)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            configure(options);
        });

    [Fact]
    public void the_generic_form_registers_what_the_instance_form_registers()
    {
        using var byType = StoreWith(o => o.Projections.Add<ByTypeProjection>(ProjectionLifecycle.Inline));
        using var byInstance = StoreWith(o =>
            o.Projections.Add(new ByTypeProjection(), ProjectionLifecycle.Inline));

        byType.Options.Schema.HasMappingFor(typeof(ByTypeSnapshot))
            .ShouldBe(byInstance.Options.Schema.HasMappingFor(typeof(ByTypeSnapshot)));

        EventNamesOf(byType).ShouldBe(EventNamesOf(byInstance));

        // ...and both are the non-empty answer, or the equality above would hold vacuously.
        byType.Options.Schema.HasMappingFor(typeof(ByTypeSnapshot)).ShouldBeTrue();
        EventNamesOf(byType).ShouldContain("by_type_happened");
    }

    [Fact]
    public async Task a_projection_registered_by_type_runs_and_its_table_exists()
    {
        await using var store = StoreWith(o =>
            o.Projections.Add<ByTypeProjection>(ProjectionLifecycle.Inline));

        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(Guid.NewGuid(), new ByTypeHappened("first"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = store.LightweightSession();

        (await query.LoadAsync<ByTypeSnapshot>("first", TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
    }

    [Fact]
    public void the_lifecycle_argument_is_carried()
    {
        using var store = StoreWith(o => o.Projections.Add<ByTypeProjection>(ProjectionLifecycle.Async));

        store.Options.Projections.All.Single().Lifecycle.ShouldBe(ProjectionLifecycle.Async);
    }

    [Fact]
    public void the_async_configuration_argument_is_applied()
    {
        using var store = StoreWith(o => o.Projections.Add<ByTypeProjection>(
            ProjectionLifecycle.Async, options => options.BatchSize = 37));

        store.Options.Projections.All.Single().Options.BatchSize.ShouldBe(37);
    }

    /// <remarks>
    ///     The constraint is weaker than the inherited one, which demanded <c>IProjectionSource</c>. The
    ///     instance overload wraps a bare <see cref="IProjection" />, so requiring more of the generic
    ///     form would refuse a projection the store runs perfectly well.
    /// </remarks>
    [Fact]
    public void a_bare_projection_can_be_registered_by_type_too()
    {
        using var store = StoreWith(o => o.Projections.Add<BareByTypeProjection>(ProjectionLifecycle.Inline));

        store.Options.Projections.All.ShouldHaveSingleItem();
    }

    private static string[] EventNamesOf(DocumentStore store)
        => store.Options.EventGraph.AllKnownEventTypes().Select(x => x.EventTypeName).Order().ToArray();
}

public record ByTypeHappened(string Name);

// partial, because JasperFx's source generator emits the conventional-method dispatcher into it.
public partial class ByTypeProjection : EventProjection
{
    public ByTypeSnapshot Create(ByTypeHappened e) => new() { Id = e.Name };
}

public class ByTypeSnapshot
{
    public string Id { get; set; } = string.Empty;
}

public class BareByTypeProjection : ProjectionBase, IProjection
{
    public Task ApplyAsync(IDocumentSession operations, IReadOnlyList<IEvent> events,
        CancellationToken cancellation) => Task.CompletedTask;
}
