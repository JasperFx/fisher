using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Tags;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Events;

public record HarbourId(Guid Value);

public class ManifestFiled
{
    public string Captain { get; set; } = string.Empty;
}

public record CrewSigned(string Name, string Address);

/// <summary>
///     fisher#574. Configuration and lookups reached through JasperFx's <see cref="EventRegistry" /> base
///     type have to land on the same state Fisher reads, because hiding a base member is not overriding
///     it.
/// </summary>
/// <remarks>
///     <para>
///         JasperFx 3.3.0 lifted masking, tag types, binary serializers and type resolution onto
///         <see cref="EventRegistry" /> as non-virtual members with state of their own. Fisher's
///         <c>EventGraph</c> carried its own copies with the same names (CS0108), so a store-agnostic
///         caller holding the graph as an <see cref="EventRegistry" /> — which is what
///         <c>IEventStore&lt;,&gt;.Registry</c> hands out, cast to the class — wrote to a rule list, a tag
///         list or a serializer registry Fisher never read, and read lists Fisher never wrote. Silent in
///         both directions.
///     </para>
///     <para>
///         Every fact goes through the base reference on one side and Fisher's own spelling on the
///         other, so a fix that only unified one direction still fails.
///     </para>
/// </remarks>
public class event_registry_base_typed_configuration : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("registry-base-typed");
    private readonly List<DocumentStore> _stores = [];
    private int _schemas;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }

        _database.Dispose();
    }

    private CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    ///     A store configured through its <see cref="EventRegistry" />, reached the way a store-agnostic
    ///     consumer reaches it: <c>IEventStore&lt;,&gt;.Registry</c>, cast to the class.
    /// </summary>
    private async Task<DocumentStore> StoreAsync(Action<EventRegistry> configureRegistry,
        Action<StoreOptions>? configure = null, bool applySchema = true)
    {
        var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.DatabaseSchemaName = $"registry_{Interlocked.Increment(ref _schemas)}";
            options.AutoCreateSchemaObjects = AutoCreate.All;
            configure?.Invoke(options);

            // The graph exists from StoreOptions' constructor, so this is configuration-time.
            configureRegistry(options.EventGraph);
        });

        _stores.Add(store);

        if (applySchema)
        {
            await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
        }

        return store;
    }

    private static EventRegistry RegistryOf(DocumentStore store)
        => (EventRegistry)((IEventStore<IDocumentSession, IQuerySession>)store).Registry;

    // ---- masking ----

    [Fact]
    public async Task an_action_masking_rule_registered_through_the_base_type_is_applied()
    {
        var store = await StoreAsync(registry =>
            registry.AddMaskingRuleForProtectedInformation<ManifestFiled>(x => x.Captain = "REDACTED"));

        var streamId = await StartAsync(store, new ManifestFiled { Captain = "Haddock" });

        await store.Advanced.ApplyEventDataMaskingAsync(x => x.IncludeStream(streamId), Token);

        ((ManifestFiled)(await EventsOfAsync(store, streamId))[0].Data).Captain.ShouldBe("REDACTED");
    }

    [Fact]
    public async Task a_func_masking_rule_registered_through_the_base_type_is_applied()
    {
        var store = await StoreAsync(registry =>
            registry.AddMaskingRuleForProtectedInformation<CrewSigned>(x => x with { Address = "REDACTED" }));

        var streamId = await StartAsync(store, new CrewSigned("Archie", "Marlinspike Hall"));

        await store.Advanced.ApplyEventDataMaskingAsync(x => x.IncludeStream(streamId), Token);

        ((CrewSigned)(await EventsOfAsync(store, streamId))[0].Data).Address.ShouldBe("REDACTED");
    }

    [Fact]
    public async Task the_base_types_try_mask_applies_a_rule_registered_through_fisher()
    {
        var store = await StoreAsync(_ => { },
            options => options.Events.AddMaskingRuleForProtectedInformation<ManifestFiled>(
                x => x.Captain = "REDACTED"));

        var @event = store.Options.EventGraph.BuildEvent(new ManifestFiled { Captain = "Haddock" });

        RegistryOf(store).TryMask(@event).ShouldBeTrue();
        ((ManifestFiled)@event.Data).Captain.ShouldBe("REDACTED");
    }

    // ---- tag types ----

    [Fact]
    public async Task a_tag_type_registered_through_the_base_type_gets_a_table_and_round_trips()
    {
        var store = await StoreAsync(registry => registry.RegisterTagType<HarbourId, Guid>(x => x.Value));

        var harbour = new HarbourId(Guid.NewGuid());

        await using (var session = store.LightweightSession())
        {
            var @event = session.Events.BuildEvent(new QuestStarted("Moored"));
            @event.WithTag(harbour);
            session.Events.StartStream(Guid.NewGuid(), @event);
            await session.SaveChangesAsync(Token);
        }

        await using var query = store.LightweightSession();
        var events = await query.Events.QueryByTagsAsync(new EventTagQuery().Or<HarbourId>(harbour), Token);

        events.Single().Data.ShouldBeOfType<QuestStarted>().Name.ShouldBe("Moored");
    }

    [Fact]
    public async Task the_base_type_sees_a_tag_type_registered_through_fisher()
    {
        ITagTypeRegistration registration = null!;
        var store = await StoreAsync(_ => { },
            options => registration = options.Events.RegisterTagType<HarbourId, Guid>(x => x.Value),
            applySchema: false);

        var registry = RegistryOf(store);

        registry.FindTagType(typeof(HarbourId)).ShouldBeSameAs(registration);
        registry.TagTypes.ShouldContain(registration);
    }

    [Fact]
    public async Task an_inferred_tag_type_keeps_fishers_table_suffix_through_the_base_type()
    {
        var store = await StoreAsync(registry => registry.RegisterTagType<HarbourId>(), applySchema: false);

        // Fisher's suffix rule, not JasperFx's default: the suffix names a table in a deployed store.
        store.Options.EventGraph.FindTagType(typeof(HarbourId))!.TableSuffix.ShouldBe("harbour_id");
    }

    // ---- binary event bodies ----

    [Fact]
    public async Task a_default_binary_serializer_set_through_the_base_type_is_used()
    {
        var serializer = new CountingBinarySerializer();
        var store = await StoreAsync(registry => registry.DefaultBinarySerializer = serializer);

        var streamId = await StartAsync(store, new SoundingTaken(12, "fathoms"));

        (await EventsOfAsync(store, streamId))[0].Data.ShouldBeOfType<SoundingTaken>().Depth.ShouldBe(12);
        serializer.Serialized.ShouldBe(1);
        (await BinaryColumnIsSetAsync(store, streamId)).ShouldBeTrue();
    }

    [Fact]
    public async Task a_per_type_binary_serializer_registered_through_the_base_type_is_used()
    {
        var serializer = new CountingBinarySerializer();
        var store = await StoreAsync(registry => registry.UseBinarySerializer<VoyageEnded>(serializer));

        var streamId = await StartAsync(store, new VoyageEnded("Lisbon"));

        (await EventsOfAsync(store, streamId))[0].Data.ShouldBeOfType<VoyageEnded>().To.ShouldBe("Lisbon");
        serializer.Serialized.ShouldBe(1);
        (await BinaryColumnIsSetAsync(store, streamId)).ShouldBeTrue();
    }

    [Fact]
    public async Task the_base_type_resolves_a_serializer_registered_through_fisher()
    {
        var serializer = new CountingBinarySerializer();
        var store = await StoreAsync(_ => { },
            options => options.Events.UseBinarySerializer<VoyageEnded>(serializer), applySchema: false);

        RegistryOf(store).ResolveBinarySerializerFor(typeof(VoyageEnded)).ShouldBeSameAs(serializer);
    }

    // ---- type resolution ----

    [Fact]
    public async Task the_base_type_resolves_an_aggregate_alias_fisher_recorded()
    {
        var store = await StoreAsync(_ => { });

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Voyage>(Guid.NewGuid(), new VoyageBegun("Bristol"));
            await session.SaveChangesAsync(Token);
        }

        var alias = store.Options.EventGraph.AggregateAliasFor(typeof(Voyage));

        RegistryOf(store).TryResolveAggregateType(alias).ShouldBe(typeof(Voyage));
        RegistryOf(store).TryResolveAggregateType("NoSuchAggregate").ShouldBeNull();
    }

    [Fact]
    public async Task the_base_type_resolves_the_dotnet_type_name_fisher_stores()
    {
        var store = await StoreAsync(_ => { }, applySchema: false);

        var stored = store.Options.EventGraph.EventMappingFor(typeof(QuestStarted)).DotNetTypeName;

        RegistryOf(store).ResolveEventType(stored).ShouldBe(typeof(QuestStarted));
        RegistryOf(store).ResolveEventType("Nowhere.NoSuchEvent, Nowhere").ShouldBeNull();
    }

    // ---- the database ----

    /// <remarks>
    ///     Weasel's <c>DatabaseBase.EnsureStorageExistsAsync</c> is not virtual and returns a
    ///     <c>ValueTask</c>, so Fisher's <c>IEventDatabase</c> member cannot override it. What the base path
    ///     needs is to know Fisher's features, which is <c>FindFeature</c>, and that one is virtual.
    /// </remarks>
    [Fact]
    public async Task weasels_ensure_storage_through_the_base_type_creates_the_event_tables()
    {
        var store = await StoreAsync(_ => { }, applySchema: false);

        Weasel.Core.Migrations.DatabaseBase<SqliteConnection> database = store.Database;
        await database.EnsureStorageExistsAsync(typeof(IEvent), Token);

        (await TablesAsync()).ShouldContain($"{store.Options.DatabaseSchemaName}_fi_events");
    }

    // ---- helpers ----

    private async Task<Guid> StartAsync(DocumentStore store, object data)
    {
        var streamId = Guid.NewGuid();

        await using var session = store.LightweightSession();
        session.Events.StartStream(streamId, data);
        await session.SaveChangesAsync(Token);

        return streamId;
    }

    private async Task<IReadOnlyList<IEvent>> EventsOfAsync(DocumentStore store, Guid streamId)
    {
        await using var session = store.LightweightSession();
        return await session.Events.FetchStreamAsync(streamId, token: Token);
    }

    private async Task<bool> BinaryColumnIsSetAsync(DocumentStore store, Guid streamId)
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"select data_binary is not null from {store.Options.EventGraph.EventsTableName} where stream_id = @id";
        command.Parameters.AddWithValue("@id", streamId.ToString());

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token)) == 1;
    }

    private async Task<List<string>> TablesAsync()
    {
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select name from sqlite_master where type = 'table'";

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
