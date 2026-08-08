using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Events;

/// <summary>
///     <c>Advanced.EventProjectionScenarioAsync</c> — fisher#42.
/// </summary>
/// <remarks>
///     The harness is JasperFx's and Fisher supplies only the store seam, the same shape
///     <c>FisherProjectionDaemon</c> has. So what is worth testing is the seam: that events appended
///     through it reach the projection, that a failed assertion is reported rather than swallowed, and
///     that its teardown clears the projection's documents without taking out anything else.
/// </remarks>
public class projection_scenarios : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("scenarios");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(o =>
        {
            o.ConnectionString = _database.ConnectionString;
            o.AutoCreateSchemaObjects = AutoCreate.All;
            o.Projections.Snapshot<Quest>(SnapshotLifecycle.Inline);
            o.Schema.For<Unrelated>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task a_scenario_asserts_against_projected_state()
    {
        var id = Guid.NewGuid();

        await _store.Advanced.EventProjectionScenarioAsync(scenario =>
        {
            scenario.Append(id, new QuestStarted("The Ring"));
            scenario.Append(id, new MemberJoined("Frodo"));

            scenario.DocumentShouldExist<Quest>(id, quest =>
            {
                quest.Name.ShouldBe("The Ring");
                quest.Members.ShouldBe(["Frodo"]);
            });
        }, Token);
    }

    [Fact]
    public async Task a_failed_assertion_is_reported()
    {
        var id = Guid.NewGuid();

        await Should.ThrowAsync<Exception>(() =>
            _store.Advanced.EventProjectionScenarioAsync(scenario =>
            {
                scenario.Append(id, new QuestStarted("The Ring"));
                scenario.DocumentShouldExist<Quest>(id, quest => quest.Name.ShouldBe("Something else"));
            }, Token));
    }

    [Fact]
    public async Task a_document_that_should_not_exist()
    {
        await _store.Advanced.EventProjectionScenarioAsync(scenario =>
        {
            scenario.Append(Guid.NewGuid(), new QuestStarted("One"));
            scenario.DocumentShouldNotExist<Quest>(Guid.NewGuid());
        }, Token);
    }

    /// <summary>
    ///     The teardown clears the event store and the projections' own documents — not every table.
    ///     A scenario is entitled to seed documents its projections do not produce, and clearing those
    ///     would make the harness quietly destructive.
    /// </summary>
    [Fact]
    public async Task the_teardown_leaves_unrelated_documents_alone()
    {
        var seeded = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.Store(new Unrelated { Id = seeded, Note = "keep me" });
            await session.SaveChangesAsync(Token);
        }

        await _store.Advanced.EventProjectionScenarioAsync(scenario =>
        {
            scenario.Append(Guid.NewGuid(), new QuestStarted("The Ring"));
        }, Token);

        await using var check = _store.LightweightSession();
        (await check.LoadAsync<Unrelated>(seeded, Token)).ShouldNotBeNull();
    }

    public record QuestStarted(string Name);

    public record MemberJoined(string Member);

    public class Quest
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public List<string> Members { get; set; } = [];

        public void Apply(QuestStarted started) => Name = started.Name;
        public void Apply(MemberJoined joined) => Members.Add(joined.Member);
    }

    public class Unrelated
    {
        public Guid Id { get; set; }
        public string Note { get; set; } = "";
    }
}
