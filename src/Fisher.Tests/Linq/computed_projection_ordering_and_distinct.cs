using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

public enum TallyShade
{
    Pale,
    Deep
}

public class Tally
{
    public Guid Id { get; set; }
    public int Number { get; set; }
    public int? Maybe { get; set; }
    public long Big { get; set; }
    public short Small { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public TallyShade Shade { get; set; }
}

/// <summary>
///     <c>Distinct()</c> and identity <c>OrderBy</c> after a <c>Select</c> that computes its value —
///     fisher#590. LINQ to Objects over the same rows is the oracle throughout.
/// </summary>
/// <remarks>
///     Both operators act on the select list, which used to hold the members the projection read rather
///     than the value it computed. The data carries negative numbers, the type minimums and nulls because
///     those are exactly where SQLite's arithmetic and .NET's could part company.
/// </remarks>
public class computed_projection_ordering_and_distinct : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("computed-projection");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly List<Tally> Tallies =
    [
        new() { Id = Guid.NewGuid(), Number = 3, Maybe = 4, Big = 30, Small = 7, Name = "delta", Category = "b" },
        new() { Id = Guid.NewGuid(), Number = 1, Maybe = null, Big = -11, Small = -3, Name = "alpha", Category = "a" },
        new() { Id = Guid.NewGuid(), Number = 5, Maybe = 5, Big = long.MinValue, Small = 4, Name = "echo", Category = "b" },
        new() { Id = Guid.NewGuid(), Number = -7, Maybe = -2, Big = 8, Small = 5, Name = "charlie", Category = "c", Shade = TallyShade.Deep },
        new() { Id = Guid.NewGuid(), Number = 4, Maybe = null, Big = 0, Small = -1, Name = "bravo", Category = "a" },
        new() { Id = Guid.NewGuid(), Number = int.MinValue, Maybe = 7, Big = 2, Small = 2, Name = "fox", Category = "b" },
        new() { Id = Guid.NewGuid(), Number = -1, Maybe = 1, Big = -5, Small = 0, Name = "golf", Category = "d", Shade = TallyShade.Deep }
    ];

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Tally>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        foreach (var tally in Tallies) session.Store(tally);
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private IQuerySession Session() => _store.LightweightSession();

    private static void ShouldBeTheSameSet<T>(IEnumerable<T> actual, IEnumerable<T> expected)
        => actual.Select(x => x?.ToString()).Order().ShouldBe(expected.Select(x => x?.ToString()).Order());

    // ---- identity ordering over a computed projection ----

    [Fact]
    public async Task ordering_a_modulo_projection_orders_by_the_remainder()
    {
        await using var session = Session();

        (await session.Query<Tally>().Select(x => x.Number % 3).OrderBy(n => n).ToListAsync(Token))
            .ShouldBe(Tallies.Select(x => x.Number % 3).OrderBy(n => n));
    }

    [Fact]
    public async Task ordering_a_negated_projection_keeps_the_minimum_wrapping()
    {
        await using var session = Session();

        // -int.MinValue is int.MinValue in unchecked .NET, which is where SQLite would otherwise widen.
        (await session.Query<Tally>().Select(x => -x.Number).OrderBy(n => n).ToListAsync(Token))
            .ShouldBe(Tallies.Select(x => -x.Number).OrderBy(n => n));
    }

    [Fact]
    public async Task ordering_a_negated_long_projection_keeps_the_minimum_wrapping()
    {
        await using var session = Session();

        (await session.Query<Tally>().Select(x => -x.Big).OrderByDescending(n => n).ToListAsync(Token))
            .ShouldBe(Tallies.Select(x => -x.Big).OrderByDescending(n => n));
    }

    [Fact]
    public async Task the_first_of_an_ordered_negated_division_is_the_smallest_value()
    {
        await using var session = Session();

        (await session.Query<Tally>().Select(x => -(x.Number / 2)).OrderBy(n => n).Skip(1).FirstAsync(Token))
            .ShouldBe(Tallies.Select(x => -(x.Number / 2)).OrderBy(n => n).Skip(1).First());
    }

    [Fact]
    public async Task ordering_a_computed_group_projection()
    {
        await using var session = Session();

        (await session.Query<Tally>().GroupBy(x => x.Category).Select(g => -g.Count()).OrderBy(n => n)
                .ToListAsync(Token))
            .ShouldBe(Tallies.GroupBy(x => x.Category).Select(g => -g.Count()).OrderBy(n => n));
    }

    // ---- Distinct over a computed projection ----

    [Fact]
    public async Task distinct_over_a_modulo_projection_deduplicates_the_remainders()
    {
        await using var session = Session();

        ShouldBeTheSameSet(
            await session.Query<Tally>().Select(x => x.Number % 3).Distinct().ToListAsync(Token),
            Tallies.Select(x => x.Number % 3).Distinct());
    }

    [Fact]
    public async Task counting_a_distinct_modulo_projection_counts_the_remainders()
    {
        await using var session = Session();

        (await session.Query<Tally>().Select(x => x.Number % 3).Distinct().CountAsync(Token))
            .ShouldBe(Tallies.Select(x => x.Number % 3).Distinct().Count());
    }

