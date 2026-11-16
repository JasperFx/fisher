using System.Linq.Expressions;
using Fisher.Linq;
using JasperFx;
using Shouldly;

namespace Fisher.Tests.Linq;

public class PreciseLedger
{
    public Guid Id { get; set; }
    public string Label { get; set; } = "";
    public decimal A { get; set; }
    public decimal B { get; set; }
    public decimal? C { get; set; }
    public decimal? D { get; set; }
    public int N { get; set; }
    public long L { get; set; }
    public double Ratio { get; set; }
}

/// <summary>
///     fisher#615: a comparison C# makes in <see cref="decimal" /> between two document members (fisher#595's
///     shape) used to compare the two <c>json_extract</c> REALs, which are one value for decimals that differ
///     past the fifteenth significant digit. It now compares their exact <c>fi_decimal_key</c>s, keeping
///     fisher#595's and fisher#608's answers for a null. LINQ to Objects over the same documents is the oracle.
/// </summary>
public class decimal_member_comparisons : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("decimal-member-comparisons");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly (decimal A, decimal B)[] Pairs =
    [
        (0.1000000000000000001m, 0.1000000000000000002m),
        (0.1000000000000000002m, 0.1000000000000000001m),
        (0.1000000000000000001m, 0.1000000000000000001m),
        (12345678901234.5601m, 12345678901234.5602m),
        (-12345678901234.5602m, -12345678901234.5601m),
        (1.0m, 1.00m),
        (0.0000000000000000000000000001m, 0.0000000000000000000000000002m),
        (79228162514264337593543950335m, 79228162514264337593543950334m),
        (3m, 3.0000000000000000001m),
        (2.9999999999999999999m, 3m),
        (0m, -0.0000000000000000001m),
        (7m, 7m)
    ];

    private static readonly List<PreciseLedger> Rows = Pairs.Select((pair, i) => new PreciseLedger
    {
        Id = Guid.NewGuid(),
        Label = $"row-{i:D2}",
        A = pair.A,
        B = pair.B,
        C = i % 4 == 0 ? null : pair.A,
        D = i % 3 == 0 ? null : pair.B,
        // 3 and 7 sit beside the 3m / 3.0000000000000000001m / 2.9999999999999999999m and 7m rows, which a
        // REAL comparison cannot tell apart from them.
        N = 3,
        L = 7,
        Ratio = (double)pair.A
    }).ToList();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<PreciseLedger>();
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

    private async Task MatchesCSharp(string name, Expression<Func<PreciseLedger, bool>> predicate)
    {
        await using var session = _store.QuerySession();

        var found = await session.Query<PreciseLedger>().Where(predicate).ToListAsync(Token);
        var expected = Rows.Where(predicate.Compile()).Select(x => x.Label).Order().ToArray();

        found.Select(x => x.Label).Order().ToArray().ShouldBe(expected, name);
    }

    private static readonly Dictionary<string, Expression<Func<PreciseLedger, bool>>> Shapes = new()
    {
        ["equal"] = x => x.A == x.B,
        ["not_equal"] = x => x.A != x.B,
        ["less"] = x => x.A < x.B,
        ["at_most"] = x => x.A <= x.B,
        ["greater"] = x => x.A > x.B,
        ["at_least"] = x => x.A >= x.B,
        ["negated_less"] = x => !(x.A < x.B),
        ["negated_equal"] = x => !(x.A == x.B),
        ["nullable_equal"] = x => x.C == x.D,
        ["nullable_not_equal"] = x => x.C != x.D,
        ["nullable_less"] = x => x.C < x.D,
        ["nullable_negated_greater"] = x => !(x.C > x.D),
        ["nullable_against_plain"] = x => x.C >= x.B,
        ["int_against_decimal"] = x => x.N < x.A,
        ["long_against_decimal"] = x => x.B == x.L,
        ["int_arithmetic_against_decimal"] = x => x.N + 1 > x.B
    };

    public static TheoryData<string> ShapeNames() => [.. Shapes.Keys];

    [Theory]
    [MemberData(nameof(ShapeNames))]
    public Task a_decimal_comparison_between_members_matches_csharp(string name) => MatchesCSharp(name, Shapes[name]);

    /// <summary>
    ///     The REALs the old rendering compared are equal for these rows, so a REAL comparison answers
    ///     differently from C#. Without this the theory above could pass on data a double tells apart.
    /// </summary>
    [Fact]
    public void the_data_differs_past_what_a_double_holds()
    {
        Rows.Count(x => x.A != x.B && (double)x.A == (double)x.B).ShouldBeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public async Task a_double_converted_to_decimal_is_refused_by_name()
    {
        await using var session = _store.QuerySession();

        var refused = await Should.ThrowAsync<BadLinqExpressionException>(
            () => session.Query<PreciseLedger>().Where(x => (decimal)x.Ratio == x.A).ToListAsync(Token));
        refused.Message.ShouldContain("fifteen significant digits");
    }

    [Fact]
    public async Task decimal_arithmetic_between_members_stays_refused()
    {
        await using var session = _store.QuerySession();

        await Should.ThrowAsync<BadLinqExpressionException>(
            () => session.Query<PreciseLedger>().Where(x => x.A + x.B < x.B).ToListAsync(Token));
    }

    /// <summary>
    ///     A comparison C# makes in double keeps the REAL comparison. Asserted on the SQL: C#'s
    ///     <c>(double)decimal</c> is not correctly rounded, so an oracle would fail on a 1e-28 row for a
    ///     reason that is not this one.
    /// </summary>
    [Fact]
    public async Task a_comparison_made_in_double_still_compares_reals()
    {
        await using var session = _store.LightweightSession();

        var sql = session.ToSql(session.Query<PreciseLedger>().Where(x => (double)x.A == x.Ratio));
        sql.ShouldNotContain("fi_decimal_key");

        var exact = session.ToSql(session.Query<PreciseLedger>().Where(x => x.A == x.B));
        exact.ShouldContain("fi_decimal_key");
    }
}
