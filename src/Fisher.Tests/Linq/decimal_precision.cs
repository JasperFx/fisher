using System.Globalization;
using Fisher.Linq;
using JasperFx;
using JasperFx.Events;

namespace Fisher.Tests.Linq;

public class PreciseAmount
{
    public Guid Id { get; set; }
    public string Label { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal? Fee { get; set; }
    public decimal[] Prices { get; set; } = [];
}

public class DuplicatedPreciseAmount
{
    public Guid Id { get; set; }
    public string Label { get; set; } = "";
    public decimal Amount { get; set; }
}

public class PreciseInvoice
{
    public Guid Id { get; set; }
    public decimal Total { get; set; }
}

public record PreciseAmountRecorded(decimal Amount);

/// <summary>
///     <c>Select</c>, <c>Where</c>, <c>OrderBy</c>, a keyset cursor, <c>Distinct</c>, <c>GroupBy</c> and an event
///     body query over a <c>decimal</c> member whose values differ only past a double's fifteenth significant
///     digit (fisher#609). LINQ to Objects over the same rows is the oracle throughout.
/// </summary>
/// <remarks>
///     <para>
///         <c>json_extract</c> reads a JSON number as REAL, so before fisher#609 every pair below was one
///         value: <c>Select</c> returned <c>0.1</c> for both, <c>==</c> matched both and <c>&gt;</c> neither.
///         The pairs cover a tiny magnitude, a large one with a fraction, decimal's maximum, negatives, and
///         <c>1.0m</c> against <c>1.00m</c> — equal as decimals, different as text.
///     </para>
/// </remarks>
public class decimal_precision : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("decimal-precision");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly decimal[] Values =
    [
        0.1000000000000000001m,
        0.1000000000000000002m,
        0.1m,
        12345678901234.5601m,
        12345678901234.5602m,
        -0.1000000000000000001m,
        -0.1000000000000000002m,
        -12345678901234.5601m,
        0.0000000000000000000000000001m,
        0.0000000000000000000000000002m,
        79228162514264337593543950335m,
        79228162514264337593543950334m,
        1.0m,
        1.00m,
        0m
    ];

    private static readonly List<PreciseAmount> Rows = Values.Select((value, i) => new PreciseAmount
    {
        Id = Guid.NewGuid(),
        Label = $"row-{i:D2}",
        Amount = value,
        Fee = i % 3 == 0 ? null : value,
        Prices = [value, -value]
    }).ToList();

    private static readonly List<DuplicatedPreciseAmount> DuplicatedRows = Values.Select((value, i) =>
        new DuplicatedPreciseAmount { Id = Guid.NewGuid(), Label = $"row-{i:D2}", Amount = value }).ToList();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<PreciseAmount>();
            options.Schema.For<PreciseInvoice>();
            options.Schema.For<DuplicatedPreciseAmount>().Duplicate(x => x.Amount);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        foreach (var row in Rows) session.Store(row);
        foreach (var row in DuplicatedRows) session.Store(row);

        foreach (var value in Values)
        {
            session.Events.StartStream(Guid.NewGuid(), new PreciseAmountRecorded(value));
        }

        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private IDocumentSession Session() => _store.LightweightSession();

    private static string[] Labels(IEnumerable<PreciseAmount> rows) => rows.Select(x => x.Label).Order().ToArray();

    private static string[] Labels(IEnumerable<DuplicatedPreciseAmount> rows)
        => rows.Select(x => x.Label).Order().ToArray();

