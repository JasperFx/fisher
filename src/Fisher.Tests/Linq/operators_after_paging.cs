using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

public class PageTarget
{
    public Guid Id { get; set; }
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
}

public class PageCustomer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public class PageOrder
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public int Quantity { get; set; }
}

/// <summary>
///     An operator after <c>Take</c>/<c>Skip</c> applies to the page — fisher#589.
/// </summary>
/// <remarks>
///     LINQ to Objects over the same rows is the oracle throughout. Before #589, <c>Take</c>/<c>Skip</c>
///     overwrote the statement's paging and every later operator was folded into the same statement,
///     so it ran before the LIMIT: a <c>Where</c> filtered the table and then took the page, and a
///     second <c>Take</c> or <c>Skip</c> replaced the first.
/// </remarks>
public class operators_after_paging : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("after-paging");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Number is unique, so ordering by it is total; Category has ties, which is what a re-sort's
    // tie-breaking needs.
    private static readonly List<PageTarget> Targets =
    [
        new() { Id = Guid.NewGuid(), Number = 3, Name = "delta", Category = "b" },
        new() { Id = Guid.NewGuid(), Number = 1, Name = "alpha", Category = "a" },
        new() { Id = Guid.NewGuid(), Number = 5, Name = "echo", Category = "b" },
        new() { Id = Guid.NewGuid(), Number = 2, Name = "charlie", Category = "c" },
        new() { Id = Guid.NewGuid(), Number = 4, Name = "bravo", Category = "a" },
        new() { Id = Guid.NewGuid(), Number = 6, Name = "foxtrot", Category = "b" }
    ];

    private static readonly List<PageCustomer> Customers =
    [
        new() { Id = Guid.NewGuid(), Name = "Ann" },
        new() { Id = Guid.NewGuid(), Name = "Bob" }
    ];

    private static readonly List<PageOrder> Orders =
    [
        new() { Id = Guid.NewGuid(), CustomerId = Customers[0].Id, Quantity = 5 },
        new() { Id = Guid.NewGuid(), CustomerId = Customers[0].Id, Quantity = 2 },
        new() { Id = Guid.NewGuid(), CustomerId = Customers[1].Id, Quantity = 9 },
        new() { Id = Guid.NewGuid(), CustomerId = Customers[1].Id, Quantity = 1 },
        new() { Id = Guid.NewGuid(), CustomerId = Customers[0].Id, Quantity = 7 }
    ];

    public record PageRow(string Name, int Quantity);

    private static IOrderedEnumerable<PageTarget> ByNumber => Targets.OrderBy(x => x.Number);

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            // A duplicated field is a generated column rather than a json_extract, so ordering by it
            // after the page proves the page carries every column of the table, not just `data`.
            options.Schema.For<PageTarget>().Duplicate(x => x.Name);
            options.Schema.For<PageCustomer>();
            options.Schema.For<PageOrder>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        foreach (var target in Targets) session.Store(target);
        foreach (var customer in Customers) session.Store(customer);
        foreach (var order in Orders) session.Store(order);
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private IQueryable<PageTarget> Query(IQuerySession session)
        => session.Query<PageTarget>().OrderBy(x => x.Number);

    // ---- consecutive paging composes ----

    [Fact]
    public async Task take_then_take_is_the_smaller_take()
    {
        await using var session = _store.QuerySession();

        (await Query(session).Take(2).Take(4).Select(x => x.Number).ToListAsync(Token))
            .ShouldBe(ByNumber.Take(2).Take(4).Select(x => x.Number));

        (await Query(session).Take(4).Take(2).Select(x => x.Number).ToListAsync(Token))
            .ShouldBe(ByNumber.Take(4).Take(2).Select(x => x.Number));
    }

    [Fact]
    public async Task skips_add_up()
    {
        await using var session = _store.QuerySession();

        (await Query(session).Skip(1).Skip(2).Select(x => x.Number).ToListAsync(Token))
            .ShouldBe(ByNumber.Skip(1).Skip(2).Select(x => x.Number));
    }

    [Fact]
    public async Task take_then_skip_skips_into_the_page()
    {
        await using var session = _store.QuerySession();

        (await Query(session).Take(3).Skip(2).Select(x => x.Number).ToListAsync(Token))
            .ShouldBe(ByNumber.Take(3).Skip(2).Select(x => x.Number));

        (await Query(session).Take(3).Skip(5).Select(x => x.Number).ToListAsync(Token))
            .ShouldBeEmpty();

        (await Query(session).Skip(1).Take(4).Skip(1).Take(2).Select(x => x.Number).ToListAsync(Token))
            .ShouldBe(ByNumber.Skip(1).Take(4).Skip(1).Take(2).Select(x => x.Number));
    }

    [Fact]
    public async Task skip_then_take_is_unchanged()
    {
        await using var session = _store.QuerySession();

        (await Query(session).Skip(2).Take(3).Select(x => x.Number).ToListAsync(Token))
            .ShouldBe(ByNumber.Skip(2).Take(3).Select(x => x.Number));
    }

    [Fact]
    public async Task a_terminal_does_not_lift_a_smaller_limit()
    {
        await using var session = _store.QuerySession();

        (await Query(session).Take(0).FirstOrDefaultAsync(Token)).ShouldBeNull();
        (await Query(session).Take(3).Skip(5).FirstOrDefaultAsync(Token)).ShouldBeNull();

        // Fetching two rows to detect a second would refuse what LINQ answers.
        (await Query(session).Take(1).SingleAsync(Token))!.Number.ShouldBe(ByNumber.Take(1).Single().Number);
    }

    // ---- an operator after paging applies to the page ----

    [Fact]
    public async Task where_after_take()
    {
        await using var session = _store.QuerySession();

        (await Query(session).Take(3).Where(x => x.Number > 1).Select(x => x.Number).ToListAsync(Token))
            .ShouldBe(ByNumber.Take(3).Where(x => x.Number > 1).Select(x => x.Number));

        // Whole documents read back through the same columns the page selected.
        (await Query(session).Skip(1).Take(3).Where(x => x.Category != "c").ToListAsync(Token))
            .Select(x => x.Name).ShouldBe(ByNumber.Skip(1).Take(3).Where(x => x.Category != "c").Select(x => x.Name));
    }

    [Fact]
    public async Task where_after_skip()
    {
        await using var session = _store.QuerySession();

        (await Query(session).Skip(3).Where(x => x.Category == "a").Select(x => x.Name).ToListAsync(Token))
            .ShouldBe(ByNumber.Skip(3).Where(x => x.Category == "a").Select(x => x.Name));
    }

    [Fact]
    public async Task paging_a_filtered_page()
    {
        await using var session = _store.QuerySession();

        (await Query(session).Take(3).Where(x => x.Number > 1).Take(5).Select(x => x.Number).ToListAsync(Token))
            .ShouldBe(ByNumber.Take(3).Where(x => x.Number > 1).Take(5).Select(x => x.Number));

        (await Query(session).Take(4).Where(x => x.Number > 1).Skip(1).Select(x => x.Number).ToListAsync(Token))
            .ShouldBe(ByNumber.Take(4).Where(x => x.Number > 1).Skip(1).Select(x => x.Number));

        (await Query(session).Take(3).Where(x => x.Number > 1).Take(5).CountAsync(Token))
            .ShouldBe(ByNumber.Take(3).Where(x => x.Number > 1).Take(5).Count());
    }

    [Fact]
    public async Task order_by_after_take_re_sorts_the_page()
    {
        await using var session = _store.QuerySession();

        (await Query(session).Take(3).OrderByDescending(x => x.Name).Select(x => x.Name).ToListAsync(Token))
            .ShouldBe(ByNumber.Take(3).OrderByDescending(x => x.Name).Select(x => x.Name));

        (await Query(session).Take(4).OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(Token))
            .ShouldBe(ByNumber.Take(4).OrderBy(x => x.Id.ToString(), StringComparer.Ordinal).Select(x => x.Id));
    }

    [Fact]
    public async Task a_re_sort_keeps_the_page_order_among_its_ties()
    {
        await using var session = _store.QuerySession();

        // LINQ's OrderBy is stable: within one Category the page's own Number order survives.
        (await Query(session).Take(5).OrderBy(x => x.Category).Select(x => x.Name).ToListAsync(Token))
            .ShouldBe(ByNumber.Take(5).OrderBy(x => x.Category).Select(x => x.Name));

        (await Query(session).Take(5).OrderByDescending(x => x.Category).ThenBy(x => x.Name).Select(x => x.Name)
                .ToListAsync(Token))
            .ShouldBe(ByNumber.Take(5).OrderByDescending(x => x.Category).ThenBy(x => x.Name).Select(x => x.Name));
    }

    [Fact]
    public async Task distinct_after_take()
    {
        await using var session = _store.QuerySession();

        var actual = await session.Query<PageTarget>().OrderByDescending(x => x.Number).Select(x => x.Category)
            .Take(3).Distinct().ToListAsync(Token);

        actual.Order().ShouldBe(Targets.OrderByDescending(x => x.Number).Select(x => x.Category).Take(3)
            .Distinct().Order());

        (await session.Query<PageTarget>().OrderByDescending(x => x.Number).Select(x => x.Category)
                .Take(3).Distinct().CountAsync(Token))
            .ShouldBe(Targets.OrderByDescending(x => x.Number).Select(x => x.Category).Take(3).Distinct().Count());
    }

    [Fact]
    public async Task a_terminal_predicate_filters_the_page()
    {
        await using var session = _store.QuerySession();

        (await Query(session).Take(2).CountAsync(x => x.Number > 1, Token))
            .ShouldBe(ByNumber.Take(2).Count(x => x.Number > 1));

        (await Query(session).Take(2).LongCountAsync(x => x.Number > 1, Token))
            .ShouldBe(ByNumber.Take(2).LongCount(x => x.Number > 1));

        (await Query(session).Take(2).AnyAsync(x => x.Number > 2, Token))
            .ShouldBe(ByNumber.Take(2).Any(x => x.Number > 2));

        (await Query(session).Take(2).FirstOrDefaultAsync(x => x.Number > 2, Token))
            .ShouldBe(ByNumber.Take(2).FirstOrDefault(x => x.Number > 2));

        (await Query(session).Take(3).FirstOrDefaultAsync(x => x.Number > 1, Token))!.Name
            .ShouldBe(ByNumber.Take(3).First(x => x.Number > 1).Name);

        (await Query(session).Skip(4).SingleOrDefaultAsync(x => x.Number < 6, Token))!.Name
            .ShouldBe(ByNumber.Skip(4).Single(x => x.Number < 6).Name);

        // A plain count of the page is still the page.
        (await Query(session).Take(2).CountAsync(Token)).ShouldBe(2);
    }

    [Fact]
    public async Task last_and_the_aggregates_read_the_filtered_page()
    {
        await using var session = _store.QuerySession();

        (await Query(session).Take(3).Where(x => x.Number > 1).LastAsync(Token))!.Number
            .ShouldBe(ByNumber.Take(3).Where(x => x.Number > 1).Last().Number);

        (await Query(session).Take(4).Where(x => x.Category != "a").SumAsync(x => x.Number, Token))
            .ShouldBe(ByNumber.Take(4).Where(x => x.Category != "a").Sum(x => x.Number));

        (await Query(session).Take(3).Where(x => x.Number > 1).Take(5).MaxAsync(x => x.Number, Token))
            .ShouldBe(ByNumber.Take(3).Where(x => x.Number > 1).Take(5).Max(x => x.Number));
    }

    [Fact]
    public async Task a_json_read_of_a_filtered_page()
    {
        await using var session = _store.QuerySession();

        var json = await Query(session).Take(3).Where(x => x.Number > 1).ToJsonArrayAsync(Token);

        var names = System.Text.Json.JsonDocument.Parse(json).RootElement.EnumerateArray()
            .Select(x => x.GetProperty("name").GetString()).ToList();

        names.ShouldBe(ByNumber.Take(3).Where(x => x.Number > 1).Select(x => x.Name));
    }

    [Fact]
    public async Task a_pager_pages_the_query_including_its_own_paging()
    {
        await using var session = _store.QuerySession();

        var page = await Query(session).Take(4).ToPagedListAsync(2, 3, Token);

        page.Select(x => x.Number).ShouldBe(ByNumber.Take(4).Skip(3).Take(3).Select(x => x.Number));
        page.TotalItemCount.ShouldBe(4);
    }

    // ---- joins compose their paging and refuse anything else after it ----

    [Fact]
    public async Task a_join_composes_its_paging()
    {
        await using var session = _store.QuerySession();

        var oracle = Customers.Join(Orders, c => c.Id, o => o.CustomerId, (c, o) => o.Quantity).Order().ToList();

        IQueryable<PageRow> Joined() => session.Query<PageCustomer>()
            .Join(session.Query<PageOrder>(), c => c.Id, o => o.CustomerId,
                (c, o) => new PageRow(c.Name, o.Quantity))
            .OrderBy(x => x.Quantity);

        (await Joined().Take(4).Skip(1).ToListAsync(Token))
            .Select(x => x.Quantity).ShouldBe(oracle.Take(4).Skip(1));

        (await Joined().Take(2).Take(3).ToListAsync(Token))
            .Select(x => x.Quantity).ShouldBe(oracle.Take(2).Take(3));

        (await Joined().Take(4).Skip(1).CountAsync(Token)).ShouldBe(oracle.Take(4).Skip(1).Count());
    }

    [Fact]
    public async Task an_operator_after_a_joins_paging_is_refused()
    {
        await using var session = _store.QuerySession();

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(() => session.Query<PageCustomer>()
            .Join(session.Query<PageOrder>(), c => c.Id, o => o.CustomerId, (c, o) => new { c.Name, o.Quantity })
            .OrderBy(x => x.Quantity).Take(3).Where(x => x.Quantity > 1).ToListAsync(Token));

        ex.Message.ShouldContain("'Where' after Take/Skip");
    }

    // ---- refused rather than answered wrongly ----

    [Fact]
    public async Task shapes_a_page_cannot_answer_are_refused_by_name()
    {
        await using var session = _store.QuerySession();

        (await Should.ThrowAsync<BadLinqExpressionException>(() =>
                Query(session).Take(3).GroupBy(x => x.Category).Select(g => g.Key).ToListAsync(Token)))
            .Message.ShouldContain("'GroupBy' after Take/Skip");

        (await Should.ThrowAsync<BadLinqExpressionException>(() =>
                session.Query<PageTarget>().Select(x => x.Category).Distinct().Take(2).OrderBy(x => x)
                    .ToListAsync(Token)))
            .Message.ShouldContain("'OrderBy' after Take/Skip");

        (await Should.ThrowAsync<BadLinqExpressionException>(() =>
                Query(session).Take(4).Where(x => x.Number > 1).Take(2).Where(x => x.Number > 2)
                    .ToListAsync(Token)))
            .Message.ShouldContain("one level of operators over a page");

        (await Should.ThrowAsync<BadLinqExpressionException>(() =>
                Query(session).Take(3).DistinctBy(x => x.Category).ToListAsync(Token)))
            .Message.ShouldContain("'DistinctBy' after Take/Skip");

        (await Should.ThrowAsync<BadLinqExpressionException>(() =>
                Query(session).Take(3).Distinct().ToListAsync(Token)))
            .Message.ShouldContain("Distinct() after Take/Skip");
    }

    [Fact]
    public async Task keyset_paging_refuses_a_query_that_is_already_paged()
    {
        await using var session = _store.QuerySession();

        (await Should.ThrowAsync<BadLinqExpressionException>(() =>
                session.Query<PageTarget>().OrderBy(x => x.Number).ThenBy(x => x.Id).Take(3)
                    .ToCursorPageAsync(2, null, Token)))
            .Message.ShouldContain("cannot follow Take or Skip");
    }
}
