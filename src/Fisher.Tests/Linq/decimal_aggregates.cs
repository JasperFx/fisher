using System.Globalization;
using Fisher.Linq;
using JasperFx;
using Microsoft.Data.Sqlite;

namespace Fisher.Tests.Linq;

public class LedgerLine
{
    public Guid Id { get; set; }
    public string Category { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal? Fee { get; set; }
}

public class DuplicatedLedgerLine
{
    public Guid Id { get; set; }
    public string Category { get; set; } = "";
    public decimal Amount { get; set; }
}

/// <summary>
///     A <c>decimal</c> aggregate — <c>Sum</c>, <c>Average</c>, <c>Min</c>, <c>Max</c> — in a <c>HAVING</c>,
///     as a projected group column, as a grouped ordering key and as a top-level terminal (fisher#606).
///     LINQ to Objects over the same rows is the oracle throughout.
/// </summary>
/// <remarks>
///     <para>
///         The values are chosen where <c>double</c> and <c>decimal</c> part company. <c>0.1 + 0.2</c> is
///         <c>0.30000000000000004</c> in double, so a <c>HAVING</c> equality against <c>0.3m</c> missed and a
///         <c>&gt;</c> against it matched. Two of <c>9999999999999.99</c> sum to sixteen significant digits,
///         more than the double-to-decimal read could carry back. <c>0.5 / 3</c> is a 28-digit decimal and a
///         17-digit double. <c>12345678901234.56</c> and the two values that differ only in the nineteenth
///         digit are not representable in double at all, so even <c>Min</c> and <c>Max</c>, which do not
///         accumulate, were wrong for them.
///     </para>
/// </remarks>
public class decimal_aggregates : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("decimal-aggregates");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly List<LedgerLine> Lines =
    [
        // a: 0.1 + 0.2 is 0.30000000000000004 in double.
        new() { Id = Guid.NewGuid(), Category = "a", Amount = 0.1m, Fee = 0.1m },
        new() { Id = Guid.NewGuid(), Category = "a", Amount = 0.2m, Fee = 0.2m },

        // b: the sum has sixteen significant digits.
        new() { Id = Guid.NewGuid(), Category = "b", Amount = 9999999999999.99m, Fee = null },
        new() { Id = Guid.NewGuid(), Category = "b", Amount = 9999999999999.99m, Fee = 0.7m },

        // c: the mean is 0.1666666666666666666666666667m.
        new() { Id = Guid.NewGuid(), Category = "c", Amount = 0.1m, Fee = null },
        new() { Id = Guid.NewGuid(), Category = "c", Amount = 0.2m, Fee = null },
        new() { Id = Guid.NewGuid(), Category = "c", Amount = 0.2m, Fee = 0.1m },

        // d: a single value no double can hold.
        new() { Id = Guid.NewGuid(), Category = "d", Amount = 12345678901234.56m, Fee = 1m },
        new() { Id = Guid.NewGuid(), Category = "d", Amount = 1m, Fee = null },

        // e and f differ only in the nineteenth digit, so in double they are the same number.
        new() { Id = Guid.NewGuid(), Category = "e", Amount = 0.1000000000000000001m },
        new() { Id = Guid.NewGuid(), Category = "f", Amount = 0.1000000000000000002m }
    ];

    // x sums to 0.3 and y to 0.30: equal as decimals, different as text.
    private static readonly List<DuplicatedLedgerLine> DuplicatedLines =
    [
        new() { Id = Guid.NewGuid(), Category = "x", Amount = 0.1m },
        new() { Id = Guid.NewGuid(), Category = "x", Amount = 0.2m },
        new() { Id = Guid.NewGuid(), Category = "y", Amount = 0.15m },
        new() { Id = Guid.NewGuid(), Category = "y", Amount = 0.15m },
        new() { Id = Guid.NewGuid(), Category = "z", Amount = 9999999999999.99m },
        new() { Id = Guid.NewGuid(), Category = "z", Amount = 9999999999999.99m }
    ];

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<LedgerLine>();
            options.Schema.For<DuplicatedLedgerLine>().Duplicate(x => x.Amount);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        foreach (var line in Lines) session.Store(line);
        foreach (var line in DuplicatedLines) session.Store(line);
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private IQuerySession Session() => _store.LightweightSession();

    private static IEnumerable<IGrouping<string, LedgerLine>> Groups => Lines.GroupBy(x => x.Category);

    // ---- HAVING ----

    [Fact]
    public async Task having_a_sum_equal_to_a_decimal_matches_csharp()
    {
        await using var session = Session();

        var keys = await session.Query<LedgerLine>().GroupBy(x => x.Category)
            .Where(g => g.Sum(x => x.Amount) == 0.3m)
            .Select(g => g.Key)
            .ToListAsync(Token);

        var expected = Groups.Where(g => g.Sum(x => x.Amount) == 0.3m).Select(g => g.Key);

        keys.Order().ShouldBe(expected.Order());
        keys.ShouldBe(["a"]);
    }

