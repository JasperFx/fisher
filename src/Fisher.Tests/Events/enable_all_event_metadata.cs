using JasperFx;

namespace Fisher.Tests.Events;

// fisher#619: the one-call counterpart of Marten's Events.MetadataConfig.EnableAll(), which since
// marten#6012 includes the user name. Mirrors enable_all_also_enables_the_user_name in Marten's
// EventSourcingTests/flexible_event_metadata.cs.
public class enable_all_event_metadata : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("enable-all-metadata");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.EnableAllEventMetadata();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public void turns_on_every_event_metadata_flag()
    {
        var events = _store.Options.Events;

        events.EnableCorrelationId.ShouldBeTrue();
        events.EnableCausationId.ShouldBeTrue();
        events.EnableHeaders.ShouldBeTrue();
        events.EnableUserName.ShouldBeTrue();
    }

    [Fact]
    public async Task enable_all_also_enables_the_user_name()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.LastModifiedBy = "Last Person";
            session.Events.StartStream<Quest>(streamId,
                new QuestStarted("Find the ring"),
                new MemberJoined("Frodo"),
                new MonsterSlain("Troll"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        var events = await query.Events.FetchStreamAsync(streamId, token: TestContext.Current.CancellationToken);

        events.ShouldNotBeEmpty();
        foreach (var @event in events)
        {
            @event.UserName.ShouldBe("Last Person");
        }
    }

    [Fact]
    public async Task stores_the_rest_of_the_session_metadata_too()
    {
        var streamId = Guid.NewGuid();

        await using (var session = _store.LightweightSession())
        {
            session.CorrelationId = "corr-1";
            session.CausationId = "cause-1";
            session.SetHeader("region", "eu-west");
            session.Events.StartStream<Quest>(streamId, new QuestStarted("Metadata"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _store.LightweightSession();
        var events = await query.Events.FetchStreamAsync(streamId, token: TestContext.Current.CancellationToken);

        events[0].CorrelationId.ShouldBe("corr-1");
        events[0].CausationId.ShouldBe("cause-1");
        events[0].Headers!["region"].ToString().ShouldBe("eu-west");
    }
}
