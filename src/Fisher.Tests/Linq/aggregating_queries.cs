using Fisher.Linq;
using JasperFx;
using Weasel.Core;

namespace Fisher.Tests.Linq;

/// <summary>
///     Scalar aggregates and <c>Last</c> — fisher#22.
/// </summary>
/// <remarks>
///     Three things carry the weight here, and none of them is the happy path. The empty-result case,
///     because SQLite's <c>sum</c> returns NULL where <c>count</c> returns 0 and an unguarded cast
///     would only fail there. The interaction with paging, because an aggregate over
///     <c>Take(n)</c> must apply to the page. And the two guards, because SQLite answers a
///     <c>sum</c> over text with 0 rather than an error.
/// </remarks>
public class aggregating_queries : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("aggregates");
    private DocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = StoreFor(EnumStorage.AsInteger);
        await Seed(_store);
    }

    private DocumentStore StoreFor(EnumStorage enumStorage)
        => DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.ConfigureSerialization(enumStorage: enumStorage);
            options.Schema.For<Catch>();
        });

    private static async Task Seed(DocumentStore store)
    {
        await store.ApplyAllConfiguredChangesToDatabaseAsync(TestContext.Current.CancellationToken);

        await using var session = store.LightweightSession();
        session.Store(new Catch { Species = "Trout", Weight = 3, Fee = 12.50m, Rating = Rating.Poor, LandedAt = At(1) });
        session.Store(new Catch { Species = "Pike", Weight = 11, Fee = 4.25m, Rating = Rating.Good, LandedAt = At(2) });
        session.Store(new Catch { Species = "Bream", Weight = 7, Fee = 30.00m, Rating = Rating.Excellent, LandedAt = At(3) });
        session.Store(new Catch { Species = "Chub", Weight = 2, Fee = 1.25m, Rating = Rating.Good, LandedAt = At(4) });
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

    // ---- the aggregates ----

    [Fact]
    public async Task sum_over_the_three_numeric_shapes()
    {
        await using var session = Session();

        (await session.Query<Catch>().SumAsync(x => x.Weight, Token)).ShouldBe(23);
        (await session.Query<Catch>().SumAsync(x => x.Fee, Token)).ShouldBe(48.00m);
        (await session.Query<Catch>().SumAsync(x => x.Depth, Token)).ShouldBe(10.0);
    }

    [Fact]
    public async Task min_and_max()
    {
        await using var session = Session();

        (await session.Query<Catch>().MinAsync(x => x.Weight, Token)).ShouldBe(2);
        (await session.Query<Catch>().MaxAsync(x => x.Weight, Token)).ShouldBe(11);
        (await session.Query<Catch>().MinAsync(x => x.Fee, Token)).ShouldBe(1.25m);
    }

    /// <summary>
    ///     <c>min</c>/<c>max</c> are open over the result type because ordering, not arithmetic, is what
    ///     they need — so a string and a timestamp are both real answers.
    /// </summary>
    [Fact]
    public async Task min_and_max_over_members_that_are_not_numbers()
    {
        await using var session = Session();

        (await session.Query<Catch>().MinAsync(x => x.Species, Token)).ShouldBe("Bream");
        (await session.Query<Catch>().MaxAsync(x => x.Species, Token)).ShouldBe("Trout");

        // Through TimestampMember's strftime normalisation, which is what makes the comparison an
        // instant comparison rather than a comparison of whatever System.Text.Json wrote.
        (await session.Query<Catch>().MaxAsync(x => x.LandedAt, Token)).ShouldBe(At(4));
        (await session.Query<Catch>().MinAsync(x => x.LandedAt, Token)).ShouldBe(At(1));

        // An int-stored enum comes back as INTEGER, and a Guid as TEXT. Neither is IConvertible from
        // what SQLite hands back, so both need the explicit conversions Coerce does.
        (await session.Query<Catch>().MaxAsync(x => x.Rating, Token)).ShouldBe(Rating.Excellent);

        // A Guid minimum is by the *text* SQLite holds, which is not .NET's Guid ordering — that
        // compares the first group as a signed int, so the two disagree whenever the set straddles
        // 0x80000000. Comparing against Enumerable.Min here would be a genuine intermittent, so the
        // expectation is built the way SQLite actually computes it.
        var ids = (await session.Query<Catch>().ToListAsync(Token)).Select(x => x.Id.ToString()).ToList();
        (await session.Query<Catch>().MinAsync(x => x.Id, Token))
            .ShouldBe(Guid.Parse(ids.OrderBy(x => x, StringComparer.Ordinal).First()));
    }

    [Fact]
    public async Task average()
    {
        await using var session = Session();

        (await session.Query<Catch>().AverageAsync(x => x.Weight, Token)).ShouldBe(5.75);
        (await session.Query<Catch>().AverageAsync(x => x.Fee, Token)).ShouldBe(12.0);
    }

    // ---- the empty case, which is where an unguarded cast fails ----

    /// <summary>
    ///     <c>sum</c>, <c>min</c>, <c>max</c> and <c>avg</c> all return NULL over no rows, unlike
    ///     <c>count</c>. Every one of these throws an <see cref="InvalidCastException" /> from inside
    ///     the provider without the null coercion, and only on an empty result — so it would ship.
    /// </summary>
    [Fact]
    public async Task aggregates_over_no_rows_are_the_default()
    {
        await using var session = Session();
        var none = session.Query<Catch>().Where(x => x.Species == "Barracuda");

        (await none.SumAsync(x => x.Weight, Token)).ShouldBe(0);
        (await none.SumAsync(x => x.Fee, Token)).ShouldBe(0m);
        (await none.AverageAsync(x => x.Weight, Token)).ShouldBe(0.0);
        (await none.MinAsync(x => x.Weight, Token)).ShouldBe(0);
        (await none.MaxAsync(x => x.Species, Token)).ShouldBeNull();
    }

    // ---- composition ----

    [Fact]
    public async Task an_aggregate_respects_a_where()
    {
        await using var session = Session();

        (await session.Query<Catch>().Where(x => x.Weight > 2).SumAsync(x => x.Weight, Token)).ShouldBe(21);
    }

    /// <summary>
    ///     The interesting one: <c>Take</c> must bound what is aggregated, so the paged query becomes a
    ///     subquery and the aggregate wraps it. Without that, this reports the whole table — the same
    ///     trap <c>CountAsync</c> already documents.
    /// </summary>
    [Fact]
    public async Task an_aggregate_over_a_page_applies_to_the_page()
    {
        await using var session = Session();

        // Ordered by weight ascending: Chub 2, Trout 3, Bream 7, Pike 11.
        (await session.Query<Catch>().OrderBy(x => x.Weight).Take(2)
            .SumAsync(x => x.Weight, Token)).ShouldBe(5);

        (await session.Query<Catch>().OrderBy(x => x.Weight).Skip(2)
            .SumAsync(x => x.Weight, Token)).ShouldBe(18);

        (await session.Query<Catch>().OrderBy(x => x.Weight).Take(2)
            .MaxAsync(x => x.Weight, Token)).ShouldBe(3);
    }

    // ---- Last ----

    [Fact]
    public async Task last_of_an_ordered_query()
    {
        await using var session = Session();

        (await session.Query<Catch>().OrderBy(x => x.Weight).LastAsync(Token))!.Species.ShouldBe("Pike");
        (await session.Query<Catch>().OrderByDescending(x => x.Weight).LastAsync(Token))!.Species.ShouldBe("Chub");
    }

    /// <summary>
    ///     The reverse has to apply <em>outside</em> the page, not inside it. Ordered by weight the
    ///     first three are Chub 2, Trout 3, Bream 7 — so the last of that page is Bream. Inverting in
    ///     place would answer Chub, which is the last of the reversed whole table.
    /// </summary>
    [Fact]
    public async Task last_of_a_paged_query_is_the_last_of_the_page()
    {
        await using var session = Session();

        (await session.Query<Catch>().OrderBy(x => x.Weight).Take(3).LastAsync(Token))!
            .Species.ShouldBe("Bream");
    }

    [Fact]
    public async Task last_or_default_over_no_rows()
    {
        await using var session = Session();

        (await session.Query<Catch>().Where(x => x.Species == "Barracuda")
            .OrderBy(x => x.Weight).LastOrDefaultAsync(Token)).ShouldBeNull();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            session.Query<Catch>().Where(x => x.Species == "Barracuda")
                .OrderBy(x => x.Weight).LastAsync(Token));
    }

    [Fact]
    public async Task last_without_an_ordering_is_refused_by_name()
    {
        await using var session = Session();

        var exception = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Catch>().LastAsync(Token));

        exception.Message.ShouldContain("OrderBy");
    }

    // ---- predicate overloads ----

    [Fact]
    public async Task the_predicate_overloads()
    {
        await using var session = Session();

        (await session.Query<Catch>().CountAsync(x => x.Weight > 2, Token)).ShouldBe(3);
        (await session.Query<Catch>().LongCountAsync(x => x.Weight > 2, Token)).ShouldBe(3L);
        (await session.Query<Catch>().AnyAsync(x => x.Weight > 100, Token)).ShouldBeFalse();
        (await session.Query<Catch>().FirstOrDefaultAsync(x => x.Species == "Pike", Token))!
            .Weight.ShouldBe(11);
        (await session.Query<Catch>().SingleAsync(x => x.Species == "Pike", Token))!.Weight.ShouldBe(11);
    }

    // ---- the guards ----

    /// <summary>
    ///     SQLite's <c>sum()</c> over text returns 0 rather than failing, so summing a string-stored
    ///     enum would report a plausible total for a column that has none. Refused instead.
    /// </summary>
    [Fact]
    public async Task summing_a_non_numeric_member_is_refused_by_name()
    {
        await using var session = Session();

        var exception = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Catch>().SumAsync(x => (int)x.Rating, Token));

        exception.Message.ShouldContain("Rating");
        exception.Message.ShouldContain("not a number");
    }

    /// <summary>
    ///     A string-stored enum's minimum is alphabetical rather than by declared order, so
    ///     <c>min</c>/<c>max</c> refuse it for the same reason <c>OrderBy</c> does. Under
    ///     <c>AsInteger</c>, which is Fisher's default, it is meaningful and allowed.
    /// </summary>
    [Fact]
    public async Task min_over_a_string_stored_enum_is_refused_by_name()
    {
        await using var integerStore = StoreFor(EnumStorage.AsInteger);
        await using (var session = integerStore.LightweightSession())
        {
            (await session.Query<Catch>().MinAsync(x => x.Rating, Token)).ShouldBe(Rating.Poor);
        }

        await using var stringStore = StoreFor(EnumStorage.AsString);
        await using var stringSession = stringStore.LightweightSession();

        var exception = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            stringStore.LightweightSession().Query<Catch>().MinAsync(x => x.Rating, Token));

        exception.Message.ShouldContain("EnumStorage");
    }

    [Fact]
    public async Task an_aggregate_over_something_that_is_not_a_member_is_refused()
    {
        await using var session = Session();

        await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Catch>().SumAsync(x => x.Weight * 2, Token));
    }

    /// <summary>
    ///     An aggregate after a <c>Select</c> is refused as a LINQ error, naming the operator.
    /// </summary>
    /// <remarks>
    ///     It used to fail as an <see cref="InvalidOperationException" /> about identity members, because
    ///     the aggregate asked the schema for a mapping of the query's <em>element</em> type — which
    ///     after a projection is whatever the projection produced, and never a document. The aggregates
    ///     build from the chain's source type now, which is what made the join case answerable at all
    ///     (fisher#54) and this case reportable.
    /// </remarks>
    [Fact]
    public async Task an_aggregate_after_a_select_is_refused_by_name()
    {
        await using var session = Session();

        var exception = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Catch>().Select(x => new { x.Weight }).SumAsync(x => x.Weight, Token));

        exception.Message.ShouldContain("SumAsync");
        exception.Message.ShouldContain("Select");
    }

    public enum Rating
    {
        Poor,
        Good,
        Excellent
    }

    public class Catch
    {
        public Guid Id { get; set; }
        public string Species { get; set; } = "";
        public int Weight { get; set; }
        public decimal Fee { get; set; }
        public double Depth { get; set; } = 2.5;
        public Rating Rating { get; set; }
        public DateTimeOffset LandedAt { get; set; }
    }
}
