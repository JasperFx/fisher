using System.Text.Json;
using System.Text.Json.Serialization;
using Fisher.Linq;
using Fisher.Tests.Documents;
using JasperFx;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Linq;

public readonly record struct CrewId(Guid Value);

public readonly record struct BerthNumber(int Value);

public readonly record struct VesselCode(string Value);

/// <summary>A wrapper nobody registers — it keeps the object shape.</summary>
public readonly record struct HullTag(string Value);

/// <summary>A wrapper with its own converter, which a registration must not override.</summary>
[JsonConverter(typeof(FlagCodeConverter))]
public readonly record struct FlagCode(string Value);

public class FlagCodeConverter : JsonConverter<FlagCode>
{
    public override FlagCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString()!.TrimStart('#'));

    public override void Write(Utf8JsonWriter writer, FlagCode value, JsonSerializerOptions options)
        => writer.WriteStringValue("#" + value.Value);
}

public class Vessel
{
    public VesselCode Id { get; set; }
    public CrewId Captain { get; set; }
    public BerthNumber? Berth { get; set; }
    public HullTag Hull { get; set; }
    public FlagCode Flag { get; set; }
    public List<CrewId> Crew { get; set; } = [];
    public Dictionary<CrewId, string> Roles { get; set; } = [];
}