    /// <summary>The decimal's digits and scale, which <c>==</c> on decimals does not compare.</summary>
    private static string Exactly(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    // ---- Select ----

    [Fact]
    public async Task select_reads_the_value_as_written()
    {
        await using var session = Session();

        var amounts = await session.Query<PreciseAmount>().OrderBy(x => x.Label).Select(x => x.Amount)
            .ToListAsync(Token);

        amounts.Select(Exactly).ShouldBe(Rows.OrderBy(x => x.Label).Select(x => Exactly(x.Amount)));
    }

    [Fact]
    public async Task select_of_a_nullable_decimal_and_a_shaped_projection_read_the_value_as_written()
    {
        await using var session = Session();

        var fees = await session.Query<PreciseAmount>().OrderBy(x => x.Label)
            .Select(x => new { x.Label, x.Fee, Halved = x.Amount / 2 })
            .ToListAsync(Token);

        var expected = Rows.OrderBy(x => x.Label).Select(x => new { x.Label, x.Fee, Halved = x.Amount / 2 })
            .ToList();

        fees.Select(x => x.Fee is null ? "null" : Exactly(x.Fee.Value))
            .ShouldBe(expected.Select(x => x.Fee is null ? "null" : Exactly(x.Fee.Value)));

        fees.Select(x => x.Halved).ShouldBe(expected.Select(x => x.Halved));
    }

    /// <summary>
    ///     Loading the document deserializes <c>data</c> through System.Text.Json, which was always exact.
    ///     Pinned, because the projection now agrees with it rather than with the REAL.
    /// </summary>
    [Fact]
    public async Task a_whole_document_load_and_a_nested_array_are_exact()
    {
        await using var session = Session();

        var loaded = await session.Query<PreciseAmount>().OrderBy(x => x.Label).ToListAsync(Token);
        loaded.Select(x => Exactly(x.Amount)).ShouldBe(Rows.OrderBy(x => x.Label).Select(x => Exactly(x.Amount)));

        var prices = await session.Query<PreciseAmount>().OrderBy(x => x.Label).Select(x => x.Prices)
            .ToListAsync(Token);
        prices.Select(x => string.Join(",", x.Select(Exactly)))
            .ShouldBe(Rows.OrderBy(x => x.Label).Select(x => string.Join(",", x.Prices.Select(Exactly))));
    }

    // ---- Where ----

    [Fact]
    public async Task equality_and_inequality_match_csharp()
    {
        await using var session = Session();

        foreach (var value in Values)
        {
            var equal = await session.Query<PreciseAmount>().Where(x => x.Amount == value).ToListAsync(Token);
            Labels(equal).ShouldBe(Labels(Rows.Where(x => x.Amount == value)), $"== {value}");

            var unequal = await session.Query<PreciseAmount>().Where(x => x.Amount != value).ToListAsync(Token);
            Labels(unequal).ShouldBe(Labels(Rows.Where(x => x.Amount != value)), $"!= {value}");

            var reversed = await session.Query<PreciseAmount>().Where(x => value == x.Amount).ToListAsync(Token);
            Labels(reversed).ShouldBe(Labels(Rows.Where(x => value == x.Amount)), $"{value} ==");
        }
    }

    [Fact]
    public async Task range_comparisons_match_csharp()
    {
        await using var session = Session();

        foreach (var value in Values)
        {
            var greater = await session.Query<PreciseAmount>().Where(x => x.Amount > value).ToListAsync(Token);
            Labels(greater).ShouldBe(Labels(Rows.Where(x => x.Amount > value)), $"> {value}");

            var atLeast = await session.Query<PreciseAmount>().Where(x => x.Amount >= value).ToListAsync(Token);
            Labels(atLeast).ShouldBe(Labels(Rows.Where(x => x.Amount >= value)), $">= {value}");

            var less = await session.Query<PreciseAmount>().Where(x => x.Amount < value).ToListAsync(Token);
            Labels(less).ShouldBe(Labels(Rows.Where(x => x.Amount < value)), $"< {value}");

            var atMost = await session.Query<PreciseAmount>().Where(x => value >= x.Amount).ToListAsync(Token);
            Labels(atMost).ShouldBe(Labels(Rows.Where(x => value >= x.Amount)), $"{value} >=");
        }
    }

    /// <summary>A null member matches no comparison but <c>== null</c>, as the REAL comparison did.</summary>
    [Fact]
    public async Task a_nullable_member_compares_exactly_and_null_still_means_null()
    {
        await using var session = Session();

        foreach (var value in Values)
        {
            var equal = await session.Query<PreciseAmount>().Where(x => x.Fee == value).ToListAsync(Token);
            Labels(equal).ShouldBe(Labels(Rows.Where(x => x.Fee == value)), $"== {value}");

            var greater = await session.Query<PreciseAmount>().Where(x => x.Fee > value).ToListAsync(Token);
            Labels(greater).ShouldBe(Labels(Rows.Where(x => x.Fee > value)), $"> {value}");
        }

        var missing = await session.Query<PreciseAmount>().Where(x => x.Fee == null).ToListAsync(Token);
        Labels(missing).ShouldBe(Labels(Rows.Where(x => x.Fee == null)));
    }

    /// <summary>
    ///     The exact path keeps fisher#608's answers for a null member: <c>!=</c> and every negation are true
    ///     where C# says so, and a null in a <c>Contains</c> set matches a null member.
    /// </summary>
    [Fact]
    public async Task a_nullable_member_answers_inequality_and_negation_as_csharp_does()
    {
        await using var session = Session();

        foreach (var value in Values)
        {
            var unequal = await session.Query<PreciseAmount>().Where(x => x.Fee != value).ToListAsync(Token);
            Labels(unequal).ShouldBe(Labels(Rows.Where(x => x.Fee != value)), $"!= {value}");

            var notEqual = await session.Query<PreciseAmount>().Where(x => !(x.Fee == value)).ToListAsync(Token);
            Labels(notEqual).ShouldBe(Labels(Rows.Where(x => !(x.Fee == value))), $"!(== {value})");

            var notUnequal = await session.Query<PreciseAmount>().Where(x => !(x.Fee != value)).ToListAsync(Token);
            Labels(notUnequal).ShouldBe(Labels(Rows.Where(x => !(x.Fee != value))), $"!(!= {value})");

            var notGreater = await session.Query<PreciseAmount>().Where(x => !(x.Fee > value)).ToListAsync(Token);
            Labels(notGreater).ShouldBe(Labels(Rows.Where(x => !(x.Fee > value))), $"!(> {value})");

            var notAtMost = await session.Query<PreciseAmount>().Where(x => !(x.Fee <= value)).ToListAsync(Token);
            Labels(notAtMost).ShouldBe(Labels(Rows.Where(x => !(x.Fee <= value))), $"!(<= {value})");
        }

        decimal?[] withNull = [0.1000000000000000002m, null, 12345678901234.5601m];
        decimal?[] withoutNull = [0.1000000000000000002m, 12345678901234.5601m];

        foreach (var set in new[] { withNull, withoutNull })
        {
            var contains = await session.Query<PreciseAmount>().Where(x => set.Contains(x.Fee)).ToListAsync(Token);
            Labels(contains).ShouldBe(Labels(Rows.Where(x => set.Contains(x.Fee))), $"Contains, {set.Length}");

            var notContains = await session.Query<PreciseAmount>().Where(x => !set.Contains(x.Fee)).ToListAsync(Token);
            Labels(notContains).ShouldBe(Labels(Rows.Where(x => !set.Contains(x.Fee))), $"!Contains, {set.Length}");
        }
    }

    [Fact]
    public async Task is_one_of_and_contains_match_csharp()
    {
        await using var session = Session();

        decimal[] set = [0.1000000000000000002m, 12345678901234.5601m, -0.1000000000000000001m, 1.000m, 42m];

        var oneOf = await session.Query<PreciseAmount>().Where(x => x.Amount.IsOneOf(set)).ToListAsync(Token);
        Labels(oneOf).ShouldBe(Labels(Rows.Where(x => set.Contains(x.Amount))));

        var contains = await session.Query<PreciseAmount>().Where(x => set.Contains(x.Amount)).ToListAsync(Token);
        Labels(contains).ShouldBe(Labels(Rows.Where(x => set.Contains(x.Amount))));
    }

    /// <summary>Past 1,000 values the set is one JSON array of decimal strings (fisher#372's shape).</summary>
    [Fact]
    public async Task a_large_set_matches_csharp()
    {
        await using var session = Session();

        var set = Enumerable.Range(1, 1_500).Select(i => 0.1000000000000000002m + i).ToList();
        set.Add(0.1000000000000000002m);
        set.Add(-12345678901234.5601m);

        var found = await session.Query<PreciseAmount>().Where(x => x.Amount.IsOneOf(set)).ToListAsync(Token);
        Labels(found).ShouldBe(Labels(Rows.Where(x => set.Contains(x.Amount))));
    }

    [Fact]
    public async Task a_decimal_collection_element_compares_exactly()
    {
        await using var session = Session();

        var contains = await session.Query<PreciseAmount>()
            .Where(x => x.Prices.Contains(0.1000000000000000002m)).ToListAsync(Token);
        Labels(contains).ShouldBe(Labels(Rows.Where(x => x.Prices.Contains(0.1000000000000000002m))));

        var negative = await session.Query<PreciseAmount>()
            .Where(x => x.Prices.Contains(-0.1000000000000000001m)).ToListAsync(Token);
        Labels(negative).ShouldBe(Labels(Rows.Where(x => x.Prices.Contains(-0.1000000000000000001m))));
    }

    // ---- OrderBy ----

    [Fact]
    public async Task order_by_and_then_by_match_csharp()
    {
        await using var session = Session();

        var ascending = await session.Query<PreciseAmount>().OrderBy(x => x.Amount).ThenBy(x => x.Label)
            .ToListAsync(Token);
        ascending.Select(x => x.Label)
            .ShouldBe(Rows.OrderBy(x => x.Amount).ThenBy(x => x.Label, StringComparer.Ordinal).Select(x => x.Label));

        var descending = await session.Query<PreciseAmount>().OrderByDescending(x => x.Fee)
            .ThenByDescending(x => x.Label).ToListAsync(Token);
        descending.Select(x => x.Label)
            .ShouldBe(Rows.OrderByDescending(x => x.Fee).ThenByDescending(x => x.Label, StringComparer.Ordinal)
                .Select(x => x.Label));

        // Every label is the same length, so the decimal decides as a secondary key.
        var secondary = await session.Query<PreciseAmount>().OrderBy(x => x.Label.Length).ThenBy(x => x.Amount)
            .ThenBy(x => x.Label).Select(x => x.Label).ToListAsync(Token);
        secondary.ShouldBe(Rows.OrderBy(x => x.Label.Length).ThenBy(x => x.Amount)
            .ThenBy(x => x.Label, StringComparer.Ordinal).Select(x => x.Label));
    }

    [Fact]
    public async Task last_over_a_page_ordered_by_a_decimal_matches_csharp()
    {
        await using var session = Session();

        var last = await session.Query<PreciseAmount>().OrderBy(x => x.Amount).ThenBy(x => x.Label).Take(3)
            .LastAsync(Token);

        last.Label.ShouldBe(Rows.OrderBy(x => x.Amount).ThenBy(x => x.Label, StringComparer.Ordinal).Take(3)
            .Last().Label);
    }

    /// <summary>
    ///     The cursor carries the decimal exactly and the seek compares it exactly. Seeking against the REAL
    ///     repeated or skipped the rows a double cannot tell apart, which a page size of one exposes.
    /// </summary>
    [Fact]
    public async Task a_cursor_walk_over_a_decimal_visits_every_row_once_in_order()
    {
        await using var session = Session();

        foreach (var descending in new[] { false, true })
        {
            var seen = new List<string>();
            string? cursor = null;

            do
            {
                var query = descending
                    ? session.Query<PreciseAmount>().OrderByDescending(x => x.Amount).ThenBy(x => x.Id)
                    : session.Query<PreciseAmount>().OrderBy(x => x.Amount).ThenBy(x => x.Id);

                var page = await query.ToCursorPageAsync(1, cursor, Token);
                seen.AddRange(page.Items.Select(x => x.Label));
                cursor = page.NextCursor;

                // A seek that cannot tell two rows apart can hand back the same page forever.
                seen.Count.ShouldBeLessThanOrEqualTo(Rows.Count + 1, "the walk revisited rows");
            } while (cursor is not null);

            // The id column holds the Guid's lowercase text, so ties order by that, ordinally.
            var expected = descending
                ? Rows.OrderByDescending(x => x.Amount).ThenBy(x => x.Id.ToString(), StringComparer.Ordinal)
                : Rows.OrderBy(x => x.Amount).ThenBy(x => x.Id.ToString(), StringComparer.Ordinal);

            seen.ShouldBe(expected.Select(x => x.Label), $"descending: {descending}");
        }
    }

    /// <summary>
    ///     <c>fi_decimal_key</c>'s bytes sort as the numbers do and read back as the same value — the property
    ///     every exact ordering, grouping and cursor seek rests on — over random decimals of every scale and
    ///     sign, clustered so many differ only in their last digits.
    /// </summary>
    [Fact]
    public void the_sort_key_orders_as_the_decimal_and_round_trips()
    {
        var random = new Random(609);
        var values = new List<decimal>(Values);

        for (var i = 0; i < 2_000; i++)
        {
            var scale = (byte)random.Next(0, 29);
            var start = new decimal(random.Next(), random.Next(), random.Next(0, 1 << random.Next(1, 31)),
                random.Next(2) == 0, scale);

            values.Add(start);
            values.Add(-start);
            values.Add(start / 10m);

            try
            {
                values.Add(start + new decimal(random.Next(1, 1000), 0, 0, false, scale));
            }
            catch (OverflowException)
            {
                // Next to decimal.MaxValue; the cluster still has its other members.
            }
        }

        var byKey = values.OrderBy(Fisher.Linq.DecimalAggregates.SortKey, ByteOrder.Instance).ToList();

        for (var i = 1; i < byKey.Count; i++)
        {
            byKey[i - 1].ShouldBeLessThanOrEqualTo(byKey[i]);

            var sameKey = Fisher.Linq.DecimalAggregates.SortKey(byKey[i - 1])
                .AsSpan().SequenceEqual(Fisher.Linq.DecimalAggregates.SortKey(byKey[i]));
            sameKey.ShouldBe(byKey[i - 1] == byKey[i], $"{byKey[i - 1]} and {byKey[i]}");
        }

        foreach (var value in values)
        {
            var key = Fisher.Linq.DecimalAggregates.SortKey(value);
            Fisher.Linq.DecimalAggregates.FromSortKey(key).ShouldBe(value);

            // The function reads the JSON text, digit by digit; it must key it as the decimal would.
            var text = value.ToString(CultureInfo.InvariantCulture);
            Fisher.Linq.DecimalAggregates.SortKeyOf(text).ShouldBe(key, text);
            Fisher.Linq.DecimalAggregates.SortKeyOf($"\"{text}\"").ShouldBe(key, text);

            // The same number with an exponent, which only the decimal route reads.
            var point = text.IndexOf('.');
            var scientific = point < 0 ? text + "E0" : $"{text.Remove(point, 1)}E-{text.Length - point - 1}";
            Fisher.Linq.DecimalAggregates.SortKeyOf(scientific).ShouldBe(key, scientific);
        }

        Fisher.Linq.DecimalAggregates.SortKeyOf("null").ShouldBeNull();
    }

    /// <summary>SQLite's BLOB order: <c>memcmp</c>, then the shorter first.</summary>
    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static ByteOrder Instance { get; } = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }

