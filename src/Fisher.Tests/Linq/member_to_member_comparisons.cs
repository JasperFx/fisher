using System.Linq.Expressions;
using Fisher.Linq;
using JasperFx;
using Shouldly;
using Weasel.Core;

namespace Fisher.Tests.Linq;

public enum Pace
{
    Slow = 1,
    Steady = 2,
    Fast = 3
}

public class PairAddress
{
    public string Street { get; set; } = "";
}

public class Pair
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";

    public int A { get; set; }
    public int B { get; set; }
    public long Long { get; set; }
    public int? NullableA { get; set; }
    public int? NullableB { get; set; }

    public decimal Price { get; set; }
    public decimal Cost { get; set; }
    public double Ratio { get; set; }

    public DateTimeOffset Started { get; set; }
    public DateTimeOffset Finished { get; set; }
    public DateTimeOffset? Reviewed { get; set; }
    public DateTime LocalStamp { get; set; }
    public DateOnly Opened { get; set; }
    public DateOnly Closed { get; set; }

    public string First { get; set; } = "";
    public string? Second { get; set; }

    public Pace Planned { get; set; }
    public Pace Actual { get; set; }

    public Guid Owner { get; set; }
    public Guid Reviewer { get; set; }

    public bool Paid { get; set; }
    public bool Shipped { get; set; }

    public List<string> Tags { get; set; } = [];
    public PairAddress Home { get; set; } = new();
    public PairAddress Work { get; set; } = new();
}

