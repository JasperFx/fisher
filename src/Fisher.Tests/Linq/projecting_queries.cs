using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

/// <summary>
///     <c>Select</c> projections, <c>Distinct</c> and <c>DistinctBy</c> — fisher#23.
/// </summary>
/// <remarks>
///     The point of the feature is that a projection reads columns rather than documents, so the
///     tests that matter are the ones about <em>which</em> columns: that only the members reached in
///     the lambda become columns, that a member reached twice becomes one, and that the values come
///     back through the same conversions a document read uses.
/// </remarks>
public class projecting_queries : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("projections");
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
        session.Store(new Catch { Species = "Trout", Angler = "Frodo", Weight = 3, Fee = 12.50m, Rating = Rating.Poor, LandedAt = At(1), Water = new Water { Name = "Brandywine" } });
        session.Store(new Catch { Species = "Pike", Angler = "Sam", Weight = 11, Fee = 4.25m, Rating = Rating.Good, LandedAt = At(2), Water = new Water { Name = "Brandywine" } });
        session.Store(new Catch { Species = "Trout", Angler = "Merry", Weight = 7, Fee = 30.00m, Rating = Rating.Excellent, LandedAt = At(3), Water = new Water { Name = "Withywindle" } });
        session.Store(new Catch { Species = "Chub", Angler = "Pippin", Weight = 2, Fee = 1.25m, Rating = Rating.Good, LandedAt = At(4), Water = new Water { Name = "Withywindle" } });
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

    // ---- scalar projection ----

    [Fact]
    public async Task a_scalar_projection()
    {
        await using var session = Session();

        (await session.Query<Catch>().OrderBy(x => x.Angler).Select(x => x.Angler).ToListAsync(Token))
            .ShouldBe(["Frodo", "Merry", "Pippin", "Sam"]);
    }

    /// <summary>
    ///     A projected member goes through exactly the conversions a document read would — which for
    ///     three of these is not <c>Convert.ChangeType</c>, for the reasons fisher#22 records.
    /// </summary>
    [Fact]
    public async Task projected_values_use_the_same_conversions_a_document_read_does()
    {
        await using var session = Session();

        (await session.Query<Catch>().Where(x => x.Angler == "Frodo")
            .Select(x => x.LandedAt).ToListAsync(Token)).ShouldBe([At(1)]);

        (await session.Query<Catch>().Where(x => x.Angler == "Frodo")
            .Select(x => x.Rating).ToListAsync(Token)).ShouldBe([Rating.Poor]);

        (await session.Query<Catch>().Where(x => x.Angler == "Frodo")
            .Select(x => x.Fee).ToListAsync(Token)).ShouldBe([12.50m]);

        (await session.Query<Catch>().Where(x => x.Angler == "Frodo")
            .Select(x => x.Id).ToListAsync(Token)).ShouldHaveSingleItem().ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task a_nested_member_projects()
    {
        await using var session = Session();

        (await session.Query<Catch>().Where(x => x.Angler == "Sam")
            .Select(x => x.Water.Name).ToListAsync(Token)).ShouldBe(["Brandywine"]);
    }

    // ---- shaped projections ----

    [Fact]
    public async Task an_anonymous_type()
    {
        await using var session = Session();

        var rows = await session.Query<Catch>().Where(x => x.Angler == "Pippin")
            .Select(x => new { x.Angler, x.Weight }).ToListAsync(Token);

        rows.ShouldHaveSingleItem();
        rows[0].Angler.ShouldBe("Pippin");
        rows[0].Weight.ShouldBe(2);
    }

    [Fact]
    public async Task a_constructor()
    {
        await using var session = Session();

        (await session.Query<Catch>().OrderBy(x => x.Weight)
            .Select(x => new Summary(x.Angler, x.Weight)).ToListAsync(Token))
            .Select(x => x.Angler).ShouldBe(["Pippin", "Frodo", "Merry", "Sam"]);
    }

    [Fact]
    public async Task an_object_initialiser()
    {
        await using var session = Session();

        var rows = await session.Query<Catch>().Where(x => x.Angler == "Merry")
            .Select(x => new Card { Who = x.Angler, What = x.Species }).ToListAsync(Token);

        rows.ShouldHaveSingleItem();
        rows[0].Who.ShouldBe("Merry");
        rows[0].What.ShouldBe("Trout");
    }

    /// <summary>
    ///     Only the member accesses become columns; everything around them runs in .NET, per row. That
    ///     is a deliberate boundary — see <c>SelectProjection</c> — and it means a projection can be any
    ///     C# expression without any of it needing to be translatable.
    /// </summary>
    [Fact]
    public async Task computation_around_the_members_runs_in_dotnet()
    {
        await using var session = Session();

        (await session.Query<Catch>().Where(x => x.Angler == "Sam")
            .Select(x => $"{x.Angler} caught a {x.Species}").ToListAsync(Token))
            .ShouldBe(["Sam caught a Pike"]);

        (await session.Query<Catch>().Where(x => x.Angler == "Sam")
            .Select(x => x.Weight * 2).ToListAsync(Token)).ShouldBe([22]);
    }

    /// <summary>
    ///     A member reached twice is one column, not two.
    /// </summary>
    [Fact]
    public async Task repeated_members_are_selected_once()
    {
        await using var session = Session();

        var rows = await session.Query<Catch>().Where(x => x.Angler == "Sam")
            .Select(x => new { Half = x.Weight / 2, Double = x.Weight * 2 }).ToListAsync(Token);

        rows[0].Half.ShouldBe(5);
        rows[0].Double.ShouldBe(22);

        // Asserted on the analyzer directly, because the result above is the same either way — two
        // columns holding the same value would compute the same answer while reading twice as much.
        var projection = Fisher.Linq.Parsing.SelectProjection.For(
            (System.Linq.Expressions.Expression<Func<Catch, object>>)(x => new { A = x.Weight, B = x.Weight }),
            new Fisher.Linq.Members.MemberFactory(_store.Options, _store.Options.Schema.MappingFor(typeof(Catch))));

        projection.Locators.Length.ShouldBe(1);
    }

    // ---- composition with everything else ----

    [Fact]
    public async Task a_projection_composes_with_where_order_and_paging()
    {
        await using var session = Session();

        (await session.Query<Catch>().Where(x => x.Weight > 2).OrderByDescending(x => x.Weight)
            .Take(2).Select(x => x.Angler).ToListAsync(Token)).ShouldBe(["Sam", "Merry"]);
    }

    [Fact]
    public async Task the_terminals_work_over_a_projection()
    {
        await using var session = Session();

        (await session.Query<Catch>().Select(x => x.Angler).CountAsync(Token)).ShouldBe(4);
        (await session.Query<Catch>().Select(x => x.Angler).AnyAsync(Token)).ShouldBeTrue();

        (await session.Query<Catch>().OrderBy(x => x.Weight).Select(x => x.Angler)
            .FirstAsync(Token)).ShouldBe("Pippin");

        (await session.Query<Catch>().OrderBy(x => x.Weight).Select(x => x.Angler)
            .LastAsync(Token)).ShouldBe("Sam");

        (await session.Query<Catch>().Where(x => x.Angler == "Sam").Select(x => x.Weight)
            .SingleAsync(Token)).ShouldBe(11);
    }

    // ---- Distinct ----

    [Fact]
    public async Task distinct_over_a_projection()
    {
        await using var session = Session();

        (await session.Query<Catch>().Select(x => x.Species).Distinct().OrderBy(x => x)
            .ToListAsync(Token)).ShouldBe(["Chub", "Pike", "Trout"]);
    }

    [Fact]
    public async Task counting_a_distinct_projection_counts_the_distinct_values()
    {
        await using var session = Session();

        (await session.Query<Catch>().Select(x => x.Species).Distinct().CountAsync(Token)).ShouldBe(3);
    }

    /// <summary>
    ///     Over whole documents DISTINCT would compare serialized JSON byte for byte. Refused, with
    ///     <c>DistinctBy</c> named as the operator that was meant.
    /// </summary>
    [Fact]
    public async Task distinct_without_a_projection_is_refused_by_name()
    {
        await using var session = Session();

        var exception = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Catch>().Distinct().ToListAsync(Token));

        exception.Message.ShouldContain("DistinctBy");
    }

    // ---- DistinctBy ----

    /// <summary>
    ///     One whole document per key, which DISTINCT cannot express — hence the <c>row_number()</c>
    ///     window.
    /// </summary>
    [Fact]
    public async Task distinct_by_keeps_one_document_per_key()
    {
        await using var session = Session();

        var rows = await session.Query<Catch>().DistinctBy(x => x.Species)
            .OrderBy(x => x.Species).ToListAsync(Token);

        rows.Select(x => x.Species).ShouldBe(["Chub", "Pike", "Trout"]);
        rows.ShouldAllBe(x => x.Angler != "");
    }

    [Fact]
    public async Task distinct_by_composes_with_where_and_paging()
    {
        await using var session = Session();

        (await session.Query<Catch>().Where(x => x.Weight > 2).DistinctBy(x => x.Species)
            .OrderBy(x => x.Species).ToListAsync(Token))
            .Select(x => x.Species).ShouldBe(["Pike", "Trout"]);

        (await session.Query<Catch>().DistinctBy(x => x.Species).OrderBy(x => x.Species).Take(2)
            .ToListAsync(Token)).Select(x => x.Species).ShouldBe(["Chub", "Pike"]);
    }

    [Fact]
    public async Task distinct_by_after_a_projection_is_refused_by_name()
    {
        await using var session = Session();

        var exception = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Catch>().Select(x => x.Species).DistinctBy(x => x).ToListAsync(Token));

        exception.Message.ShouldContain("Distinct()");
    }

    // ---- refusals ----

    [Fact]
    public async Task two_selects_are_refused_by_name()
    {
        await using var session = Session();

        var exception = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Catch>().Select(x => x.Angler).Select(x => x.Length).ToListAsync(Token));

        exception.Message.ShouldContain("one Select");
    }

    [Fact]
    public async Task a_projection_of_constants_only_is_refused()
    {
        await using var session = Session();

        await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<Catch>().Select(x => 1).ToListAsync(Token));
    }

    public enum Rating
    {
        Poor,
        Good,
        Excellent
    }

    public class Water
    {
        public string Name { get; set; } = "";
    }

    public class Catch
    {
        public Guid Id { get; set; }
        public string Species { get; set; } = "";
        public string Angler { get; set; } = "";
        public int Weight { get; set; }
        public decimal Fee { get; set; }
        public Rating Rating { get; set; }
        public DateTimeOffset LandedAt { get; set; }
        public Water Water { get; set; } = new();
    }

    public record Summary(string Angler, int Weight);

    public class Card
    {
        public string Who { get; set; } = "";
        public string What { get; set; } = "";
    }
}