    // ---- Distinct, GroupBy, DistinctBy ----

    [Fact]
    public async Task distinct_matches_csharp()
    {
        await using var session = Session();

        var distinct = await session.Query<PreciseAmount>().Select(x => x.Amount).Distinct().OrderBy(x => x)
            .ToListAsync(Token);

        // 1.0m and 1.00m are one value, as Enumerable.Distinct says; which spelling survives is unspecified.
        distinct.ShouldBe(Values.Distinct().Order().ToList());

        (await session.Query<PreciseAmount>().Select(x => x.Amount).Distinct().CountAsync(Token))
            .ShouldBe(Values.Distinct().Count());

        var distinctBy = await session.Query<PreciseAmount>().DistinctBy(x => x.Amount).CountAsync(Token);
        distinctBy.ShouldBe(Rows.DistinctBy(x => x.Amount).Count());
    }

    [Fact]
    public async Task group_by_a_decimal_key_matches_csharp()
    {
        await using var session = Session();

        var groups = await session.Query<PreciseAmount>().GroupBy(x => x.Amount)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(Token);

        var expected = Rows.GroupBy(x => x.Amount).Select(g => new { g.Key, Count = g.Count() }).ToList();

        groups.OrderBy(x => x.Key).Select(x => (x.Key, x.Count))
            .ShouldBe(expected.OrderBy(x => x.Key).Select(x => (x.Key, x.Count)));

        var having = await session.Query<PreciseAmount>().GroupBy(x => x.Amount)
            .Where(g => g.Key > 0.1000000000000000001m)
            .OrderBy(g => g.Key)
            .Select(g => g.Key)
            .ToListAsync(Token);

        having.ShouldBe(Rows.GroupBy(x => x.Amount).Where(g => g.Key > 0.1000000000000000001m)
            .OrderBy(g => g.Key).Select(g => g.Key));
    }