    [Fact]
    public async Task distinct_over_a_computed_anonymous_projection()
    {
        await using var session = Session();

        ShouldBeTheSameSet(
            await session.Query<Tally>().Select(x => new { M = x.Number % 2 }).Distinct().ToListAsync(Token),
            Tallies.Select(x => new { M = x.Number % 2 }).Distinct());
    }

    [Fact]
    public async Task distinct_over_a_nullable_modulo_keeps_the_null()
    {
        await using var session = Session();

        ShouldBeTheSameSet(
            await session.Query<Tally>().Select(x => x.Maybe % 3).Distinct().ToListAsync(Token),
            Tallies.Select(x => x.Maybe % 3).Distinct());
    }

    [Fact]
    public async Task distinct_over_a_widened_short_division()
    {
        await using var session = Session();

        ShouldBeTheSameSet(
            await session.Query<Tally>().Select(x => x.Small / 2).Distinct().ToListAsync(Token),
            Tallies.Select(x => x.Small / 2).Distinct());
    }

    [Fact]
    public async Task distinct_over_a_computed_group_projection()
    {
        await using var session = Session();

        ShouldBeTheSameSet(
            await session.Query<Tally>().GroupBy(x => x.Category).Select(g => g.Count() / 3).Distinct()
                .ToListAsync(Token),
            Tallies.GroupBy(x => x.Category).Select(g => g.Count() / 3).Distinct());
    }

    [Fact]
    public async Task the_arithmetic_moves_into_sql_only_when_an_operator_needs_it()
    {
        await using var session = Session();

        // An ordinary projection keeps computing in .NET — the boundary SelectProjection documents.
        session.ToSql(session.Query<Tally>().Select(x => x.Number % 3)).ShouldNotContain("% 3");
        session.ToSql(session.Query<Tally>().Select(x => x.Number % 3).Distinct()).ShouldContain("% 3");
    }

    // ---- refused rather than answered wrongly ----

    [Fact]
    public async Task ordering_a_multiplied_projection_is_refused()
    {
        await using var session = Session();

        // x * -1 overflows differently in .NET and SQLite, so it is not translated.
        var exception = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Tally>().Select(x => x.Number * -1).OrderBy(n => n).FirstAsync(Token));

        exception.Message.ShouldContain("OrderBy");
        exception.Message.ShouldContain("computes its value in .NET");
    }

    [Fact]
    public async Task distinct_over_a_string_computation_is_refused()
    {
        await using var session = Session();

        var exception = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Tally>().Select(x => x.Name.ToUpper()).Distinct().ToListAsync(Token));

        exception.Message.ShouldContain("Distinct()");
    }

    [Fact]
    public async Task distinct_over_a_floating_point_division_is_refused()
    {
        await using var session = Session();

        await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Tally>().Select(x => (double)x.Number / 2).Distinct().ToListAsync(Token));
    }

    [Fact]
    public async Task distinct_over_a_divisor_of_minus_one_is_refused()
    {
        await using var session = Session();

        // int.MinValue % -1 throws in .NET and is 0 in SQLite.
        await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Tally>().Select(x => x.Number % -1).Distinct().CountAsync(Token));
    }

    [Fact]
    public async Task distinct_over_a_converted_enum_is_refused()
    {
        await using var session = Session();

        await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Tally>().Select(x => (int)x.Shade % 2).Distinct().ToListAsync(Token));
    }

    [Fact]
    public async Task distinct_over_a_multiplied_group_projection_is_refused()
    {
        await using var session = Session();

        await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Tally>().GroupBy(x => x.Category).Select(g => g.Count() * 2).Distinct()
                .ToListAsync(Token));
    }

    // ---- faithful projections are unchanged ----

    [Fact]
    public async Task a_length_projection_still_orders_and_deduplicates()
    {
        await using var session = Session();

        (await session.Query<Tally>().Select(x => x.Name.Length).OrderBy(n => n).ToListAsync(Token))
            .ShouldBe(Tallies.Select(x => x.Name.Length).OrderBy(n => n));

        ShouldBeTheSameSet(
            await session.Query<Tally>().Select(x => x.Name.Length).Distinct().ToListAsync(Token),
            Tallies.Select(x => x.Name.Length).Distinct());
    }

    [Fact]
    public async Task a_widening_conversion_is_still_a_copy()
    {
        await using var session = Session();

        (await session.Query<Tally>().Select(x => (long)x.Number).OrderBy(n => n).ToListAsync(Token))
            .ShouldBe(Tallies.Select(x => (long)x.Number).OrderBy(n => n));
    }

    [Fact]
    public async Task a_member_copy_and_a_constant_still_deduplicate()
    {
        await using var session = Session();

        ShouldBeTheSameSet(
            await session.Query<Tally>().Select(x => new { x.Category, Tag = "t" }).Distinct().ToListAsync(Token),
            Tallies.Select(x => new { x.Category, Tag = "t" }).Distinct());
    }

    [Fact]
    public async Task a_computed_projection_with_no_operator_over_it_still_computes_in_dotnet()
    {
        await using var session = Session();

        (await session.Query<Tally>().OrderBy(x => x.Name).Select(x => x.Number * -1).ToListAsync(Token))
            .ShouldBe(Tallies.OrderBy(x => x.Name).Select(x => x.Number * -1));
    }
}
