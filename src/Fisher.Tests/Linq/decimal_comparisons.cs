using Fisher.Linq;
using JasperFx;
using Shouldly;

namespace Fisher.Tests.Linq;

/// <summary>
///     Comparisons, set membership and ordering against a <see cref="decimal" /> member (fisher#304).
/// </summary>
/// <remarks>
///     <para>
///         <b>The failure this pins is silent and asymmetric, which is what makes it worth a file of its
///         own.</b> Microsoft.Data.Sqlite binds a raw <see cref="decimal" /> as TEXT, and
///         <c>json_extract</c> yields a REAL for a JSON number. SQLite's cross-type ordering puts every
///         numeric value below every TEXT value, so <c>&gt;</c> and <c>==</c> matched nothing while
///         <c>&lt;</c> matched everything — no exception, and a plausible-looking result set either way.
///         Money is the overwhelmingly common <see cref="decimal" />, so
///         <c>Where(x =&gt; x.Total &gt; limit)</c> quietly returning nothing is the dangerous direction.
///     </para>
///     <para>
///         <b>Every assertion here is two-sided</b> — the matching rows AND the count of the ones that
///         must not match. A one-sided test over the "wrong" operator passes against the broken build:
///         <c>&lt;</c> returned every row, so asserting only that the cheap invoice came back was green
///         on a store that was also returning the two expensive ones.
///     </para>
///     <para>
///         The raw-SQL twin of this already existed —
///         <c>queued_sql_commands.a_decimal_parameter_matches_a_json_extracted_number</c> — and so did
///         the modulo one, <c>ModuloFilter</c> having normalised its operands since fisher#161. The
///         ordinary comparison path beside them never learned, which is the shape rather than the
///         exception.
///     </para>
/// </remarks>
public class decimal_comparisons : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("decimals");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Invoice>();
            options.Schema.For<DuplicatedInvoice>().Duplicate(x => x.Total);
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using var session = _store.LightweightSession();
        session.Store(
            new Invoice { Number = "cheap", Total = 50m, Quantity = 50, Discount = 5m },
            new Invoice { Number = "middle", Total = 150m, Quantity = 150, Discount = null },
            new Invoice { Number = "dear", Total = 250.75m, Quantity = 250, Discount = 25.5m });
        session.Store(
            new DuplicatedInvoice { Number = "cheap", Total = 50m },
            new DuplicatedInvoice { Number = "dear", Total = 250.75m });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private async Task<string[]> NumbersAsync(IQueryable<Invoice> query)
    {
        var results = await query.ToListAsync(TestContext.Current.CancellationToken);
        return results.Select(x => x.Number).OrderBy(x => x).ToArray();
    }

    [Fact]
    public async Task greater_than_a_decimal_matches_the_rows_above_it()
    {
        await using var session = _store.QuerySession();

        var matched = await NumbersAsync(session.Query<Invoice>().Where(x => x.Total > 100m));

        matched.ShouldBe(["dear", "middle"]);
    }

    [Fact]
    public async Task less_than_a_decimal_matches_the_rows_below_it()
    {
        await using var session = _store.QuerySession();

        var matched = await NumbersAsync(session.Query<Invoice>().Where(x => x.Total < 100m));

        matched.ShouldBe(["cheap"]);
    }

    [Fact]
    public async Task equality_against_a_decimal_matches_exactly_one_row()
    {
        await using var session = _store.QuerySession();

        var matched = await NumbersAsync(session.Query<Invoice>().Where(x => x.Total == 150m));

        matched.ShouldBe(["middle"]);
    }

    [Fact]
    public async Task a_fractional_decimal_compares_as_a_number()
    {
        await using var session = _store.QuerySession();

        var matched = await NumbersAsync(session.Query<Invoice>().Where(x => x.Total == 250.75m));

        matched.ShouldBe(["dear"]);
    }

    [Fact]
    public async Task inequality_against_a_decimal_matches_the_rest()
    {
        await using var session = _store.QuerySession();

        var matched = await NumbersAsync(session.Query<Invoice>().Where(x => x.Total != 150m));

        matched.ShouldBe(["cheap", "dear"]);
    }

    [Fact]
    public async Task the_inclusive_bounds_include_the_boundary_row()
    {
        await using var session = _store.QuerySession();

        (await NumbersAsync(session.Query<Invoice>().Where(x => x.Total >= 150m)))
            .ShouldBe(["dear", "middle"]);
        (await NumbersAsync(session.Query<Invoice>().Where(x => x.Total <= 150m)))
            .ShouldBe(["cheap", "middle"]);
    }

    /// <summary>
    ///     The int control. It passed throughout and is kept so a future regression says whether the
    ///     comparison path broke or only the decimal conversion did.
    /// </summary>
    [Fact]
    public async Task an_integer_member_was_never_affected()
    {
        await using var session = _store.QuerySession();

        var matched = await NumbersAsync(session.Query<Invoice>().Where(x => x.Quantity > 100));

        matched.ShouldBe(["dear", "middle"]);
    }

    [Fact]
    public async Task a_nullable_decimal_compares_as_a_number_and_still_answers_null()
    {
        await using var session = _store.QuerySession();

        (await NumbersAsync(session.Query<Invoice>().Where(x => x.Discount > 10m)))
            .ShouldBe(["dear"]);
        (await NumbersAsync(session.Query<Invoice>().Where(x => x.Discount == null)))
            .ShouldBe(["middle"]);
    }

    /// <summary>
    ///     <c>IsOneOf</c> and <c>Contains</c> both render <c>in (…)</c> through <c>WhereInFilter</c>,
    ///     reached from opposite directions — there the collection is the receiver, here the member is.
    ///     Both bind their values, so both were wrong in the same way and neither was covered.
    /// </summary>
    [Fact]
    public async Task set_membership_over_decimals_matches()
    {
        await using var session = _store.QuerySession();

        (await NumbersAsync(session.Query<Invoice>().Where(x => x.Total.IsOneOf(50m, 250.75m))))
            .ShouldBe(["cheap", "dear"]);

        var wanted = new[] { 150m };
        (await NumbersAsync(session.Query<Invoice>().Where(x => wanted.Contains(x.Total))))
            .ShouldBe(["middle"]);
    }

    /// <summary>
    ///     A duplicated decimal is a <c>VIRTUAL</c> generated column declared REAL, so the column's own
    ///     affinity coerces a TEXT-bound parameter and this shape was <b>already right</b> before
    ///     fisher#304. Pinned deliberately: it is the reason the bug could survive a store that had
    ///     duplicated fields in play, and a fix that regressed it would otherwise be invisible.
    /// </summary>
    [Fact]
    public async Task a_duplicated_decimal_column_compares_as_a_number()
    {
        await using var session = _store.QuerySession();

        var matched = await session.Query<DuplicatedInvoice>()
            .Where(x => x.Total > 100m)
            .ToListAsync(TestContext.Current.CancellationToken);

        matched.Select(x => x.Number).ShouldBe(["dear"]);
    }

    /// <summary>
    ///     Ordering binds no value — the locator is compared against itself — so this was correct
    ///     throughout. fisher#304 asked for it to be checked rather than assumed.
    /// </summary>
    [Fact]
    public async Task ordering_by_a_decimal_member_orders_numerically()
    {
        await using var session = _store.QuerySession();

        var ordered = await session.Query<Invoice>()
            .OrderByDescending(x => x.Total)
            .ToListAsync(TestContext.Current.CancellationToken);

        ordered.Select(x => x.Number).ShouldBe(["dear", "middle", "cheap"]);
    }

    /// <summary>
    ///     The arithmetic path composes its own locator and binds the value side directly rather than
    ///     through the member, so it is a second producer that had to be covered — and
    ///     <c>WhereClauseParser.IsNumeric</c> admits <see cref="decimal" /> explicitly.
    /// </summary>
    [Fact]
    public async Task arithmetic_on_a_decimal_member_compares_as_a_number()
    {
        await using var session = _store.QuerySession();

        var matched = await NumbersAsync(session.Query<Invoice>().Where(x => x.Total + 60m > 200m));

        matched.ShouldBe(["dear", "middle"]);
    }
}

public class Invoice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public decimal? Discount { get; set; }
    public int Quantity { get; set; }
}

public class DuplicatedInvoice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get; set; } = string.Empty;
    public decimal Total { get; set; }
}
