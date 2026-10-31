using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

public class Stamp
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int? Count { get; set; }
    public decimal? Price { get; set; }
    public DateTimeOffset At { get; set; }
    public DateTimeOffset? MaybeAt { get; set; }
    public DateTime Plain { get; set; }
    public DateOnly Day { get; set; }
    public TimeOnly Time { get; set; }
    public TimeSpan Span { get; set; }
    public Uri? Site { get; set; }
}

/// <summary>
///     fisher#483. There is nothing below one JSON value for a path to reach, so a member read off a scalar
///     used to be valid SQL yielding NULL: a <c>Where</c> matched nothing, a <c>Select</c> returned the
///     type's default and an <c>OrderBy</c> did not order. All silently.
/// </summary>
public class members_below_a_scalar : IAsyncLifetime
{
    /// <summary>The other side of New Year in UTC, so a part computed after folding to UTC is wrong.</summary>
    private static readonly DateTimeOffset NewYearsEve = new(2025, 12, 31, 23, 30, 45, TimeSpan.FromHours(-2));

    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("below-a-scalar");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Stamp>().Duplicate(x => x.MaybeAt!);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();

        session.Store(new Stamp
        {
            Id = Guid.NewGuid(), Name = "abcd", Count = 5, Price = 5.5m, At = NewYearsEve, MaybeAt = NewYearsEve,
            Plain = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc), Day = new DateOnly(2026, 3, 4),
            Time = new TimeOnly(5, 6, 7), Span = TimeSpan.FromHours(3), Site = new Uri("https://example.com/")
        });

        session.Store(new Stamp
        {
            Id = Guid.NewGuid(), Name = "ab", Count = 1, At = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            Plain = new DateTime(2027, 11, 12, 13, 14, 15, DateTimeKind.Utc), Day = new DateOnly(2027, 11, 12),
            Time = new TimeOnly(13, 14, 15)
        });

        session.Store(new Stamp { Id = Guid.NewGuid(), Name = "", At = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) });

        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<int> CountAsync(System.Linq.Expressions.Expression<Func<Stamp, bool>> predicate)
    {
        await using var session = _store.QuerySession();
        return await session.Query<Stamp>().Where(predicate).CountAsync(Token);
    }

    [Fact]
    public async Task nullable_value_is_the_member_itself_in_a_predicate()
    {
        (await CountAsync(x => x.Count!.Value > 2)).ShouldBe(1);
        (await CountAsync(x => x.Price!.Value > 2)).ShouldBe(1);
        (await CountAsync(x => x.MaybeAt!.Value > DateTimeOffset.MinValue)).ShouldBe(1);
    }

    [Fact]
    public async Task nullable_value_orders_by_the_member()
    {
        await using var session = _store.QuerySession();

        var names = await session.Query<Stamp>().Where(x => x.Count.HasValue)
            .OrderByDescending(x => x.Count!.Value)
            .Select(x => x.Name)
            .ToListAsync(Token);

        names.ShouldBe(["abcd", "ab"]);
    }

    [Fact]
    public async Task selecting_nullable_value_reads_the_member_and_defaults_a_missing_one()
    {
        await using var session = _store.QuerySession();

        var counts = await session.Query<Stamp>().OrderByDescending(x => x.Name).Select(x => x.Count!.Value)
            .ToListAsync(Token);

        counts.ShouldBe([5, 1, 0]);
    }

    /// <summary>
    ///     The discriminating fact for reading the stored text. Folded to UTC this value is 01:30 on the
    ///     first of January 2026, so <c>strftime</c> gets every one of these parts wrong.
    /// </summary>
    [Fact]
    public async Task date_parts_are_read_at_the_values_own_offset()
    {
        (await CountAsync(x => x.At.Year == 2025)).ShouldBe(1);
        (await CountAsync(x => x.At.Month == 12)).ShouldBe(1);
        (await CountAsync(x => x.At.Day == 31)).ShouldBe(1);
        (await CountAsync(x => x.At.Hour == 23)).ShouldBe(1);
        (await CountAsync(x => x.At.Minute == 30)).ShouldBe(1);
        (await CountAsync(x => x.At.Second == 45)).ShouldBe(1);
    }

    /// <summary>A duplicated date column holds the UTC form, so reading the column would be wrong.</summary>
    [Fact]
    public async Task a_duplicated_date_still_answers_at_its_own_offset()
    {
        (await CountAsync(x => x.MaybeAt!.Value.Year == 2025)).ShouldBe(1);
    }

    [Fact]
    public async Task every_date_type_has_its_parts()
    {
        (await CountAsync(x => x.Plain.Year == 2027 && x.Plain.Month == 11 && x.Plain.Day == 12)).ShouldBe(1);
        (await CountAsync(x => x.Plain.Hour == 13 && x.Plain.Minute == 14 && x.Plain.Second == 15)).ShouldBe(1);
        (await CountAsync(x => x.Day.Year == 2026 && x.Day.Month == 3 && x.Day.Day == 4)).ShouldBe(1);
        (await CountAsync(x => x.Time.Hour == 5 && x.Time.Minute == 6 && x.Time.Second == 7)).ShouldBe(1);
    }

    [Fact]
    public async Task a_date_part_can_be_selected_ordered_and_grouped()
    {
        await using var session = _store.QuerySession();

        (await session.Query<Stamp>().OrderByDescending(x => x.Plain.Year).Select(x => x.Plain.Year)
            .ToListAsync(Token)).ShouldBe([2027, 2026, 1]);

        var years = await session.Query<Stamp>().GroupBy(x => x.At.Year)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(Token);

        years.Single(x => x.Key == 2026).Count.ShouldBe(2);
        years.Single(x => x.Key == 2025).Count.ShouldBe(1);
    }

    [Fact]
    public async Task has_value_and_length_work_outside_a_predicate()
    {
        await using var session = _store.QuerySession();

        (await session.Query<Stamp>().OrderByDescending(x => x.Name.Length).Select(x => x.Name.Length)
            .ToListAsync(Token)).ShouldBe([4, 2, 0]);

        (await session.Query<Stamp>().OrderByDescending(x => x.Name).Select(x => x.Count.HasValue)
            .ToListAsync(Token)).ShouldBe([true, true, false]);
    }

    [Fact]
    public async Task what_cannot_be_translated_is_refused_by_name_in_every_position()
    {
        await using var session = _store.QuerySession();

        var filtered = await Should.ThrowAsync<BadLinqExpressionException>(
            () => session.Query<Stamp>().Where(x => x.At.Date == DateTime.Today).ToListAsync(Token));
        filtered.Message.ShouldContain("DateTimeOffset.Date");

        var ordered = await Should.ThrowAsync<BadLinqExpressionException>(
            () => session.Query<Stamp>().OrderBy(x => x.Span.TotalHours).ToListAsync(Token));
        ordered.Message.ShouldContain("TimeSpan.TotalHours");

        var selected = await Should.ThrowAsync<BadLinqExpressionException>(
            () => session.Query<Stamp>().Select(x => x.Site!.Host).ToListAsync(Token));
        selected.Message.ShouldContain("Uri.Host");

        await Should.ThrowAsync<BadLinqExpressionException>(
            () => session.Query<Stamp>().Where(x => x.At.DayOfWeek == DayOfWeek.Monday).ToListAsync(Token));
    }
}
