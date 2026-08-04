using Fisher.Projections;
using Fisher.Tests.Events;
using JasperFx;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Projections;

/// <summary>
///     An <c>EventProjection</c> registered Inline, storing a document per event.
/// </summary>
/// <remarks>
///     The point under test is <c>EventProjection.storeEntity</c>: a <c>Create</c> method's return
///     value is routed there by the generated evolver, and it has to reach the same unit of work the
///     events are committing in rather than a connection of its own.
/// </remarks>
public class inline_event_projections : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("event-projection");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<QuestLogEntry>();
            options.Projections.Add(new QuestLogProjection(), ProjectionLifecycle.Inline);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task a_created_entity_is_stored_with_the_events_that_produced_it()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(streamId,
                new QuestStarted("Find the Ring"), new MonsterSlain("Balrog"));

            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();

        (await query.LoadAsync<QuestLogEntry>("quest:Find the Ring", TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
        (await query.LoadAsync<QuestLogEntry>("slain:Balrog", TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task nothing_is_stored_when_the_commit_never_happens()
    {
        await using (var session = _store.LightweightSession())
        {
            session.Events.StartStream(Guid.NewGuid(), new QuestStarted("Abandoned"));
            // deliberately no SaveChangesAsync
        }

        await using var query = _store.LightweightSession();

        (await query.LoadAsync<QuestLogEntry>("quest:Abandoned", TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }
}

// partial, because JasperFx's source generator emits the conventional-method dispatcher into this
// class — there is no runtime fallback for Project/Create.
public partial class QuestLogProjection : EventProjection
{
    public QuestLogEntry Create(QuestStarted e) => new() { Id = $"quest:{e.Name}", Description = e.Name };

    public QuestLogEntry Create(MonsterSlain e) => new() { Id = $"slain:{e.Monster}", Description = e.Monster };
}

public class QuestLogEntry
{
    public string Id { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
