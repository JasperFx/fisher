using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Projections;

namespace Fisher.Tests.Events;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SpokenLine), "spoken")]
[JsonDerivedType(typeof(WrittenLine), "written")]
public abstract record ScriptLine;

public sealed record SpokenLine(string Text) : ScriptLine;

public sealed record WrittenLine(string Text) : ScriptLine;

public record ScriptOpened(string Scene);

public record ScriptAmended(string Scene, ScriptLine[] Lines);

public partial class ScriptDraft
{
    public Guid Id { get; set; }
    public int Lines { get; set; }

    public static ScriptDraft Create(ScriptOpened _) => new();

    public void Apply(ScriptAmended e) => Lines += e.Lines.Length;
}

public class SceneTally
{
    public string Id { get; set; } = "";
    public int Lines { get; set; }
}

public partial class SceneTallyProjection : Fisher.Projections.MultiStreamProjection<SceneTally, string>
{
    public SceneTallyProjection()
    {
        Identity<ScriptAmended>(x => x.Scene);
    }

    public void Apply(ScriptAmended e, SceneTally tally) => tally.Lines += e.Lines.Length;
}

/// <summary>
///     The parity check for marten#6107: Marten's step-through read every event body with a static
///     <c>JsonSerializerOptions</c>, so an event that needs the store's own settings failed to deserialize in
///     CritterWatch while the application read it fine.
/// </summary>
/// <remarks>
///     Fisher's step-through reads event bodies and states through <c>StoreOptions.Serializer</c> (fisher#412,
///     <c>DocumentStore.ProjectionReplay.cs</c>). The event here holds a polymorphic member whose discriminator is
///     not the first property, which System.Text.Json reads only with <c>AllowOutOfOrderMetadataProperties</c>, the
///     setting the reporter needed. Both step-through paths read it.
/// </remarks>
public class step_through_reads_with_the_stores_serializer : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("step-through-serializer");
    private DocumentStore _store = null!;

    // The discriminator after the member, as a store that does not keep property order can hand it back
    private const string AmendedJson =
        """{"scene":"one","lines":[{"text":"Hello","kind":"spoken"},{"text":"Exit","kind":"written"}]}""";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.ConfigureSerialization(configure: json => json.AllowOutOfOrderMetadataProperties = true);
            options.Events.AddEventType(typeof(ScriptOpened));
            options.Events.AddEventType(typeof(ScriptAmended));
            options.Projections.Snapshot<ScriptDraft>(SnapshotLifecycle.Inline);
            options.Projections.Add(new SceneTallyProjection(), ProjectionLifecycle.Inline);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private EventRecord recordOf<T>(Guid streamId, int version, string json) => new(
        Guid.NewGuid(), version, version, streamId.ToString(),
        _store.Options.EventGraph.EventMappingFor(typeof(T)).EventTypeName,
        JsonSerializer.Deserialize<JsonElement>(json), null, DateTimeOffset.UtcNow,
        StorageConstants.DefaultTenantId, null);

    [Fact]
    public void the_event_needs_the_stores_settings()
    {
        // Not vacuous: System.Text.Json's defaults cannot read this body
        Should.Throw<NotSupportedException>(() => JsonSerializer.Deserialize<ScriptAmended>(AmendedJson,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    [Fact]
    public async Task a_multi_stream_step_through_reads_the_event()
    {
        var result = await ((IEventStore)_store).RunMultiStreamProjectionAsync(nameof(SceneTally),
            [recordOf<ScriptAmended>(Guid.NewGuid(), 1, AmendedJson)], Token);

        var timeline = result.AggregatesByIdentity["one"];
        timeline.Steps.ShouldAllBe(x => x.Error == null);
        timeline.FinalState!.Value.GetProperty("lines").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task a_single_stream_step_through_reads_the_event()
    {
        var streamId = Guid.NewGuid();
        var timeline = await ((IEventStore)_store).RunProjectionAsync<ScriptDraft>(nameof(ScriptDraft), streamId,
            [recordOf<ScriptOpened>(streamId, 1, """{"scene":"one"}"""), recordOf<ScriptAmended>(streamId, 2, AmendedJson)],
            startingState: null, Token);

        timeline.Steps.ShouldAllBe(x => x.Error == null);
        timeline.FinalState!.Lines.ShouldBe(2);
    }
}
