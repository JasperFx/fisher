using System.Text.Json;
using System.Text.Json.Serialization;
using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

public class ShippingAddress
{
    public string City { get; set; } = "";
    public List<string> Lines { get; set; } = [];
}

public readonly record struct GridReference(int X, int Y);

/// <summary>A non-scalar type stored as a JSON string, which is what stops the target alone deciding.</summary>
[JsonConverter(typeof(MoneyConverter))]
public record Money(decimal Amount, string Currency);

public class MoneyConverter : JsonConverter<Money>
{
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var parts = reader.GetString()!.Split(' ');
        return new Money(decimal.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), parts[1]);
    }

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
        => writer.WriteStringValue(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{value.Amount} {value.Currency}"));
}

public class Consignment
{
    public Guid Id { get; set; }
    public string Note { get; set; } = "";
    public ShippingAddress Address { get; set; } = new();
    public ShippingAddress? Backup { get; set; }
    public GridReference Grid { get; set; }
    public GridReference? MaybeGrid { get; set; }
    public List<string> Tags { get; set; } = [];
    public int[] Scores { get; set; } = [];
    public Dictionary<string, int> Weights { get; set; } = [];
    public List<ShippingAddress> Stops { get; set; } = [];
    public Money Price { get; set; } = new(0, "USD");
}

public class ConsignmentSummary
{
    public string Note { get; set; } = "";
    public ShippingAddress? Address { get; set; }
    public List<string>? Tags { get; set; }
}

/// <summary>
///     fisher#489. <c>json_extract</c> returns an object or array as JSON text, and quoting that as a JSON
///     string meant no object could be built from it. Asserted against the whole-document load, as
///     <c>serializer_shaped_projections</c> is, because agreement between the two paths is the property.
/// </summary>
public class projecting_object_members : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("object-members");
    private DocumentStore _store = null!;

    private readonly Consignment _full = new()
    {
        Id = Guid.NewGuid(), Note = "{ not json",
        Address = new ShippingAddress { City = "Hobbiton", Lines = ["Bag End", "Bagshot Row"] },
        Backup = new ShippingAddress { City = "Bree" },
        Grid = new GridReference(1, 2), MaybeGrid = new GridReference(3, 4),
        Tags = ["fragile", "urgent"], Scores = [1, 2, 3], Weights = new() { ["a"] = 1, ["b"] = 2 },
        Stops = [new ShippingAddress { City = "Bree" }, new ShippingAddress { City = "Rivendell" }],
        Price = new Money(12.5m, "USD")
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Consignment>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        session.Store(_full);
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static void ShouldBeTheSame<T>(T actual, T expected)
        => JsonSerializer.Serialize(actual).ShouldBe(JsonSerializer.Serialize(expected));

    private async Task<T> SelectAsync<T>(System.Linq.Expressions.Expression<Func<Consignment, T>> selector)
        where T : notnull
    {
        await using var session = _store.QuerySession();
        return (await session.Query<Consignment>().Select(selector).ToListAsync(Token)).Single();
    }

    [Fact]
    public async Task an_object_member()
        => ShouldBeTheSame(await SelectAsync(x => x.Address), _full.Address);

    [Fact]
    public async Task a_collection_of_primitives()
    {
        ShouldBeTheSame(await SelectAsync(x => x.Tags), _full.Tags);
        ShouldBeTheSame(await SelectAsync(x => x.Scores), _full.Scores);
    }

    [Fact]
    public async Task a_dictionary()
        => ShouldBeTheSame(await SelectAsync(x => x.Weights), _full.Weights);

    [Fact]
    public async Task a_collection_of_objects()
        => ShouldBeTheSame(await SelectAsync(x => x.Stops), _full.Stops);

    [Fact]
    public async Task a_struct_member_nullable_or_not()
    {
        (await SelectAsync(x => x.Grid)).ShouldBe(_full.Grid);

        await using var session = _store.QuerySession();
        (await session.Query<Consignment>().Select(x => x.MaybeGrid).ToListAsync(Token)).Single()
            .ShouldBe(_full.MaybeGrid);
    }

    [Fact]
    public async Task inside_an_anonymous_type_and_a_named_one()
    {
        var anonymous = await SelectAsync(x => new { x.Id, x.Tags, x.Address });
        ShouldBeTheSame(anonymous.Tags, _full.Tags);
        ShouldBeTheSame(anonymous.Address, _full.Address);

        var named = await SelectAsync(x => new ConsignmentSummary { Note = x.Note, Address = x.Address, Tags = x.Tags });
        ShouldBeTheSame(named.Address, _full.Address);
        ShouldBeTheSame(named.Tags, _full.Tags);
    }

    [Fact]
    public async Task a_missing_object_comes_back_null()
    {
        await using (var writer = _store.LightweightSession())
        {
            writer.Store(new Consignment { Id = Guid.NewGuid(), Note = "z" });
            await writer.SaveChangesAsync(Token);
        }

        await using var session = _store.QuerySession();

        var backups = await session.Query<Consignment>().Select(x => x.Backup).ToListAsync(Token);

        backups.Count.ShouldBe(2);
        backups.Count(x => x is null).ShouldBe(1);
    }

    [Fact]
    public async Task a_type_with_a_string_converter_is_still_quoted()
        => (await SelectAsync(x => x.Price)).ShouldBe(_full.Price);

    /// <summary>The text looks like the start of an object, and it is a string member.</summary>
    [Fact]
    public async Task a_string_that_starts_like_json_stays_a_string()
        => (await SelectAsync(x => x.Note)).ShouldBe(_full.Note);
}