    [Fact]
    public async Task having_a_sum_range_comparison_matches_csharp()
    {
        await using var session = Session();

        var greater = await session.Query<LedgerLine>().GroupBy(x => x.Category)
            .Where(g => g.Sum(x => x.Amount) > 0.3m)
            .Select(g => g.Key)
            .ToListAsync(Token);

        var atMost = await session.Query<LedgerLine>().GroupBy(x => x.Category)
            .Where(g => 0.3m >= g.Sum(x => x.Amount))
            .Select(g => g.Key)
            .ToListAsync(Token);

        greater.Order().ShouldBe(Groups.Where(g => g.Sum(x => x.Amount) > 0.3m).Select(g => g.Key).Order());
        atMost.Order().ShouldBe(Groups.Where(g => 0.3m >= g.Sum(x => x.Amount)).Select(g => g.Key).Order());
    }

    [Fact]
    public async Task having_an_average_equal_to_a_decimal_matches_csharp()
    {
        const decimal mean = 0.1666666666666666666666666667m;

        await using var session = Session();

        var keys = await session.Query<LedgerLine>().GroupBy(x => x.Category)
            .Where(g => g.Average(x => x.Amount) == mean)
            .Select(g => g.Key)
            .ToListAsync(Token);

        keys.ShouldBe(Groups.Where(g => g.Average(x => x.Amount) == mean).Select(g => g.Key).ToList());
        keys.ShouldBe(["c"]);
    }

    [Fact]
    public async Task having_tells_apart_values_double_cannot()
    {
        await using var session = Session();

        var keys = await session.Query<LedgerLine>().GroupBy(x => x.Category)
            .Where(g => g.Max(x => x.Amount) == 0.1000000000000000001m)
            .Select(g => g.Key)
            .ToListAsync(Token);

        keys.ShouldBe(["e"]);
    }

    // ---- projected group columns ----

    [Fact]
    public async Task a_projected_group_sum_and_average_match_csharp()
    {
        await using var session = Session();

        var rows = await session.Query<LedgerLine>().GroupBy(x => x.Category)
            .Select(g => new { g.Key, Total = g.Sum(x => x.Amount), Mean = g.Average(x => x.Amount) })
            .ToListAsync(Token);

        var expected = Groups
            .Select(g => new { g.Key, Total = g.Sum(x => x.Amount), Mean = g.Average(x => x.Amount) })
            .ToList();

        rows.OrderBy(x => x.Key).ShouldBe(expected.OrderBy(x => x.Key));
    }

    [Fact]
    public async Task a_projected_group_min_and_max_match_csharp()
    {
        await using var session = Session();

        var rows = await session.Query<LedgerLine>().GroupBy(x => x.Category)
            .Select(g => new { g.Key, Low = g.Min(x => x.Amount), High = g.Max(x => x.Amount) })
            .ToListAsync(Token);

        var expected = Groups
            .Select(g => new { g.Key, Low = g.Min(x => x.Amount), High = g.Max(x => x.Amount) })
            .ToList();

        rows.OrderBy(x => x.Key).ShouldBe(expected.OrderBy(x => x.Key));
    }

    [Fact]
    public async Task a_nullable_member_skips_its_nulls_as_csharp_does()
    {
        await using var session = Session();

        var rows = await session.Query<LedgerLine>().GroupBy(x => x.Category)
            .Select(g => new { g.Key, Total = g.Sum(x => x.Fee), Mean = g.Average(x => x.Fee) })
            .ToListAsync(Token);

        var expected = Groups
            .Select(g => new { g.Key, Total = g.Sum(x => x.Fee), Mean = g.Average(x => x.Fee) })
            .ToList();

        // A group whose fees are all null sums to 0 in C# and to NULL in SQL, which the projection reads
        // back as the member's default; the mean is null on both sides.
        rows.OrderBy(x => x.Key).Select(x => new { x.Key, Total = x.Total ?? 0m, x.Mean })
            .ShouldBe(expected.OrderBy(x => x.Key).Select(x => new { x.Key, Total = x.Total ?? 0m, x.Mean }));
    }

    // ---- ordering by an aggregate ----

    [Fact]
    public async Task ordering_groups_by_a_decimal_sum_matches_csharp()
    {
        await using var session = Session();

        var keys = await session.Query<LedgerLine>().GroupBy(x => x.Category)
            .OrderByDescending(g => g.Sum(x => x.Amount))
            .Select(g => g.Key)
            .ToListAsync(Token);

        keys.ShouldBe(Groups.OrderByDescending(g => g.Sum(x => x.Amount)).Select(g => g.Key).ToList());
    }

    [Fact]
    public async Task ordering_a_single_sum_projection_matches_csharp()
    {
        await using var session = Session();

        var totals = await session.Query<LedgerLine>().GroupBy(x => x.Category)
            .Select(g => g.Sum(x => x.Amount))
            .OrderBy(x => x)
            .ToListAsync(Token);

        totals.ShouldBe(Groups.Select(g => g.Sum(x => x.Amount)).Order().ToList());
    }

