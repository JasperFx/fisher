using JasperFx;
using JasperFx.Events;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Events;

/// <summary>
///     fisher#643 / jasperfx#1215: what <c>EventRewriteCompliance</c> does not reach. A rewrite writes each event
///     back the way an append would store it today, so an upcast event moves to its target's type and a binary
///     event keeps its body in <c>data_binary</c>.
/// </summary>
public class rewriting_stored_events : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("rewriting-stored-events");
    private readonly List<DocumentStore> _stores = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }

        _database.Dispose();
    }

    private async Task<DocumentStore> storeAsync(Action<StoreOptions> configure)
    {
        var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            configure(options);
        });

        _stores.Add(store);
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        return store;
    }

    private static async Task<EventRewriteProgress> rewriteAsync(IEventStore store, EventRewriteDefinition definition)
    {
        var database = (await store.AllDatabases()).Single();
        var run = await EventRewriteRun.FromMarksAsync(database, definition, Token);

        return (await store.RewriteStoredEventsAsync(run, Token)).ShouldHaveSingleItem();
    }

    private static EventRewriteDefinition aRewrite(params string[] transforms) => new()
    {
        Name = "rewrite" + Guid.NewGuid().ToString("N")[..12],
        Transforms = transforms.ToList()
    };

    private async Task<(string Type, string Data, byte[]? Binary)> storedRowAsync(Guid streamId, int version)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "select type, data, data_binary from fi_events where stream_id = @id and version = @version";
        command.Parameters.AddWithValue("@id", streamId.ToString());
        command.Parameters.AddWithValue("@version", version);

        await using var reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).ShouldBeTrue();

        return (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : (byte[])reader.GetValue(2));
    }

    [Fact]
    public async Task an_upcast_event_is_written_back_as_its_target_type()
    {
        var legacy = await storeAsync(options => options.Events.AddEventType(typeof(ApplicantFiled)));
        var streamId = Guid.NewGuid();
        await using (var session = legacy.LightweightSession())
        {
            session.Events.StartStream(streamId, new ApplicantFiled("Alice Baggins", "REF-1"));
            await session.SaveChangesAsync(Token);
        }

        var current = await storeAsync(options =>
        {
            options.Events.Upcasters.Upcast<ApplicantFiled, ApplicantRegistered>(
                old => new ApplicantRegistered(old.FullName, old.Reference));
            options.Events.AddRewriteTransform<ApplicantRegistered>("shout",
                x => x with { Name = x.Name.ToUpperInvariant() });
        });

        // Selected by the transform's type alone: the source's stored name comes along with it
        var total = await rewriteAsync(current, aRewrite("shout"));

        total.Visited.ShouldBe(1);
        total.Rewritten.ShouldBe(1);

        var row = await storedRowAsync(streamId, 1);
        row.Type.ShouldBe(current.Options.EventGraph.EventMappingFor(typeof(ApplicantRegistered)).EventTypeName);

        await using var query = current.LightweightSession();
        var events = await query.Events.FetchStreamAsync(streamId, token: Token);
        events.Single().Data.ShouldBe(new ApplicantRegistered("ALICE BAGGINS", "REF-1"));
    }

    [Fact]
    public async Task a_binary_event_keeps_its_body_in_data_binary()
    {
        var store = await storeAsync(options =>
        {
            options.Events.DefaultBinarySerializer = new CountingBinarySerializer();
            options.Events.AddEventType(typeof(SoundingTaken));
            options.Events.AddRewriteTransform<SoundingTaken>("deeper", x => x with { Depth = x.Depth + 1 });
        });

        var streamId = Guid.NewGuid();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(streamId, new SoundingTaken(10, "m"));
            await session.SaveChangesAsync(Token);
        }

        var before = await storedRowAsync(streamId, 1);

        var total = await rewriteAsync(store, aRewrite("deeper"));

        total.Rewritten.ShouldBe(1);
        total.BytesBefore.ShouldBe(before.Data.Length + before.Binary!.Length);

        var row = await storedRowAsync(streamId, 1);
        row.Data.ShouldBe("{}");
        row.Binary.ShouldNotBeNull();

        await using var query = store.LightweightSession();
        (await query.Events.FetchStreamAsync(streamId, token: Token)).Single().Data
            .ShouldBe(new SoundingTaken(11, "m"));
    }

    [Fact]
    public async Task only_events_stored_as_json_leaves_binary_events_alone()
    {
        var store = await storeAsync(options =>
        {
            options.Events.DefaultBinarySerializer = new CountingBinarySerializer();
            options.Events.AddEventType(typeof(SoundingTaken));
            options.Events.AddEventType(typeof(VoyageBegun));
            options.Events.AddRewriteTransform<SoundingTaken>("deeper", x => x with { Depth = x.Depth + 1 });
            options.Events.AddRewriteTransform<VoyageBegun>("renamed", x => x with { From = x.From + " harbour" });
        });

        var streamId = Guid.NewGuid();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(streamId, new VoyageBegun("Bree"), new SoundingTaken(10, "m"));
            await session.SaveChangesAsync(Token);
        }

        var definition = aRewrite("deeper", "renamed");
        definition.OnlyEventsStoredAsJson = true;

        var total = await rewriteAsync(store, definition);

        total.Visited.ShouldBe(1);
        total.Rewritten.ShouldBe(1);

        await using var query = store.LightweightSession();
        (await query.Events.FetchStreamAsync(streamId, token: Token)).Select(x => x.Data).ShouldBe(
            [new VoyageBegun("Bree harbour"), new SoundingTaken(10, "m")]);
    }

    [Fact]
    public async Task events_that_would_be_stored_the_same_are_not_written()
    {
        var store = await storeAsync(options => options.Events.AddEventType(typeof(VoyageBegun)));

        var streamId = Guid.NewGuid();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(streamId, new VoyageBegun("Bree"), new VoyageBegun("Archet"));
            await session.SaveChangesAsync(Token);
        }

        var definition = aRewrite();
        definition.AllEventTypes = true;

        var total = await rewriteAsync(store, definition);

        total.Visited.ShouldBe(2);
        total.Rewritten.ShouldBe(0);
        total.Completed.ShouldBeTrue();
    }

    [Fact]
    public async Task a_rewrite_that_selects_nothing_is_refused()
    {
        var store = await storeAsync(_ => { });

        await Should.ThrowAsync<InvalidOperationException>(() => rewriteAsync(store, aRewrite()));
    }
}