/// <summary>
///     fisher#595: a comparison with a document member on BOTH sides (<c>x.A &lt; x.B</c>,
///     <c>x.A - 1 &lt; x.B</c>) used to compile one side as a constant and fail with an internal "variable
///     'x' ... is not defined". It now translates both sides as locators, or is refused by name. LINQ to
///     Objects over the same documents is the oracle for every translated shape.
/// </summary>
public class member_to_member_comparisons : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("member-to-member");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly Guid SharedOwner = Guid.NewGuid();
    private static readonly Guid OneId = Guid.NewGuid();
    private static DateTimeOffset At(int hour) => new(2026, 11, 16, hour, 0, 0, TimeSpan.Zero);

    private static readonly List<Pair> Pairs =
    [
        new()
        {
            Id = OneId, Name = "one", A = 1, B = 2, Long = 1, NullableA = null, NullableB = null,
            Price = 1.10m, Cost = 1.10m, Ratio = 0.5, Started = At(1), Finished = At(3),
            Reviewed = null, Opened = new DateOnly(2026, 1, 1), Closed = new DateOnly(2026, 1, 2),
            First = "a", Second = "a", Planned = Pace.Slow, Actual = Pace.Fast, Owner = OneId,
            Reviewer = OneId, Paid = true, Shipped = true
        },
        new()
        {
            Id = Guid.NewGuid(), Name = "two", A = 5, B = 5, Long = 9, NullableA = 5, NullableB = null,
            Price = 2.50m, Cost = 3.75m, Ratio = 6.5, Started = At(5), Finished = At(5),
            Reviewed = At(5), Opened = new DateOnly(2026, 3, 1), Closed = new DateOnly(2026, 2, 1),
            First = "b", Second = null, Planned = Pace.Steady, Actual = Pace.Steady, Owner = Guid.NewGuid(),
            Reviewer = Guid.NewGuid(), Paid = false, Shipped = true
        },
        new()
        {
            // Finished is the SAME instant as Started written at another offset, so a comparison of the
            // stored text would call them different and the strftime-normalised one must not.
            Id = Guid.NewGuid(), Name = "three", A = 9, B = 3, Long = 3, NullableA = 7, NullableB = 7,
            Price = 10m, Cost = 9.99m, Ratio = 9.5, Started = At(8),
            Finished = new DateTimeOffset(2026, 11, 16, 3, 0, 0, TimeSpan.FromHours(-5)),
            Reviewed = At(9), Opened = new DateOnly(2026, 5, 5), Closed = new DateOnly(2026, 5, 5),
            First = "ccc", Second = "c", Planned = Pace.Fast, Actual = Pace.Slow, Owner = SharedOwner,
            Reviewer = Guid.NewGuid(), Paid = false, Shipped = false
        },
        new()
        {
            Id = Guid.NewGuid(), Name = "four", A = 0, B = 1, Long = -1, NullableA = null, NullableB = 2,
            Price = 0.5m, Cost = 0.25m, Ratio = -1, Started = At(12), Finished = At(14),
            Reviewed = At(10), Opened = new DateOnly(2025, 12, 31), Closed = new DateOnly(2026, 1, 1),
            First = "dd", Second = "dd", Planned = Pace.Steady, Actual = Pace.Fast, Owner = Guid.NewGuid(),
            Reviewer = Guid.NewGuid(), Paid = true, Shipped = false
        },
        new()
        {
            Id = Guid.NewGuid(), Name = "five", A = 3, B = 4, Long = 3, NullableA = 2, NullableB = 3,
            Price = 4m, Cost = 4.00m, Ratio = 3, Started = At(20), Finished = At(18),
            Reviewed = At(20), Opened = new DateOnly(2026, 7, 1), Closed = new DateOnly(2026, 8, 1),
            First = "e", Second = "f", Planned = Pace.Fast, Actual = Pace.Fast, Owner = Guid.NewGuid(),
            Reviewer = Guid.NewGuid(), Paid = false, Shipped = false
        }
    ];

    private static readonly List<int> Thresholds = [1, 2, 3];

    /// <summary>Every shape that translates, by name, so the theory rows serialize (see CLAUDE.md).</summary>
    private static readonly Dictionary<string, Expression<Func<Pair, bool>>> Translated = new()
    {
        ["int_less_than"] = x => x.A < x.B,
        ["int_less_than_or_equal"] = x => x.A <= x.B,
        ["int_greater_than"] = x => x.A > x.B,
        ["int_greater_than_or_equal"] = x => x.A >= x.B,
        ["int_equal"] = x => x.A == x.B,
        ["int_not_equal"] = x => x.A != x.B,
        ["int_against_long"] = x => x.A > x.Long,
        ["decimal_less_than"] = x => x.Price < x.Cost,
        ["decimal_equal_across_scales"] = x => x.Price == x.Cost,
        ["decimal_against_int"] = x => x.Price > x.A,
        ["double_against_int"] = x => x.Ratio > x.A,
        ["nullable_equal_is_true_when_both_are_null"] = x => x.NullableA == x.NullableB,
        ["nullable_not_equal_is_true_when_one_is_null"] = x => x.NullableA != x.NullableB,
        ["nullable_less_than"] = x => x.NullableA < x.NullableB,
        ["negated_nullable_less_than_keeps_the_null_rows"] = x => !(x.NullableA < x.NullableB),
        ["negated_nullable_equal"] = x => !(x.NullableA == x.NullableB),
        ["nullable_against_non_nullable"] = x => x.NullableA == x.A,
        ["timestamp_less_than"] = x => x.Started < x.Finished,
        ["timestamp_equal_across_offsets"] = x => x.Started == x.Finished,
        ["timestamp_greater_than_or_equal"] = x => x.Started >= x.Finished,
        ["nullable_timestamp_equal"] = x => x.Reviewed == x.Started,
        ["negated_nullable_timestamp_greater_than"] = x => !(x.Reviewed > x.Finished),
        ["date_only_less_than"] = x => x.Opened < x.Closed,
        ["date_only_equal"] = x => x.Opened == x.Closed,
        ["string_equal"] = x => x.First == x.Second,
        ["string_not_equal"] = x => x.First != x.Second,
        ["string_length_against_member"] = x => x.First.Length < x.B,
        ["integer_enum_equal"] = x => x.Planned == x.Actual,
        ["integer_enum_less_than"] = x => x.Planned < x.Actual,
        ["integer_enum_against_an_int"] = x => (int)x.Planned < x.B,
        ["guid_equal"] = x => x.Owner == x.Reviewer,
        ["guid_not_equal"] = x => x.Owner != x.Reviewer,
        ["identity_against_a_guid_member"] = x => x.Id == x.Owner,
        ["bool_equal"] = x => x.Paid == x.Shipped,
        ["bool_not_equal"] = x => x.Paid != x.Shipped,
        ["arithmetic_on_the_left"] = x => x.A - 1 < x.B,
        ["arithmetic_on_the_right"] = x => x.B > x.A - 1,
        ["arithmetic_on_both_sides"] = x => x.A + x.B > x.Long + 2,
        ["arithmetic_over_nullable_members"] = x => x.NullableA + 1 == x.NullableB,
        ["double_arithmetic"] = x => x.Ratio - 0.5 >= x.A,
        ["composed_with_a_constant_comparison"] = x => x.A < x.B && x.A > 0,
        ["member_against_a_count_over_a_captured_list"] = x => x.A > Thresholds.Count(t => t > 1)
    };

    public static TheoryData<string> TranslatedNames() => new(Translated.Keys);

    /// <summary>Every shape that is refused by name rather than guessed at.</summary>
    private static readonly Dictionary<string, Expression<Func<Pair, bool>>> Refused = new()
    {
        ["narrowing_a_double_to_an_int"] = x => (int)x.Ratio == x.A,
        ["decimal_arithmetic"] = x => x.Price - 0.1m < x.Cost,
        ["multiplication"] = x => x.A * 2 < x.B,
        ["a_method_call_on_one_side"] = x => x.First.ToUpper() == x.Second,
        ["a_collection_count_against_a_member"] = x => x.Tags.Count() > x.A,
        ["a_modulo_by_a_member"] = x => x.A % x.B == 0,
        ["timestamp_against_a_date_time"] = x => x.Started == x.LocalStamp,
        ["two_objects_by_reference"] = x => x.Home == x.Work,
        ["boxed_values_by_reference"] = x => (object)x.A == (object)x.B
    };

    public static TheoryData<string> RefusedNames() => new(Refused.Keys);

    public async ValueTask InitializeAsync()
    {
        _store = StoreFor(_database, EnumStorage.AsInteger);
        await SeedAsync(_store);
    }

    private static DocumentStore StoreFor(TemporaryDatabase database, EnumStorage enumStorage,
        bool duplicateA = false)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.ConfigureSerialization(enumStorage: enumStorage);

            var mapping = options.Schema.For<Pair>();
            if (duplicateA)
            {
                mapping.Duplicate(x => x.A);
            }
        });

    private static async Task SeedAsync(DocumentStore store)
    {
        await store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = store.LightweightSession();
        foreach (var pair in Pairs) session.Store(pair);
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static async Task<List<string>> NamesAsync(DocumentStore store, Expression<Func<Pair, bool>> predicate)
    {
        await using var session = store.QuerySession();
        var names = await session.Query<Pair>().Where(predicate).Select(x => x.Name).ToListAsync(Token);
        return names.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private static List<string> Oracle(Expression<Func<Pair, bool>> predicate)
        => Pairs.Where(predicate.Compile()).Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();

    [Theory]
    [MemberData(nameof(TranslatedNames))]
    public async Task translates_and_agrees_with_linq_to_objects(string name)
    {
        var predicate = Translated[name];

        (await NamesAsync(_store, predicate)).ShouldBe(Oracle(predicate));
    }

    [Fact]
    public void the_data_tells_the_shapes_apart()
    {
        // A translated shape that returned everything or nothing would agree with an oracle that also
        // did. Every predicate here selects some rows and leaves others out.
        foreach (var (name, predicate) in Translated)
        {
            var count = Oracle(predicate).Count;
            count.ShouldBeGreaterThan(0, name);
            count.ShouldBeLessThan(Pairs.Count, name);
        }
    }

    [Theory]
    [MemberData(nameof(RefusedNames))]
    public async Task is_refused_by_name(string name)
    {
        await using var session = _store.QuerySession();

        var refusal = await Should.ThrowAsync<BadLinqExpressionException>(
            () => session.Query<Pair>().Where(Refused[name]).ToListAsync(Token));

        refusal.Message.ShouldStartWith("Cannot translate");
    }

    [Fact]
    public void both_sides_are_typed_locators_and_equality_is_null_safe()
    {
        using var session = _store.QuerySession();

        var timestamps = session.ToSql(session.Query<Pair>().Where(x => x.Started < x.Finished));
        timestamps.ShouldContain(
            "coalesce(strftime('%Y-%m-%dT%H:%M:%f', json_extract(data, '$.started')) < "
            + "strftime('%Y-%m-%dT%H:%M:%f', json_extract(data, '$.finished')), 0)",
            Case.Insensitive);

        var equality = session.ToSql(session.Query<Pair>().Where(x => x.NullableA == x.NullableB));
        equality.ShouldContain(" is json_extract(", Case.Insensitive);

        var inequality = session.ToSql(session.Query<Pair>().Where(x => x.NullableA != x.NullableB));
        inequality.ShouldContain(" is not json_extract(", Case.Insensitive);
    }

    [Fact]
    public async Task a_string_stored_enum_compares_for_equality_and_refuses_a_range()
    {
        using var database = TemporaryDatabase.Create("member-to-member-string-enum");
        await using var store = StoreFor(database, EnumStorage.AsString);
        await SeedAsync(store);

        Expression<Func<Pair, bool>> equal = x => x.Planned == x.Actual;
        (await NamesAsync(store, equal)).ShouldBe(Oracle(equal));

        Expression<Func<Pair, bool>> notEqual = x => x.Planned != x.Actual;
        (await NamesAsync(store, notEqual)).ShouldBe(Oracle(notEqual));

        // A name sorts alphabetically, not by the enum's declared order.
        var range = await Should.ThrowAsync<BadLinqExpressionException>(
            () => NamesAsync(store, x => x.Planned < x.Actual));
        range.Message.ShouldContain("does not order");

        // A name is never equal to a number, which SQLite would quietly answer false for every row.
        var crossKind = await Should.ThrowAsync<BadLinqExpressionException>(
            () => NamesAsync(store, x => (int)x.Planned == x.B));
        crossKind.Message.ShouldContain("cross-type");
    }

    [Fact]
    public async Task a_duplicated_member_compares_through_its_column()
    {
        using var database = TemporaryDatabase.Create("member-to-member-duplicated");
        await using var store = StoreFor(database, EnumStorage.AsInteger, duplicateA: true);
        await SeedAsync(store);

        Expression<Func<Pair, bool>> lessThan = x => x.A < x.B;
        (await NamesAsync(store, lessThan)).ShouldBe(Oracle(lessThan));

        Expression<Func<Pair, bool>> arithmetic = x => x.A - 1 < x.B;
        (await NamesAsync(store, arithmetic)).ShouldBe(Oracle(arithmetic));

        await using var session = store.QuerySession();
        var sql = session.ToSql(session.Query<Pair>().Where(lessThan));
        sql.ShouldContain("coalesce(a < json_extract(data, '$.b'), 0)");
    }

    [Fact]
    public async Task a_constant_order_by_still_starts_a_new_ordering_group()
    {
        // Kept by the merge of fisher#588 and fisher#591 on this branch: a constant OrderBy is dropped,
        // and the ThenBy after it refines it, ahead of the older key, as LINQ does.
        await using var session = _store.QuerySession();

        var names = await session.Query<Pair>().OrderBy(x => x.B).OrderBy(x => 1).ThenBy(x => x.Name)
            .Select(x => x.Name).ToListAsync(Token);

        names.ShouldBe(Pairs.OrderBy(x => x.B).OrderBy(x => 1).ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Name));
    }
}
