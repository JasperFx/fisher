using System.Linq.Expressions;
using Fisher.Linq;
using JasperFx;
using JasperFx.Events;
using Weasel.Core;
using Shouldly;

namespace Fisher.Tests.Linq;

public enum Weather
{
    Calm,
    Breezy,
    Stormy
}

public class Logbook
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int? Count { get; set; }
    public string? Skipper { get; set; }
    public decimal? Fuel { get; set; }
    public float? Draft { get; set; }
    public DateTimeOffset? Sailed { get; set; }
    public Weather? Sky { get; set; }
    public bool? Logged { get; set; }

    // Non-nullable, and absent from some rows: deserialized as 0, read by SQL as NULL.
    public int Crew { get; set; }
}

public record LogbookEntered(string Name, int? Knots);

/// <summary>
///     fisher#608: C# says <c>x.N != 3</c> is true when <c>x.N</c> is null, and SQL says
///     <c>json_extract(…) != ?</c> is NULL there, which a WHERE drops. The same holds for every comparison
///     under a <c>!</c>. LINQ to Objects over the documents the store hands back is the oracle throughout,
///     so a member absent from the JSON is whatever the serializer made of it.
/// </summary>
public class nullable_negation_semantics : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("nullable-negation");
    private DocumentStore _store = null!;
    private DocumentStore _stringEnums = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Noon = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static List<Logbook> Seed() =>
    [
        new()
        {
            Name = "a", Count = 3, Skipper = "anne", Fuel = 3.5m, Draft = 0.3f, Sailed = Noon,
            Sky = Weather.Breezy, Logged = true, Crew = 3
        },
        new()
        {
            Name = "b", Count = 5, Skipper = "bob", Fuel = 1.25m, Draft = 1.5f, Sailed = Noon.AddHours(2),
            Sky = Weather.Stormy, Logged = false, Crew = 5
        },
        new()
        {
            Name = "c", Count = 1, Skipper = "carl", Fuel = 7m, Draft = 0.1f,
            // The same instant at another offset.
            Sailed = Noon.ToOffset(TimeSpan.FromHours(-5)), Sky = Weather.Calm, Logged = true, Crew = 0
        },
        // Every nullable member null.
        new() { Name = "d", Crew = 2 },
        // Every nullable member null, and removed from the JSON below.
        new() { Name = "e", Crew = 7 }
    ];

    private static readonly Dictionary<string, Expression<Func<Logbook, bool>>> Shapes = BuildShapes();

    private static Dictionary<string, Expression<Func<Logbook, bool>>> BuildShapes()
    {
        var three = 3;
        var fuel = 3.5m;
        var draft = 0.3f;
        var noon = Noon;
        var sky = Weather.Breezy;
        var name = "anne";
        int?[] counts = [3, 5];
        int?[] countsWithNull = [3, null];
        string?[] skippers = ["anne", "carl"];

        return new Dictionary<string, Expression<Func<Logbook, bool>>>
        {
            // int?
            ["int_not_equal"] = x => x.Count != three,
            ["int_not_equal_reversed"] = x => three != x.Count,
            ["int_negated_equal"] = x => !(x.Count == three),
            ["int_negated_greater"] = x => !(x.Count > three),
            ["int_negated_greater_or_equal"] = x => !(x.Count >= three),
            ["int_negated_less"] = x => !(x.Count < three),
            ["int_negated_less_or_equal"] = x => !(x.Count <= three),
            ["int_negated_not_equal"] = x => !(x.Count != three),
            ["int_double_negation"] = x => !!(x.Count > three),
            ["int_negated_and"] = x => !(x.Count < 4 && x.Crew > 1),
            ["int_negated_or"] = x => !(x.Count < 2 || x.Count > 4),
            ["int_negated_and_of_two_nullables"] = x => !(x.Count > 0 && x.Fuel > 2m),
            ["int_nested_negation"] = x => !(!(x.Count > 2) && x.Crew > 1),
            ["int_equal"] = x => x.Count == three,
            ["int_greater"] = x => x.Count > three,
            ["int_not_equal_null"] = x => x.Count != null,
            ["int_negated_equal_null"] = x => !(x.Count == null),

            // string
            ["string_not_equal"] = x => x.Skipper != name,
            ["string_negated_equal"] = x => !(x.Skipper == name),
            ["string_equal"] = x => x.Skipper == name,

            // decimal?
            ["decimal_not_equal"] = x => x.Fuel != fuel,
            ["decimal_negated_equal"] = x => !(x.Fuel == fuel),
            ["decimal_negated_greater"] = x => !(x.Fuel > 2m),
            ["decimal_negated_less"] = x => !(x.Fuel < 4m),
            ["decimal_folded_not_equal"] = x => x.Fuel - 0.5m != 3m,
            ["decimal_negated_folded_less"] = x => !(x.Fuel + 1m < 3m),

            // float?
            ["float_not_equal"] = x => x.Draft != draft,
            ["float_negated_equal"] = x => !(x.Draft == draft),
            ["float_negated_greater_or_equal"] = x => !(x.Draft >= draft),
            ["float_negated_less"] = x => !(x.Draft < 1f),
            ["float_against_a_double_not_equal"] = x => x.Draft != 0.3,
            ["float_against_a_double_negated_greater"] = x => !(x.Draft > 0.3),

            // DateTimeOffset?, through the strftime locator
            ["timestamp_not_equal"] = x => x.Sailed != noon,
            ["timestamp_negated_equal"] = x => !(x.Sailed == noon),
            ["timestamp_negated_greater"] = x => !(x.Sailed > noon),
            ["timestamp_negated_less_or_equal"] = x => !(x.Sailed <= noon),

            // enum?, integer storage
            ["enum_not_equal"] = x => x.Sky != sky,
            ["enum_negated_equal"] = x => !(x.Sky == sky),
            ["enum_negated_greater"] = x => !(x.Sky > Weather.Calm),

            // bool?
            ["bool_not_equal"] = x => x.Logged != true,
            ["bool_negated_equal"] = x => !(x.Logged == false),

            // arithmetic and modulo over a nullable member are lifted too
            ["arithmetic_not_equal"] = x => x.Count + 1 != 4,
            ["arithmetic_negated_greater"] = x => !(x.Count - 1 > 1),
            ["modulo_not_equal"] = x => x.Count % 2 != 0,
            ["modulo_negated_equal"] = x => !(x.Count % 2 == 1),

            // membership
            ["is_one_of"] = x => x.Count.IsOneOf(counts),
            ["negated_is_one_of"] = x => !x.Count.IsOneOf(counts),
            ["negated_contains"] = x => !counts.Contains(x.Count),
            ["contains_with_a_null"] = x => countsWithNull.Contains(x.Count),
            ["negated_contains_with_a_null"] = x => !countsWithNull.Contains(x.Count),
            ["negated_string_is_one_of"] = x => !x.Skipper.IsOneOf(skippers),

            // a non-nullable member absent from the JSON is NULL to SQL and 0 to C#: != a non-zero value
            // agrees, because both say true
            ["absent_non_nullable_not_equal"] = x => x.Crew != 5,
            ["absent_non_nullable_negated_greater"] = x => !(x.Crew > 4)
        };
    }

    public static TheoryData<string> ShapeNames() => new(Shapes.Keys);

    public async ValueTask InitializeAsync()
    {
        _store = await StoreAsync("main", EnumStorage.AsInteger);
        _stringEnums = await StoreAsync("strings", EnumStorage.AsString);
    }

    private async Task<DocumentStore> StoreAsync(string schema, EnumStorage enumStorage)
    {
        var store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.DatabaseSchemaName = schema;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.ConfigureSerialization(enumStorage);
            options.Schema.For<Logbook>();
        });

        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = store.LightweightSession();
        foreach (var row in Seed())
        {
            row.Id = Guid.NewGuid();
            session.Store(row);
        }

        session.Events.StartStream(Guid.NewGuid(),
            new LogbookEntered("a", 3), new LogbookEntered("b", 5), new LogbookEntered("c", null));
        await session.SaveChangesAsync(Token);

        // The serializer omits a null member, so row d is given explicit JSON nulls, and row e carries no
        // key for any nullable member, nor for Crew: the shape an older document has.
        await using var raw = store.LightweightSession();
        raw.QueueSqlCommand(
            $"update {TableName(schema)} set data = json_set(data, '$.count', null, '$.skipper', null, "
            + "'$.fuel', null, '$.draft', null, '$.sailed', null, '$.sky', null, '$.logged', null) "
            + "where json_extract(data, '$.name') = 'd'");
        raw.QueueSqlCommand(
            $"update {TableName(schema)} set data = json_remove(data, '$.count', '$.skipper', '$.fuel', "
            + "'$.draft', '$.sailed', '$.sky', '$.logged', '$.crew') where json_extract(data, '$.name') = 'e'");
        await raw.SaveChangesAsync(Token);

        return store;
    }

    private static string TableName(string schema) => schema == "main" ? "fi_doc_logbook" : $"{schema}_fi_doc_logbook";

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        await _stringEnums.DisposeAsync();
        _database.Dispose();
    }

    private static async Task<List<Logbook>> AllAsync(DocumentStore store)
    {
        await using var session = store.QuerySession();
        return [.. await session.Query<Logbook>().ToListAsync(Token)];
    }

    private static async Task<string[]> NamesAsync(DocumentStore store, Expression<Func<Logbook, bool>> predicate)
    {
        await using var session = store.QuerySession();
        var found = await session.Query<Logbook>().Where(predicate).ToListAsync(Token);
        return [.. found.Select(x => x.Name).Order()];
    }

    private static async Task<string[]> OracleAsync(DocumentStore store, Expression<Func<Logbook, bool>> predicate)
        => [.. (await AllAsync(store)).Where(predicate.Compile()).Select(x => x.Name).Order()];

    [Fact]
    public async Task the_seed_holds_null_and_absent_members()
    {
        var all = await AllAsync(_store);
        all.Count.ShouldBe(5);

        all.Single(x => x.Name == "d").Count.ShouldBeNull();

        var absent = all.Single(x => x.Name == "e");
        absent.Count.ShouldBeNull();
        absent.Crew.ShouldBe(0);

        await using var session = _store.QuerySession();
        (await session.AdvancedSql.QueryAsync<string>(
                $"select json_extract(data, '$.name') from {TableName("main")} "
                + "where json_type(data, '$.count') = 'null'", Token))
            .ShouldBe(["d"]);
        (await session.AdvancedSql.QueryAsync<string>(
                $"select json_extract(data, '$.name') from {TableName("main")} "
                + "where json_type(data, '$.count') is null", Token))
            .ShouldBe(["e"]);
    }

    [Theory]
    [MemberData(nameof(ShapeNames))]
    public async Task every_shape_agrees_with_linq_to_objects(string shape)
    {
        var predicate = Shapes[shape];
        (await NamesAsync(_store, predicate)).ShouldBe(await OracleAsync(_store, predicate), shape);
    }

    [Theory]
    [InlineData("not_equal")]
    [InlineData("negated_equal")]
    [InlineData("negated_is_one_of")]
    public async Task a_string_stored_enum_keeps_its_null_rows(string shape)
    {
        var sky = Weather.Breezy;
        Expression<Func<Logbook, bool>> predicate = shape switch
        {
            "not_equal" => x => x.Sky != sky,
            "negated_equal" => x => !(x.Sky == sky),
            "negated_is_one_of" => x => !x.Sky.IsOneOf(Weather.Breezy, Weather.Calm),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

        (await NamesAsync(_stringEnums, predicate)).ShouldBe(await OracleAsync(_stringEnums, predicate));
    }

    /// <summary>
    ///     A string method on a null receiver throws in C#, so no answer agrees with it. Fisher keeps SQL's:
    ///     a row whose member is null matches neither the method nor its negation.
    /// </summary>
    [Fact]
    public async Task a_negated_string_method_keeps_sql_semantics_for_a_null_receiver()
    {
        (await NamesAsync(_store, x => !x.Skipper!.StartsWith("a"))).ShouldBe(["b", "c"]);
        (await NamesAsync(_store, x => !x.Skipper!.Contains("o"))).ShouldBe(["a", "c"]);
        (await NamesAsync(_store, x => !x.Skipper!.EndsWith("l"))).ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task an_event_body_keeps_its_null_rows()
    {
        await using var session = _store.QuerySession();

        var notEqual = await session.Events.QueryEventDataAsync<LogbookEntered>(x => x.Knots != 3, Token);
        notEqual.Select(x => x.Name).Order().ShouldBe(["b", "c"]);

        var negated = await session.Events.QueryEventDataAsync<LogbookEntered>(x => !(x.Knots > 3), Token);
        negated.Select(x => x.Name).Order().ShouldBe(["a", "c"]);
    }

    [Fact]
    public async Task event_metadata_keeps_its_null_rows()
    {
        using var database = TemporaryDatabase.Create("nullable-negation-events");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.EnableCorrelationId = true;
        });
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = store.LightweightSession())
        {
            session.CorrelationId = "c1";
            session.Events.StartStream(Guid.NewGuid(), new LogbookEntered("a", 1));
            await session.SaveChangesAsync(Token);
        }

        await using (var session = store.LightweightSession())
        {
            session.CorrelationId = null;
            session.Events.StartStream(Guid.NewGuid(), new LogbookEntered("b", 2));
            await session.SaveChangesAsync(Token);
        }

        await using var query = store.QuerySession();
        var found = await query.Events.QueryEventsAsync(e => e.CorrelationId != "c1", Token);
        found.Select(x => ((LogbookEntered)x.Data).Name).ShouldBe(["b"]);

        var negated = await query.Events.QueryEventsAsync(e => !(e.CorrelationId == "c1"), Token);
        negated.Select(x => ((LogbookEntered)x.Data).Name).ShouldBe(["b"]);
    }

    [Fact]
    public void the_common_case_keeps_its_plain_comparison()
    {
        using var session = _store.QuerySession();

        session.ToSql(session.Query<Logbook>().Where(x => x.Count == 3))
            .ShouldContain("json_extract(data, '$.count') = ");
        session.ToSql(session.Query<Logbook>().Where(x => x.Count > 3))
            .ShouldContain("json_extract(data, '$.count') > ");

        session.ToSql(session.Query<Logbook>().Where(x => x.Count != 3))
            .ShouldContain("json_extract(data, '$.count') is not ");
    }

    /// <summary>
    ///     The plain form is what an index serves, so a fix that wrapped every comparison would lose it.
    /// </summary>
    [Fact]
    public async Task an_indexed_member_is_still_searched_through_its_index()
    {
        using var database = TemporaryDatabase.Create("nullable-negation-index");
        await using var store = DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Logbook>().Duplicate(x => x.Count).Index(x => x.Fuel);
        });
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = store.QuerySession();

        foreach (var predicate in new Expression<Func<Logbook, bool>>[]
                 {
                     x => x.Count == 3, x => x.Count > 3, x => x.Fuel == 3.5m, x => x.Fuel < 2m
                 })
        {
            var plan = await session.Query<Logbook>().Where(predicate).ExplainAsync(Token);
            plan.UsesIndex.ShouldBeTrue(predicate.ToString());
        }
    }
}