/// <summary>
///     fisher#356 — a registered wrapper is stored as the primitive it wraps, so LINQ over a member of
///     that type works like LINQ over the primitive.
/// </summary>
/// <remarks>
///     <para>
///         Before this, System.Text.Json wrote <c>CrewId</c> as <c>{"value":"…"}</c>. Loading a document
///         never noticed, but <c>Where(x =&gt; x.Captain == id)</c> failed to bind the wrapper and
///         <c>Select(x =&gt; x.Captain)</c> read a JSON object. #351 had fixed only the identity, whose
///         column already held the inner value.
///     </para>
///     <para>
///         The facts that matter most are the two compatibility ones: rows written in the object form
///         still load, and a wrapper nobody registered keeps its shape. That second fact is why this
///         applies to registered types only.
///     </para>
/// </remarks>
public class strong_typed_members : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("strong_typed_members");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.RegisterValueType<VesselCode>();
            options.RegisterValueType<CrewId>();
            options.RegisterValueType<BerthNumber>();
            options.RegisterValueType<FlagCode>();
            options.Schema.For<Vessel>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task a_registered_wrapper_is_stored_as_its_primitive()
    {
        var captain = new CrewId(Guid.NewGuid());
        await StoreAsync(new Vessel { Id = new VesselCode("v1"), Captain = captain, Berth = new BerthNumber(4) });

        var json = await DataAsync("v1");

        json.GetProperty("captain").GetString().ShouldBe(captain.Value.ToString());
        json.GetProperty("berth").GetInt32().ShouldBe(4);
        json.GetProperty("id").GetString().ShouldBe("v1");
    }

    [Fact]
    public async Task an_unregistered_wrapper_keeps_its_object_shape()
    {
        await StoreAsync(new Vessel { Id = new VesselCode("v2"), Hull = new HullTag("steel") });

        var json = await DataAsync("v2");

        json.GetProperty("hull").ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public async Task a_wrapper_with_its_own_converter_is_left_to_it()
    {
        await StoreAsync(new Vessel { Id = new VesselCode("v3"), Flag = new FlagCode("NO") });

        (await DataAsync("v3")).GetProperty("flag").GetString().ShouldBe("#NO");

        await using var session = _store.QuerySession();
        (await session.LoadAsync<Vessel>(new VesselCode("v3"), Token))!.Flag.ShouldBe(new FlagCode("NO"));
    }

    [Fact]
    public async Task where_compares_a_wrapper_member()
    {
        var captain = new CrewId(Guid.NewGuid());
        await StoreAsync(new Vessel { Id = new VesselCode("a"), Captain = captain });
        await StoreAsync(new Vessel { Id = new VesselCode("b"), Captain = new CrewId(Guid.NewGuid()) });

        await using var session = _store.QuerySession();
        var found = await session.Query<Vessel>().Where(x => x.Captain == captain).ToListAsync(Token);

        found.Select(x => x.Id).ShouldBe([new VesselCode("a")]);
    }

    [Fact]
    public async Task where_compares_a_nullable_wrapper_member_and_orders_by_it()
    {
        await StoreAsync(new Vessel { Id = new VesselCode("c"), Berth = new BerthNumber(9) });
        await StoreAsync(new Vessel { Id = new VesselCode("d"), Berth = new BerthNumber(2) });
        await StoreAsync(new Vessel { Id = new VesselCode("e") });

        await using var session = _store.QuerySession();

        (await session.Query<Vessel>().Where(x => x.Berth == new BerthNumber(9)).ToListAsync(Token))
            .Single().Id.ShouldBe(new VesselCode("c"));

        var ordered = await session.Query<Vessel>().Where(x => x.Berth != null)
            .OrderBy(x => x.Berth).Select(x => x.Id).ToListAsync(Token);

        ordered.ShouldBe([new VesselCode("d"), new VesselCode("c")]);
    }

    [Fact]
    public async Task where_through_the_value_property()
    {
        var captain = new CrewId(Guid.NewGuid());
        await StoreAsync(new Vessel { Id = new VesselCode("f"), Captain = captain, Berth = new BerthNumber(12) });

        await using var session = _store.QuerySession();

        (await session.Query<Vessel>().Where(x => x.Captain.Value == captain.Value).CountAsync(Token))
            .ShouldBe(1);
        (await session.Query<Vessel>().Where(x => x.Berth!.Value.Value > 10).CountAsync(Token))
            .ShouldBe(1);
        (await session.Query<Vessel>().Where(x => x.Id.Value == "f").CountAsync(Token)).ShouldBe(1);
    }

    [Fact]
    public async Task where_compares_the_identity_by_its_wrapper()
    {
        await StoreAsync(new Vessel { Id = new VesselCode("g") });

        await using var session = _store.QuerySession();
        var id = new VesselCode("g");

        (await session.Query<Vessel>().Where(x => x.Id == id).CountAsync(Token)).ShouldBe(1);
    }

    [Fact]
    public async Task is_one_of_a_set_of_wrappers()
    {
        var first = new CrewId(Guid.NewGuid());
        var second = new CrewId(Guid.NewGuid());
        await StoreAsync(new Vessel { Id = new VesselCode("h"), Captain = first });
        await StoreAsync(new Vessel { Id = new VesselCode("i"), Captain = second });
        await StoreAsync(new Vessel { Id = new VesselCode("j"), Captain = new CrewId(Guid.NewGuid()) });

        await using var session = _store.QuerySession();
        var wanted = new[] { first, second };

        var found = await session.Query<Vessel>().Where(x => wanted.Contains(x.Captain))
            .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(Token);

        found.ShouldBe([new VesselCode("h"), new VesselCode("i")]);
    }

    [Fact]
    public async Task a_collection_of_wrappers_contains_one()
    {
        var sailor = new CrewId(Guid.NewGuid());
        await StoreAsync(new Vessel { Id = new VesselCode("k"), Crew = [sailor, new CrewId(Guid.NewGuid())] });
        await StoreAsync(new Vessel { Id = new VesselCode("l"), Crew = [new CrewId(Guid.NewGuid())] });

        await using var session = _store.QuerySession();

        (await session.Query<Vessel>().Where(x => x.Crew.Contains(sailor)).Select(x => x.Id).ToListAsync(Token))
            .ShouldBe([new VesselCode("k")]);
        (await session.Query<Vessel>().Where(x => x.Crew.Any(c => c.Value == sailor.Value))
                .Select(x => x.Id).ToListAsync(Token))
            .ShouldBe([new VesselCode("k")]);
    }

    [Fact]
    public async Task select_reads_a_wrapper_member()
    {
        var captain = new CrewId(Guid.NewGuid());
        await StoreAsync(new Vessel { Id = new VesselCode("m"), Captain = captain, Berth = new BerthNumber(3) });

        await using var session = _store.QuerySession();
        var shaped = await session.Query<Vessel>().Where(x => x.Id == new VesselCode("m"))
            .Select(x => new { x.Captain, x.Berth }).SingleAsync(Token);

        shaped.Captain.ShouldBe(captain);
        shaped.Berth.ShouldBe(new BerthNumber(3));
    }

    [Fact]
    public async Task a_dictionary_keyed_by_a_wrapper_round_trips()
    {
        var mate = new CrewId(Guid.NewGuid());
        await StoreAsync(new Vessel { Id = new VesselCode("n"), Roles = { [mate] = "mate" } });

        (await DataAsync("n")).GetProperty("roles").GetProperty(mate.Value.ToString()).GetString().ShouldBe("mate");

        await using var session = _store.QuerySession();
        (await session.LoadAsync<Vessel>(new VesselCode("n"), Token))!.Roles[mate].ShouldBe("mate");
    }

    /// <summary>
    ///     A row written before the wrapper was registered holds the object form, and it still loads —
    ///     which is what makes registering a type on a live store safe.
    /// </summary>
    [Fact]
    public async Task a_row_written_in_the_object_form_still_loads()
    {
        var captain = Guid.NewGuid();
        await StoreAsync(new Vessel { Id = new VesselCode("o") });

        await using (var conn = new SqliteConnection(_database.ConnectionString))
        {
            await conn.OpenAsync(Token);
            await using var command = conn.CreateCommand();
            command.CommandText = """
                update fi_doc_vessel set data = json_set(data,
                    '$.captain', json(@captain), '$.berth', json('{"value":7}'), '$.id', json('{"value":"o"}'))
                where id = 'o'
                """;
            command.Parameters.AddWithValue("@captain", $$"""{"value":"{{captain}}"}""");
            await command.ExecuteNonQueryAsync(Token);
        }

        await using var session = _store.QuerySession();
        var vessel = (await session.LoadAsync<Vessel>(new VesselCode("o"), Token))!;

        vessel.Captain.ShouldBe(new CrewId(captain));
        vessel.Berth.ShouldBe(new BerthNumber(7));
        vessel.Id.ShouldBe(new VesselCode("o"));
    }

    private async Task StoreAsync(Vessel vessel)
    {
        await using var session = _store.LightweightSession();
        session.Store(vessel);
        await session.SaveChangesAsync(Token);
    }

    private async Task<JsonElement> DataAsync(string id)
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync(Token);
        await using var command = conn.CreateCommand();
        command.CommandText = "select data from fi_doc_vessel where id = @id";
        command.Parameters.AddWithValue("@id", id);

        var text = (string)(await command.ExecuteScalarAsync(Token))!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