    [Fact]
    public async Task a_join_on_a_decimal_key_matches_csharp()
    {
        await using (var writer = Session())
        {
            writer.Store(new PreciseInvoice { Id = Guid.NewGuid(), Total = 0.1000000000000000002m });
            await writer.SaveChangesAsync(Token);
        }

        await using var session = Session();

        var joined = await session.Query<PreciseAmount>()
            .Join(session.Query<PreciseInvoice>(), x => x.Amount, i => i.Total, (x, i) => x)
            .ToListAsync(Token);

        Labels(joined).ShouldBe(Labels(Rows.Where(x => x.Amount == 0.1000000000000000002m)));
    }

    // ---- A duplicated field, and its index ----

    [Fact]
    public async Task a_duplicated_decimal_compares_and_orders_exactly()
    {
        await using var session = Session();

        foreach (var value in Values)
        {
            var equal = await session.Query<DuplicatedPreciseAmount>().Where(x => x.Amount == value)
                .ToListAsync(Token);
            Labels(equal).ShouldBe(Labels(DuplicatedRows.Where(x => x.Amount == value)), $"== {value}");

            var greater = await session.Query<DuplicatedPreciseAmount>().Where(x => x.Amount > value)
                .ToListAsync(Token);
            Labels(greater).ShouldBe(Labels(DuplicatedRows.Where(x => x.Amount > value)), $"> {value}");

            var atMost = await session.Query<DuplicatedPreciseAmount>().Where(x => x.Amount <= value)
                .ToListAsync(Token);
            Labels(atMost).ShouldBe(Labels(DuplicatedRows.Where(x => x.Amount <= value)), $"<= {value}");
        }

        var ordered = await session.Query<DuplicatedPreciseAmount>().OrderBy(x => x.Amount).ThenBy(x => x.Label)
            .Select(x => x.Label).ToListAsync(Token);
        ordered.ShouldBe(DuplicatedRows.OrderBy(x => x.Amount).ThenBy(x => x.Label, StringComparer.Ordinal)
            .Select(x => x.Label));
    }