    // ---- top-level terminals ----

    [Fact]
    public async Task top_level_sum_matches_csharp()
    {
        await using var session = Session();

        (await session.Query<LedgerLine>().SumAsync(x => x.Amount, Token))
            .ShouldBe(Lines.Sum(x => x.Amount));

        (await session.Query<LedgerLine>().Where(x => x.Category == "a").SumAsync(x => x.Amount, Token))
            .ShouldBe(0.3m);
    }

    [Fact]
    public async Task top_level_sum_over_a_page_matches_csharp()
    {
        await using var session = Session();

        var sum = await session.Query<LedgerLine>().OrderBy(x => x.Amount).Skip(2).Take(6)
            .SumAsync(x => x.Amount, Token);

        sum.ShouldBe(Lines.OrderBy(x => x.Amount).Skip(2).Take(6).Sum(x => x.Amount));
    }

    [Fact]
    public async Task top_level_average_is_the_exact_mean_rounded_once()
    {
        await using var session = Session();

        // AverageAsync returns double by contract, so the oracle is the C# decimal mean rounded once to the
        // nearest double. Parsed from its text rather than cast: decimal's explicit conversion to double is
        // not correctly rounded, and is a last digit off for 0.1666…7.
        (await session.Query<LedgerLine>().Where(x => x.Category == "c").AverageAsync(x => x.Amount, Token))
            .ShouldBe(RoundedOnce(Lines.Where(x => x.Category == "c").Average(x => x.Amount)));

        (await session.Query<LedgerLine>().AverageAsync(x => x.Amount, Token))
            .ShouldBe(RoundedOnce(Lines.Average(x => x.Amount)));
    }

    private static double RoundedOnce(decimal value)
        => double.Parse(value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    [Fact]
    public async Task top_level_min_and_max_match_csharp()
    {
        await using var session = Session();

        (await session.Query<LedgerLine>().MinAsync(x => x.Amount, Token)).ShouldBe(Lines.Min(x => x.Amount));
        (await session.Query<LedgerLine>().MaxAsync(x => x.Amount, Token)).ShouldBe(Lines.Max(x => x.Amount));

        (await session.Query<LedgerLine>().Where(x => x.Category == "e" || x.Category == "f")
                .MaxAsync(x => x.Amount, Token))
            .ShouldBe(0.1000000000000000002m);
    }

    [Fact]
    public async Task an_empty_sum_is_zero_as_it_is_in_csharp()
    {
        await using var session = Session();

        (await session.Query<LedgerLine>().Where(x => x.Category == "none").SumAsync(x => x.Amount, Token))
            .ShouldBe(0m);
    }

    // ---- the other ways in ----

    [Fact]
    public async Task a_duplicated_member_aggregates_its_json_rather_than_its_real_column()
    {
        await using var session = Session();

        var rows = await session.Query<DuplicatedLedgerLine>().GroupBy(x => x.Category)
            .Select(g => new { g.Key, Total = g.Sum(x => x.Amount) })
            .ToListAsync(Token);

        rows.OrderBy(x => x.Key).ShouldBe(DuplicatedLines.GroupBy(x => x.Category)
            .Select(g => new { g.Key, Total = g.Sum(x => x.Amount) }).OrderBy(x => x.Key));

        var matching = await session.Query<DuplicatedLedgerLine>().GroupBy(x => x.Category)
            .Where(g => g.Sum(x => x.Amount) == 0.3m)
            .Select(g => g.Key)
            .ToListAsync(Token);

        matching.Order().ShouldBe(["x", "y"]);

        (await session.Query<DuplicatedLedgerLine>().SumAsync(x => x.Amount, Token))
            .ShouldBe(DuplicatedLines.Sum(x => x.Amount));
    }

    [Fact]
    public async Task distinct_over_grouped_sums_compares_them_as_decimals()
    {
        await using var session = Session();

        var totals = await session.Query<DuplicatedLedgerLine>().GroupBy(x => x.Category)
            .Select(g => g.Sum(x => x.Amount))
            .Distinct()
            .ToListAsync(Token);

        // 0.3m and 0.30m are one value to C#'s Distinct, as they are to the fi_decimal collation.
        totals.Order().ShouldBe(DuplicatedLines.GroupBy(x => x.Category).Select(g => g.Sum(x => x.Amount))
            .Distinct().Order());
        totals.Count.ShouldBe(2);
    }

    [Fact]
    public async Task a_session_on_a_connection_it_was_handed_still_aggregates_exactly()
    {
        // The functions are registered on the connections Fisher opens; a borrowed one gets them too.
        await using var connection = new SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync(Token);

        await using var session = _store.OpenSession(SessionOptions.ForConnection(connection));

        (await session.Query<LedgerLine>().SumAsync(x => x.Amount, Token)).ShouldBe(Lines.Sum(x => x.Amount));
    }
}
