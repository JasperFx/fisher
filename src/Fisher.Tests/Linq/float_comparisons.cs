using System.Linq.Expressions;
using Fisher.Linq;
using JasperFx;
using Shouldly;

namespace Fisher.Tests.Linq;

public class Freight
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public float Weight { get; set; }
    public float? Tare { get; set; }
    public double Ratio { get; set; }
}

public class WeighedFreight
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public float Weight { get; set; }
}

public record FreightWeighed(string Name, float Weight);

/// <summary>
///     fisher#605: a <see cref="float" /> comparison value used to bind as the double it widens to, so
///     <c>x.Weight == 0.3f</c> compared <c>0.30000001192092896</c> with the stored <c>0.3</c> — the float's
///     shortest round-trip text, which is what System.Text.Json writes — and matched nothing. LINQ to
///     Objects over the same rows is the oracle throughout, and every probe is a float whose widened double
///     differs from its stored one, or the float either side of one, so the range boundaries are tested.
/// </summary>
public class float_comparisons : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("float-comparisons");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, float> Probes = new()
    {
        ["point_one"] = 0.1f,
        ["point_three"] = 0.3f,
        ["one_point_one"] = 1.1f,
        ["large"] = 123456.79f,
        ["negative"] = -2.7f,
        ["just_above_point_three"] = MathF.BitIncrement(0.3f),
        ["just_below_point_three"] = MathF.BitDecrement(0.3f)
    };

    public static TheoryData<string> ProbeNames() => new(Probes.Keys);

    private static readonly List<Freight> Rows =
    [
        new() { Name = "a", Weight = 0.1f, Tare = 0.1f, Ratio = 0.1 },
        new() { Name = "b", Weight = 0.3f, Tare = null, Ratio = 0.3 },
        new() { Name = "c", Weight = 1.1f, Tare = 1.1f, Ratio = 1.1 },
        new() { Name = "d", Weight = 123456.79f, Tare = 123456.79f, Ratio = 123456.79 },
        new() { Name = "e", Weight = -2.7f, Tare = -2.7f, Ratio = -2.7 },
        new() { Name = "f", Weight = MathF.BitIncrement(0.3f), Tare = MathF.BitIncrement(0.3f), Ratio = 0.5 },
        new() { Name = "g", Weight = MathF.BitDecrement(0.3f), Tare = 0.3f, Ratio = (double)0.3f },
        new() { Name = "h", Weight = 0.5f, Tare = null, Ratio = 2 }
    ];

    private static readonly string[] Operators = ["==", "!=", "<", "<=", ">", ">="];

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Freight>();
            options.Schema.For<WeighedFreight>().Duplicate(x => x.Weight);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        foreach (var row in Rows)
        {
            row.Id = Guid.NewGuid();
            session.Store(row);
            session.Store(new WeighedFreight { Id = Guid.NewGuid(), Name = row.Name, Weight = row.Weight });
        }

        session.Events.StartStream(Guid.NewGuid(), Rows.Select(x => (object)new FreightWeighed(x.Name, x.Weight)).ToArray());
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    /// <summary>A captured float, which is how a value usually reaches a query.</summary>
    private static Expression<Func<Freight, bool>> WeightAgainst(string op, float value) => op switch
    {
        "==" => x => x.Weight == value,
        "!=" => x => x.Weight != value,
        "<" => x => x.Weight < value,
        "<=" => x => x.Weight <= value,
        ">" => x => x.Weight > value,
        ">=" => x => x.Weight >= value,
        _ => throw new ArgumentOutOfRangeException(nameof(op))
    };

    private static Expression<Func<Freight, bool>> TareAgainst(string op, float value) => op switch
    {
        "==" => x => x.Tare == value,
        "<" => x => x.Tare < value,
        "<=" => x => x.Tare <= value,
        ">" => x => x.Tare > value,
        ">=" => x => x.Tare >= value,
        // != over a nullable member is SQL's null semantics, not this issue's, so it is not asserted.
        _ => throw new ArgumentOutOfRangeException(nameof(op))
    };

    /// <summary>A double compared with the float member, which C# widens: <c>(double)x.Weight op value</c>.</summary>
    private static Expression<Func<Freight, bool>> WeightAgainstDouble(string op, double value) => op switch
    {
        "==" => x => x.Weight == value,
        "!=" => x => x.Weight != value,
        "<" => x => x.Weight < value,
        "<=" => x => x.Weight <= value,
        ">" => x => x.Weight > value,
        ">=" => x => x.Weight >= value,
        _ => throw new ArgumentOutOfRangeException(nameof(op))
    };

    private static string[] Oracle(Expression<Func<Freight, bool>> predicate)
        => [.. Rows.Where(predicate.Compile()).Select(x => x.Name).Order()];

    private async Task<string[]> NamesAsync(Expression<Func<Freight, bool>> predicate)
    {
        await using var session = _store.QuerySession();
        var found = await session.Query<Freight>().Where(predicate).ToListAsync(Token);
        return [.. found.Select(x => x.Name).Order()];
    }

    private async Task ShouldAgreeWithLinqToObjects(Expression<Func<Freight, bool>> predicate)
        => (await NamesAsync(predicate)).ShouldBe(Oracle(predicate), predicate.ToString());

    /// <summary>The issue's own case, with the float as a literal constant.</summary>
    [Fact]
    public async Task equality_against_a_float_literal_finds_the_row()
    {
        (await NamesAsync(x => x.Weight == 0.3f)).ShouldBe(["b"]);
        (await NamesAsync(x => x.Weight > 0.3f)).ShouldBe(["c", "d", "f", "h"]);
    }

    [Theory]
    [MemberData(nameof(ProbeNames))]
    public async Task every_operator_agrees_with_linq_to_objects(string probe)
    {
        foreach (var op in Operators)
        {
            await ShouldAgreeWithLinqToObjects(WeightAgainst(op, Probes[probe]));
        }
    }

    [Theory]
    [MemberData(nameof(ProbeNames))]
    public async Task a_nullable_float_agrees_with_linq_to_objects(string probe)
    {
        foreach (var op in new[] { "==", "<", "<=", ">", ">=" })
        {
            await ShouldAgreeWithLinqToObjects(TareAgainst(op, Probes[probe]));
        }

        (await NamesAsync(x => x.Tare == null)).ShouldBe(["b", "h"]);
    }

    [Theory]
    [MemberData(nameof(ProbeNames))]
    public async Task a_duplicated_float_column_agrees_with_linq_to_objects(string probe)
    {
        var value = Probes[probe];
        await using var session = _store.QuerySession();

        foreach (var op in Operators)
        {
            var predicate = WeightAgainst(op, value);
            var compiled = predicate.Compile();
            var expected = Rows.Where(compiled).Select(x => x.Name).Order().ToArray();

            Expression<Func<WeighedFreight, bool>> duplicated = op switch
            {
                "==" => x => x.Weight == value,
                "!=" => x => x.Weight != value,
                "<" => x => x.Weight < value,
                "<=" => x => x.Weight <= value,
                ">" => x => x.Weight > value,
                _ => x => x.Weight >= value
            };

            var found = await session.Query<WeighedFreight>().Where(duplicated).ToListAsync(Token);
            found.Select(x => x.Name).Order().ToArray().ShouldBe(expected, duplicated.ToString());
        }
    }

    [Fact]
    public async Task is_one_of_and_contains_over_floats_match()
    {
        var wanted = new[] { 0.3f, 1.1f, -2.7f, MathF.BitIncrement(0.3f) };

        await ShouldAgreeWithLinqToObjects(x => x.Weight.IsOneOf(0.3f, 1.1f, -2.7f));
        await ShouldAgreeWithLinqToObjects(x => wanted.Contains(x.Weight));
        await ShouldAgreeWithLinqToObjects(x => x.Tare.IsOneOf(0.1f, 123456.79f));

        (await NamesAsync(x => x.Weight.IsOneOf(0.3f, 1.1f, -2.7f))).ShouldBe(["b", "c", "e"]);
    }

    /// <summary>
    ///     <c>x.Weight == 0.3</c> widens the member in C#, so it is false for a stored 0.3f: the double
    ///     0.3 lies between two floats. Binding it as it is would match the stored "0.3".
    /// </summary>
    [Theory]
    [InlineData("point_three", 0.3)]
    [InlineData("point_three_widened", (double)0.3f)]
    [InlineData("exact_half", 0.5)]
    [InlineData("between_one_point_one_and_its_neighbours", 1.1)]
    [InlineData("negative", -2.7)]
    public async Task a_double_against_a_float_member_agrees_with_linq_to_objects(string name, double value)
    {
        name.ShouldNotBeEmpty();
        foreach (var op in Operators)
        {
            await ShouldAgreeWithLinqToObjects(WeightAgainstDouble(op, value));
        }
    }

    [Fact]
    public async Task membership_of_doubles_against_a_float_member_agrees_with_linq_to_objects()
    {
        var doubles = new[] { 0.3, (double)1.1f, 0.5 };

        await ShouldAgreeWithLinqToObjects(x => doubles.Contains(x.Weight));
        await ShouldAgreeWithLinqToObjects(x => ((double)x.Weight).IsOneOf(0.3, (double)1.1f, 0.5));
        (await NamesAsync(x => doubles.Contains(x.Weight))).ShouldBe(["c", "h"]);
    }

    /// <summary>
    ///     The other direction: a float captured against a <see cref="double" /> member is widened by C#,
    ///     so the widened double is what must be bound — not the float's stored text.
    /// </summary>
    [Fact]
    public async Task a_float_against_a_double_member_is_compared_widened()
    {
        var single = 0.3f;

        await ShouldAgreeWithLinqToObjects(x => x.Ratio == single);
        await ShouldAgreeWithLinqToObjects(x => x.Ratio > single);
        (await NamesAsync(x => x.Ratio == single)).ShouldBe(["g"]);
    }

    /// <summary>A double narrowed to float is compared as that float, as C# does.</summary>
    [Fact]
    public async Task a_double_narrowed_to_float_is_compared_as_the_float()
    {
        var wide = 0.30000000001;

        await ShouldAgreeWithLinqToObjects(x => x.Weight == (float)wide);
        await ShouldAgreeWithLinqToObjects(x => x.Weight >= (float)wide);
        (await NamesAsync(x => x.Weight == (float)wide)).ShouldBe(["b"]);
    }

    [Fact]
    public async Task an_event_body_float_compares_as_stored()
    {
        await using var session = _store.QuerySession();

        var equal = await session.Events.QueryEventDataAsync<FreightWeighed>(x => x.Weight == 0.3f, Token);
        equal.Select(x => x.Name).ShouldBe(["b"]);

        var above = await session.Events.QueryEventDataAsync<FreightWeighed>(x => x.Weight > 0.3f, Token);
        above.Select(x => x.Name).Order().ShouldBe(["c", "d", "f", "h"]);
    }

    /// <summary>
    ///     Raw SQL reaches <c>SqliteParameterValue.ToDatabaseValue</c> with no member in between, so a
    ///     float parameter is normalised there too.
    /// </summary>
    [Fact]
    public async Task a_float_parameter_in_raw_sql_matches_the_stored_number()
    {
        await using var session = _store.QuerySession();

        var names = await session.AdvancedSql.QueryAsync<string>(
            "select json_extract(data, '$.name') from fi_doc_freight where json_extract(data, '$.weight') = ?",
            Token, 0.3f);

        names.ShouldBe(["b"]);
    }
}
