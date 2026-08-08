using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

/// <summary>
///     <c>GroupBy</c>, the <c>Select</c> over a group, and <c>HAVING</c> — fisher#24.
/// </summary>
/// <remarks>
///     The trap this feature was expected to have does not exist. SQLite permits a bare
///     non-aggregated column in a <c>GROUP BY</c> select and picks an arbitrary row for it, where
///     T-SQL rejects the query — so a query that errors on Polecat would silently return arbitrary
///     data here, and the plan was to validate it in the parser. It turns out to be unreachable: the
///     <c>Select</c>'s parameter is the <em>grouping</em>, so there is no ungrouped member in scope to
///     select. The type system does the validation for free.
/// </remarks>
public class grouping_queries : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("grouping");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Catch>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using var session = _store.LightweightSession();
        session.Store(new Catch { Species = "Trout", Weight = 3, Fee = 12.50m, LandedAt = At(1) });
        session.Store(new Catch { Species = "Trout", Weight = 7, Fee = 30.00m, LandedAt = At(3) });
        session.Store(new Catch { Species = "Trout", Weight = 5, Fee = 2.50m, LandedAt = At(5) });
        session.Store(new Catch { Species = "Pike", Weight = 11, Fee = 4.25m, LandedAt = At(2) });
        session.Store(new Catch { Species = "Pike", Weight = 9, Fee = 5.75m, LandedAt = At(6) });
        session.Store(new Catch { Species = "Chub", Weight = 2, Fee = 1.25m, LandedAt = At(4) });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static DateTimeOffset At(int day) => new(2026, 8, day, 9, 0, 0, TimeSpan.Zero);

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private IQuerySession Session() => _store.LightweightSession();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- the shapes ----

    [Fact]
    public async Task group_and_count()
    {
        await using var session = Session();

        var rows = await session.Query<Catch>()
            .GroupBy(x => x.Species)
            .OrderBy(g => g.Key)
            .Select(g => new { Species = g.Key, Count = g.Count() })
            .ToListAsync(Token);

        rows.Select(x => x.Species).ShouldBe(["Chub", "Pike", "Trout"]);
        rows.Select(x => x.Count).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task the_key_alone()
    {
        await using var session = Session();

        (await session.Query<Catch>().GroupBy(x => x.Species).OrderBy(g => g.Key)
            .Select(g => g.Key).ToListAsync(Token)).ShouldBe(["Chub", "Pike", "Trout"]);
    }

    [Fact]
    public async Task every_aggregate_over_a_group()
    {
        await using var session = Session();

        var rows = await session.Query<Catch>()
            .GroupBy(x => x.Species)
            .Select(g => new
            {
                g.Key,
                Total = g.Sum(x => x.Weight),
                Heaviest = g.Max(x => x.Weight),
                Lightest = g.Min(x => x.Weight),
                Mean = g.Average(x => x.Weight),
                Fees = g.Sum(x => x.Fee)
            })
            .ToListAsync(Token);

        var trout = rows.Single(x => x.Key == "Trout");
        trout.Total.ShouldBe(15);
        trout.Heaviest.ShouldBe(7);
        trout.Lightest.ShouldBe(3);
        trout.Mean.ShouldBe(5.0);
        trout.Fees.ShouldBe(45.00m);
    }

    /// <summary>
    ///     A grouped projection goes through the same conversions everything else does, so a key that
    ///     is a timestamp comes back as one.
    /// </summary>
    [Fact]
    public async Task a_group_key_that_is_not_a_string()
    {
        await using var session = Session();

        var rows = await session.Query<Catch>()
            .GroupBy(x => x.LandedAt)
            .OrderBy(g => g.Key)
            .Select(g => new { When = g.Key, Count = g.Count() })
            .ToListAsync(Token);

        rows.Count.ShouldBe(6);
        rows[0].When.ShouldBe(At(1));
    }

    [Fact]
    public async Task a_constructor_over_a_group()
    {
        await using var session = Session();

        (await session.Query<Catch>().GroupBy(x => x.Species)
            .OrderBy(g => g.Key).Select(g => new Tally(g.Key, g.Count())).ToListAsync(Token))
            .Select(x => x.Species).ShouldBe(["Chub", "Pike", "Trout"]);
    }

    // ---- composition ----

    [Fact]
    public async Task a_where_before_the_group_filters_rows()
    {
        await using var session = Session();

        var rows = await session.Query<Catch>()
            .Where(x => x.Weight > 2)
            .GroupBy(x => x.Species)
            .OrderBy(g => g.Key)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(Token);

        // Chub's only catch weighs 2, so the group disappears entirely rather than counting 0.
        rows.Select(x => x.Key).ShouldBe(["Pike", "Trout"]);
    }

    /// <summary>
    ///     A <c>Where</c> after the <c>GroupBy</c> is a <c>HAVING</c>: it filters groups, not rows.
    /// </summary>
    [Fact]
    public async Task a_where_after_the_group_becomes_having()
    {
        await using var session = Session();

        (await session.Query<Catch>().GroupBy(x => x.Species)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key).OrderBy(x => x).ToListAsync(Token))
            .ShouldBe(["Pike", "Trout"]);

        (await session.Query<Catch>().GroupBy(x => x.Species)
            .Where(g => g.Sum(x => x.Weight) >= 15)
            .Select(g => g.Key).OrderBy(x => x).ToListAsync(Token))
            .ShouldBe(["Pike", "Trout"]);
    }

    [Fact]
    public async Task having_composes_and_reverses()
    {
        await using var session = Session();

        (await session.Query<Catch>().GroupBy(x => x.Species)
            .Where(g => g.Count() > 1 && g.Max(x => x.Weight) > 10)
            .Select(g => g.Key).ToListAsync(Token)).ShouldBe(["Pike"]);

        // Reversed operands: `1 < g.Count()` is `count(*) > 1`.
        (await session.Query<Catch>().GroupBy(x => x.Species)
            .Where(g => 1 < g.Count())
            .Select(g => g.Key).OrderBy(x => x).ToListAsync(Token)).ShouldBe(["Pike", "Trout"]);
    }

    /// <summary>
    ///     Ordering a grouped query by an aggregate is the reason the feature is usually reached for.
    /// </summary>
    [Fact]
    public async Task ordering_by_an_aggregate()
    {
        await using var session = Session();

        (await session.Query<Catch>().GroupBy(x => x.Species)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => g.Key).ToListAsync(Token)).ShouldBe(["Trout", "Pike", "Chub"]);
    }

    [Fact]
    public async Task grouping_composes_with_paging_and_the_terminals()
    {
        await using var session = Session();

        (await session.Query<Catch>().GroupBy(x => x.Species)
            .OrderBy(g => g.Key).Take(2).Select(g => g.Key).ToListAsync(Token))
            .ShouldBe(["Chub", "Pike"]);

        (await session.Query<Catch>().GroupBy(x => x.Species).Select(g => g.Key)
            .CountAsync(Token)).ShouldBe(3);

        (await session.Query<Catch>().GroupBy(x => x.Species)
            .Where(g => g.Count() > 5).Select(g => g.Key).AnyAsync(Token)).ShouldBeFalse();

        (await session.Query<Catch>().GroupBy(x => x.Species)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstAsync(Token)).ShouldBe("Trout");
    }

    // ---- refusals ----

    [Fact]
    public async Task a_group_by_without_a_select_is_refused_by_name()
    {
        await using var session = Session();

        var exception = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Catch>().GroupBy(x => x.Species).CountAsync(Token));

        exception.Message.ShouldContain("needs a Select");
    }

    [Fact]
    public async Task the_element_selector_overload_is_refused_by_name()
    {
        await using var session = Session();

        var exception = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Catch>().GroupBy(x => x.Species, x => x.Weight)
                .Select(g => g.Key).ToListAsync(Token));

        exception.Message.ShouldContain("single key selector");
    }

    [Fact]
    public async Task a_having_that_is_not_about_the_group_is_refused_by_name()
    {
        await using var session = Session();

        var exception = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Catch>().GroupBy(x => x.Species)
                .Where(g => g.Key.Length > 100 && g.Count() > 0)
                .Select(g => g.Key).ToListAsync(Token));

        exception.Message.ShouldContain("is the group's key or an aggregate over it");
    }

    /// <summary>
    ///     The same two guards the query-level aggregates apply, because SQLite's <c>sum()</c> over a
    ///     non-number returns 0 rather than failing.
    /// </summary>
    [Fact]
    public async Task summing_a_non_numeric_member_over_a_group_is_refused()
    {
        await using var session = Session();

        var exception = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Catch>().GroupBy(x => x.Species)
                .Select(g => new { g.Key, Total = g.Sum(x => (int)x.Rating) })
                .ToListAsync(Token));

        exception.Message.ShouldContain("not a number");
    }

    /// <summary>
    ///     A NULL column — an absent JSON key, or an aggregate over a group that matched nothing —
    ///     unboxes to the projection's declared type. Without a default for a non-nullable value type
    ///     that is a NullReferenceException from inside generated code, naming nothing.
    /// </summary>
    [Fact]
    public async Task a_null_column_becomes_the_default_rather_than_throwing()
    {
        await using var session = Session();

        var rows = await session.Query<Catch>().GroupBy(x => x.Species)
            .OrderBy(g => g.Key)
            .Select(g => new { g.Key, Missing = g.Sum(x => x.Absent) })
            .ToListAsync(Token);

        rows.Select(x => x.Missing).ShouldAllBe(x => x == 0);
    }

    public class Catch
    {
        public Guid Id { get; set; }
        public string Species { get; set; } = "";
        public int Weight { get; set; }
        public decimal Fee { get; set; }
        public DateTimeOffset LandedAt { get; set; }
        public Rating Rating { get; set; }

        /// <summary>
        ///     Never serialized, so <c>json_extract</c> yields SQL NULL for it — which is the shape an
        ///     absent key has, and the one that unboxes badly.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public int Absent { get; set; }
    }

    public enum Rating
    {
        Poor,
        Good
    }

    public record Tally(string Species, int Count);
}
