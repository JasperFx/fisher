using System.Globalization;
using System.Linq.Expressions;
using Fisher.Linq;
using Fisher.Linq.Members;
using Fisher.Linq.Parsing;
using JasperFx;
using Shouldly;

namespace Fisher.Tests.Linq;

public class Priced
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public decimal? Discount { get; set; }
    public double Ratio { get; set; }
    public float Weight { get; set; }
    public int Count { get; set; }
}

/// <summary>
///     fisher#603: <c>x.Price - 0.1m &lt; 0.2m</c> used to be rendered as <c>(price - 0.1) &lt; @p0</c>, which
///     SQLite computes in double, where <c>0.3 - 0.1</c> is <c>0.19999999999999998</c>. A decimal member
///     plus or minus constants is now folded into one constant in .NET; every other decimal or float
///     arithmetic shape, and a non-integral modulo, is refused by name. LINQ to Objects over the same
///     documents is the oracle, and the values are chosen so double arithmetic answers differently.
/// </summary>
public class decimal_arithmetic_in_predicates : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("decimal-arithmetic");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly List<Priced> Rows =
    [
        new() { Id = Guid.NewGuid(), Name = "a", Price = 0.3m, Discount = 0.3m, Ratio = 0.3, Weight = 0.3f, Count = 3 },
        new() { Id = Guid.NewGuid(), Name = "b", Price = 0.1m, Discount = 0.1m, Ratio = 0.1, Weight = 0.1f, Count = 1 },
        new() { Id = Guid.NewGuid(), Name = "c", Price = 1.5m, Discount = null, Ratio = 1.5, Weight = 1.5f, Count = 5 },
        new() { Id = Guid.NewGuid(), Name = "d", Price = 0.25m, Discount = null, Ratio = 2.5, Weight = 2.5f, Count = 2 }
    ];

    private static readonly decimal Step = 0.1m;
    private static readonly decimal Limit = 0.2m;
    private static readonly int Whole = 1;
    private static readonly float FloatStep = 0.1f;
    private static readonly decimal? Nothing = null;

    /// <summary>Every shape that translates, by name, so the theory rows serialize (see CLAUDE.md).</summary>
    private static readonly Dictionary<string, Expression<Func<Priced, bool>>> Translated = new()
    {
        // The issue's case: 0.3 - 0.1 < 0.2 is true in double and false in decimal.
        ["subtract_a_constant"] = x => x.Price - 0.1m < 0.2m,
        ["subtract_captured_values"] = x => x.Price - Step < Limit,
        ["add_a_constant_equal"] = x => x.Price + 0.2m == 0.3m,
        ["add_a_constant_greater_than"] = x => x.Price + 0.2m > 0.3m,
        ["add_a_constant_not_equal"] = x => x.Price + 0.2m != 0.3m,
        ["constant_plus_member"] = x => 0.2m + x.Price <= 0.3m,
        ["constant_minus_member_reverses_the_operator"] = x => 0.3m - x.Price < 0.2m,
        ["constant_minus_member_equal"] = x => 0.3m - x.Price == 0.2m,
        ["constant_minus_member_greater_than_or_equal"] = x => 0.3m - x.Price >= 0.2m,
        ["arithmetic_on_the_right"] = x => 0.2m > x.Price - 0.1m,
        ["constant_minus_member_on_the_right"] = x => 0.2m <= 0.3m - x.Price,
        ["nested_subtractions"] = x => x.Price - 0.1m - 0.1m < 0.1m,
        ["an_int_constant_widened_to_decimal"] = x => x.Price + Whole > 1.3m,
        ["an_int_member_widened_to_decimal"] = x => x.Count + 0.1m > 3.1m,
        ["negated"] = x => !(x.Price - 0.1m < 0.2m),
        ["nullable_subtract"] = x => x.Discount - 0.1m < 0.2m,
        ["nullable_add_equal"] = x => x.Discount + 0.2m == 0.3m,
        ["nullable_against_null"] = x => x.Discount - 0.1m == null,
        ["composed_with_another_predicate"] = x => x.Price - 0.1m < 0.2m || x.Count > 4,

        // Integral and double arithmetic still run in SQLite, where they agree with C#.
        ["int_arithmetic"] = x => x.Count - 1 > 1,
        ["double_arithmetic"] = x => x.Ratio - 0.1 < 0.2,
        ["double_arithmetic_equal"] = x => x.Ratio + 0.2 > 0.3 && x.Ratio < 1,
        ["a_decimal_member_in_double_arithmetic"] = x => (double)x.Price - 0.1 < 0.2,
        // The constant is the double C# widens 0.1f to, not the float's "0.1".
        ["a_float_constant_widened_to_double"] = x => x.Ratio + (double)FloatStep > 0.4,
        ["integral_modulo"] = x => x.Count % 2 == 1
    };

    public static TheoryData<string> TranslatedNames() => new(Translated.Keys);

    /// <summary>Every shape that is refused by name rather than computed in double.</summary>
    private static readonly Dictionary<string, Expression<Func<Priced, bool>>> Refused = new()
    {
        ["two_decimal_members"] = x => x.Price + x.Discount < 1m,
        ["decimal_multiplication"] = x => x.Price * 2m < 1m,
        ["decimal_division"] = x => x.Price / 2m < 1m,
        ["decimal_arithmetic_converted_to_double"] = x => (double)(x.Price - 0.1m) < 0.2,
        ["decimal_modulo"] = x => x.Price % 0.25m == 0m,
        ["double_modulo"] = x => x.Ratio % 1.5 == 0,
        ["an_int_modulo_by_a_double"] = x => x.Count % 2.5 == 0.5,
        ["float_arithmetic"] = x => x.Weight - 0.1f < 0.2f,
        ["a_float_member_widened_to_double"] = x => (double)x.Weight + 0.1 > 0.4,
        ["adding_null"] = x => x.Discount + Nothing == null,
        ["folding_overflows"] = x => x.Price - decimal.MaxValue < decimal.MaxValue
    };

    public static TheoryData<string> RefusedNames() => new(Refused.Keys);

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Priced>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        foreach (var row in Rows) session.Store(row);
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<List<string>> NamesAsync(Expression<Func<Priced, bool>> predicate)
    {
        await using var session = _store.QuerySession();
        var names = await session.Query<Priced>().Where(predicate).Select(x => x.Name).ToListAsync(Token);
        return names.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private static List<string> Oracle(Expression<Func<Priced, bool>> predicate)
        => Rows.Where(predicate.Compile()).Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();

    [Theory]
    [MemberData(nameof(TranslatedNames))]
    public async Task translates_and_agrees_with_linq_to_objects(string name)
    {
        var predicate = Translated[name];

        (await NamesAsync(predicate)).ShouldBe(Oracle(predicate));
    }

    [Fact]
    public void the_data_tells_the_shapes_apart()
    {
        // A translated shape that returned everything or nothing would agree with an oracle that also did.
        foreach (var (name, predicate) in Translated)
        {
            var count = Oracle(predicate).Count;
            count.ShouldBeGreaterThan(0, name);
            count.ShouldBeLessThan(Rows.Count, name);
        }
    }

    [Fact]
    public void double_arithmetic_would_have_answered_the_issues_case_differently()
    {
        // The premise: computed in double, as SQLite would, row "a" matches. Decimal says it does not.
        Rows.Where(x => (double)x.Price - 0.1 < 0.2).Select(x => x.Name).ShouldContain("a");
        Oracle(x => x.Price - 0.1m < 0.2m).ShouldNotContain("a");
    }

    [Theory]
    [MemberData(nameof(RefusedNames))]
    public async Task is_refused_by_name(string name)
    {
        await using var session = _store.QuerySession();

        var refusal = await Should.ThrowAsync<BadLinqExpressionException>(
            () => session.Query<Priced>().Where(Refused[name]).ToListAsync(Token));

        refusal.Message.ShouldStartWith("Cannot translate");
    }

    [Fact]
    public void the_constants_are_folded_into_one_bound_value()
    {
        var factory = new MemberFactory(_store.Options, _store.Options.Schema.For<Priced>().Mapping);
        var builder = new Weasel.Sqlite.CommandBuilder();
        Expression<Func<Priced, bool>> predicate = x => 0.3m - x.Price < 0.2m;
        new WhereClauseParser(factory).Parse(predicate.Body).Apply(builder);
        var command = (Microsoft.Data.Sqlite.SqliteCommand)builder.Compile();

        // No arithmetic reaches SQLite: the member is compared with 0.3 - 0.2, computed in decimal, and
        // the operator is reversed because the member was subtracted.
        command.CommandText.ShouldBe("json_extract(data, '$.price') > @p0");
        command.Parameters["p0"].Value.ShouldBe(0.1);
    }

    [Fact]
    public async Task a_refusal_names_the_arithmetic_and_why()
    {
        await using var session = _store.QuerySession();

        var two = await Should.ThrowAsync<BadLinqExpressionException>(
            () => session.Query<Priced>().Where(x => x.Price + x.Discount < 1m).ToListAsync(Token));
        two.Message.ShouldContain("Decimal arithmetic");
        two.Message.ShouldContain("double precision");

        var modulo = await Should.ThrowAsync<BadLinqExpressionException>(
            () => session.Query<Priced>().Where(x => x.Ratio % 1.5 == 0).ToListAsync(Token));
        modulo.Message.ShouldContain("Double modulo");

        var floatMember = await Should.ThrowAsync<BadLinqExpressionException>(
            () => session.Query<Priced>().Where(x => (double)x.Weight + 0.1 > 0.4).ToListAsync(Token));
        floatMember.Message.ShouldContain("is a float");
    }

    [Fact]
    public async Task a_folded_comparison_works_under_a_comma_decimal_culture()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

        try
        {
            Expression<Func<Priced, bool>> predicate = x => x.Price - 0.1m < 0.2m;
            (await NamesAsync(predicate)).ShouldBe(Oracle(predicate));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