    /// <summary>
    ///     The REAL band keeps the duplicated column's index in play: the exact text is checked only inside
    ///     the range the index finds.
    /// </summary>
    [Fact]
    public async Task an_exact_comparison_still_searches_the_duplicated_index()
    {
        await using var session = Session();

        var equal = await session.Query<DuplicatedPreciseAmount>()
            .Where(x => x.Amount == 0.1000000000000000002m).ExplainAsync(Token);
        equal.UsesIndex.ShouldBeTrue(equal.ToString());

        var range = await session.Query<DuplicatedPreciseAmount>()
            .Where(x => x.Amount > 12345678901234.5601m).ExplainAsync(Token);
        range.UsesIndex.ShouldBeTrue(range.ToString());
    }

    // ---- Event bodies ----

    [Fact]
    public async Task an_event_body_query_compares_exactly()
    {
        await using var session = Session();

        var equal = await session.Events.QueryEventDataAsync<PreciseAmountRecorded>(
            x => x.Amount == 0.1000000000000000002m, Token);
        equal.Select(x => Exactly(x.Amount)).ShouldBe([Exactly(0.1000000000000000002m)]);

        var greater = await session.Events.QueryEventDataAsync<PreciseAmountRecorded>(
            x => x.Amount > 0.1000000000000000001m && x.Amount < 1m, Token);
        greater.Select(x => x.Amount).Order()
            .ShouldBe(Values.Where(x => x > 0.1000000000000000001m && x < 1m).Order());

        var reader = ((IEventStore)_store).OpenReadOnlyEventStore();
        var ordered = await JasperFx.Events.Documents.DocumentQueryableExtensions.ToListAsync(
            reader.QueryEventData<PreciseAmountRecorded>().OrderBy(x => x.Amount), Token);
        ordered.Select(x => x.Amount).ShouldBe(Values.Order());
    }
}
