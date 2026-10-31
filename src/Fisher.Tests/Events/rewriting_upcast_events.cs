using System.Text.Json;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Upcasting;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Events;

public record ApplicantFiled(string FullName, string Reference);

public record ApplicantRegistered(string Name, string Reference);

/// <summary>
///     fisher#494, from marten#5642. An upcast event reads as its target type, so a rewrite has to store
///     the row as an append would today. Left under the source's type name, the next read upcasts data
///     already in the new shape, and masking destroys the event rather than masking it.
/// </summary>
public class rewriting_upcast_events : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("rewriting-upcast");
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

    private async Task<DocumentStore> StoreAsync(Action<StoreOptions> configure)
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

    private async Task<Guid> AppendLegacyAsync()
    {
        var legacy = await StoreAsync(options => options.Events.AddEventType(typeof(ApplicantFiled)));

        var streamId = Guid.NewGuid();

        await using var session = legacy.LightweightSession();
        session.Events.StartStream(streamId, new ApplicantFiled("Alice Baggins", "REF-1"));
        await session.SaveChangesAsync(Token);

        return streamId;
    }

    private static void Upcast(StoreOptions options)
        => options.Events.Upcasters.Upcast<ApplicantFiled, ApplicantRegistered>(
            old => new ApplicantRegistered(old.FullName, old.Reference));

    private async Task<(string type, string dotnetType)> StoredTypeAsync(Guid streamId)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select type, dotnet_type from fi_events where stream_id = @id";
        command.Parameters.AddWithValue("@id", streamId.ToString());

        await using var reader = await command.ExecuteReaderAsync(Token);
        await reader.ReadAsync(Token);

        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<ApplicantRegistered> ReadAsync(DocumentStore store, Guid streamId)
    {
        await using var session = store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(streamId, token: Token);

        return (ApplicantRegistered)events.Single().Data;
    }

    [Fact]
    public async Task masking_an_upcast_event_masks_it_and_keeps_every_other_member()
    {
        var streamId = await AppendLegacyAsync();

        var store = await StoreAsync(options =>
        {
            Upcast(options);
            options.Events.AddMaskingRuleForProtectedInformation<ApplicantRegistered>(
                x => x with { Name = "****" });
        });

        await store.Advanced.ApplyEventDataMaskingAsync(x => x.IncludeStream(streamId));

        var masked = await ReadAsync(store, streamId);

        masked.Name.ShouldBe("****");
        masked.Reference.ShouldBe("REF-1");
    }

    [Fact]
    public async Task an_overwrite_moves_the_row_to_the_target_type()
    {
        var streamId = await AppendLegacyAsync();
        var store = await StoreAsync(Upcast);

        await using (var session = store.LightweightSession())
        {
            var @event = (await session.Events.FetchStreamAsync(streamId, token: Token)).Single();
            @event.Data.ShouldBeOfType<ApplicantRegistered>();

            session.Events.OverwriteEvent(@event);
            await session.SaveChangesAsync(Token);
        }

        var mapping = store.Options.EventGraph.EventMappingFor(typeof(ApplicantRegistered));

        (await StoredTypeAsync(streamId)).ShouldBe((mapping.EventTypeName, mapping.DotNetTypeName));
        (await ReadAsync(store, streamId)).Name.ShouldBe("Alice Baggins");
    }

    /// <summary>
    ///     The discriminating half for a store that has since dropped the upcast: the row must stand on
    ///     its own, with nothing left depending on the transformation.
    /// </summary>
    [Fact]
    public async Task a_rewritten_row_no_longer_needs_the_upcast()
    {
        var streamId = await AppendLegacyAsync();
        var store = await StoreAsync(Upcast);

        await using (var session = store.LightweightSession())
        {
            session.Events.OverwriteEvent((await session.Events.FetchStreamAsync(streamId, token: Token)).Single());
            await session.SaveChangesAsync(Token);
        }

        var without = await StoreAsync(options => options.Events.AddEventType(typeof(ApplicantRegistered)));

        (await ReadAsync(without, streamId)).Name.ShouldBe("Alice Baggins");
    }

    /// <summary>
    ///     A binary source read through its upcast is written back as the JSON its target stores. Left in
    ///     place, the BLOB is what every later read would dispatch on, so the rewrite would never be seen,
    ///     which for masking means the original personal data still being there.
    /// </summary>
    [Fact]
    public async Task an_overwrite_of_a_binary_source_clears_the_blob()
    {
        var legacy = await StoreAsync(options =>
            options.Events.UseBinarySerializer<ApplicantFiled>(new JsonBytesSerializer()));

        var streamId = Guid.NewGuid();

        await using (var session = legacy.LightweightSession())
        {
            session.Events.StartStream(streamId, new ApplicantFiled("Alice Baggins", "REF-1"));
            await session.SaveChangesAsync(Token);
        }

        var store = await StoreAsync(options =>
        {
            options.Events.UseBinarySerializer<ApplicantFiled>(new JsonBytesSerializer());
            Upcast(options);
            options.Events.AddMaskingRuleForProtectedInformation<ApplicantRegistered>(
                x => x with { Name = "****" });
        });

        await store.Advanced.ApplyEventDataMaskingAsync(x => x.IncludeStream(streamId));

        (await ReadAsync(store, streamId)).Name.ShouldBe("****");

        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from fi_events where data_binary is not null";

        ((long)(await command.ExecuteScalarAsync(Token))!).ShouldBe(0);
    }

    [Fact]
    public async Task an_ordinary_overwrite_leaves_the_type_alone()
    {
        var store = await StoreAsync(options => options.Events.AddEventType(typeof(ApplicantRegistered)));
        var streamId = Guid.NewGuid();

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(streamId, new ApplicantRegistered("Bilbo", "REF-2"));
            await session.SaveChangesAsync(Token);
        }

        var before = await StoredTypeAsync(streamId);

        await using (var session = store.LightweightSession())
        {
            var @event = (await session.Events.FetchStreamAsync(streamId, token: Token)).Single();
            session.Events.OverwriteEvent(@event);
            await session.SaveChangesAsync(Token);
        }

        (await StoredTypeAsync(streamId)).ShouldBe(before);
    }

    /// <summary>
    ///     An upcast on the target's own name leaves nowhere else to store the row, so the rewrite would
    ///     read back wrong. That one is refused rather than silently corrupting the event.
    /// </summary>
    [Fact]
    public async Task an_upcast_on_the_events_own_name_is_refused_by_name()
    {
        var store = await StoreAsync(options =>
        {
            options.Events.AddEventType(typeof(ApplicantRegistered));
            options.Events.Upcasters.Upcast<ApplicantRegistered>(
                EventTypeExtensions.GetEventTypeName<ApplicantRegistered>(),
                _ => new ApplicantRegistered("unreachable", "unreachable"));
        });

        var @event = store.Options.EventGraph.BuildEvent(new ApplicantRegistered("Frodo", "REF-3"));
        @event.Sequence = 1;

        await using var session = store.LightweightSession();

        var ex = Should.Throw<InvalidOperationException>(() => session.Events.OverwriteEvent(@event));
        ex.Message.ShouldContain("upcast");
    }

    private sealed class JsonBytesSerializer : IEventBinarySerializer
    {
        public byte[] Serialize(Type type, object data) => JsonSerializer.SerializeToUtf8Bytes(data, type);

        public object Deserialize(Type type, byte[] data) => JsonSerializer.Deserialize(data, type)!;
    }
}
