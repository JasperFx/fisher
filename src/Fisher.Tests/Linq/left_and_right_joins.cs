using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

public class JoinShopper
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Region { get; set; } = "";
}

public class JoinPurchase
{
    public Guid Id { get; set; }
    public Guid ShopperId { get; set; }
    public string Item { get; set; } = "";
    public int Amount { get; set; }
}

/// <summary>
///     .NET 10's <c>LeftJoin</c> and <c>RightJoin</c> — fisher#666, the port of marten#5910.
/// </summary>
/// <remarks>
///     <para>
///         <c>LeftJoin</c> is the join the <c>GroupJoin(...).SelectMany(x =&gt; x.Group.DefaultIfEmpty(), ...)</c>
///         spelling already produced, so the tests pin that the two render the same SQL. <c>RightJoin</c> is that
///         left join with its sources swapped, done as the query is built.
///     </para>
///     <para>
///         Shoppers and purchases each have a row the other side does not match: Pippin has bought nothing (and
///         his one purchase is soft-deleted), and one purchase belongs to no shopper. Every result is compared
///         with .NET 10's own <c>Enumerable.LeftJoin</c> / <c>RightJoin</c> over the same rows.
///     </para>
/// </remarks>
public class left_and_right_joins : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("left-and-right-joins");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly JoinShopper Frodo = new() { Id = Guid.NewGuid(), Name = "frodo", Region = "shire" };
    private static readonly JoinShopper Sam = new() { Id = Guid.NewGuid(), Name = "sam", Region = "shire" };
    private static readonly JoinShopper Merry = new() { Id = Guid.NewGuid(), Name = "merry", Region = "buckland" };
    private static readonly JoinShopper Pippin = new() { Id = Guid.NewGuid(), Name = "pippin", Region = "buckland" };

    private static readonly JoinShopper[] Shoppers = [Frodo, Sam, Merry, Pippin];

    private static readonly JoinPurchase[] Purchases =
    [
        new() { Id = Guid.NewGuid(), ShopperId = Frodo.Id, Item = "rope", Amount = 3 },
        new() { Id = Guid.NewGuid(), ShopperId = Frodo.Id, Item = "lembas", Amount = 11 },
        new() { Id = Guid.NewGuid(), ShopperId = Sam.Id, Item = "pan", Amount = 7 },
        new() { Id = Guid.NewGuid(), ShopperId = Merry.Id, Item = "horn", Amount = 2 },
        new() { Id = Guid.NewGuid(), ShopperId = Guid.NewGuid(), Item = "ring", Amount = 99 }
    ];

    private static readonly JoinPurchase Deleted = new() { Id = Guid.NewGuid(), ShopperId = Pippin.Id, Item = "pipe", Amount = 5 };

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<JoinShopper>();
            options.Schema.For<JoinPurchase>().SoftDeleted();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using (var session = _store.LightweightSession())
        {
            foreach (var shopper in Shoppers) session.Store(shopper);
            foreach (var purchase in Purchases) session.Store(purchase);
            session.Store(Deleted);
            await session.SaveChangesAsync(Token);
        }

        // A soft-deleted inner row is invisible to the join, and its outer row survives a left join.
        await using (var session = _store.LightweightSession())
        {
            session.Delete(Deleted);
            await session.SaveChangesAsync(Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static void ShouldBeTheSameRows<T>(IEnumerable<T> actual, IEnumerable<T> expected)
        => actual.Select(x => x!.ToString()).Order(StringComparer.Ordinal)
            .ShouldBe(expected.Select(x => x!.ToString()).Order(StringComparer.Ordinal));

    // ---- LeftJoin ----

    [Fact]
    public async Task left_join_keeps_an_outer_row_with_no_match()
    {
        await using var session = _store.QuerySession();

        #region sample_left_join
        var rows = await session.Query<JoinShopper>()
            .LeftJoin(session.Query<JoinPurchase>(),
                shopper => shopper.Id,
                purchase => purchase.ShopperId,
                (shopper, purchase) => new { shopper.Name, Item = purchase == null ? "none" : purchase.Item })
            .ToListAsync();
        #endregion

        ShouldBeTheSameRows(rows, Shoppers.LeftJoin(Purchases, x => x.Id, x => x.ShopperId,
            (shopper, purchase) => new { shopper.Name, Item = purchase == null ? "none" : purchase.Item }));

        rows.Single(x => x.Name == "pippin").Item.ShouldBe("none");
    }

    /// <summary>The same join the <c>GroupJoin</c> + <c>DefaultIfEmpty</c> spelling produces, so the same SQL.</summary>
    [Fact]
    public async Task left_join_sql_is_the_group_join_default_if_empty_sql()
    {
        await using var session = _store.QuerySession();

        var leftJoin = session.ToSql(session.Query<JoinShopper>()
            .LeftJoin(session.Query<JoinPurchase>(), x => x.Id, x => x.ShopperId,
                (shopper, purchase) => new { shopper.Name, purchase!.Amount }));

        var groupJoin = session.ToSql(session.Query<JoinShopper>()
            .GroupJoin(session.Query<JoinPurchase>(), x => x.Id, x => x.ShopperId, (shopper, group) => new { shopper, group })
            .SelectMany(x => x.group.DefaultIfEmpty(), (x, purchase) => new { x.shopper.Name, purchase!.Amount }));

        leftJoin.ShouldBe(groupJoin);
        leftJoin.ShouldContain("left join");
    }

    /// <summary>
    ///     A filter on the inner query goes in the <c>ON</c> clause, so it keeps an unmatched outer row; a filter
    ///     after the join goes in the <c>WHERE</c> and may remove one. Both are what the same clauses do in memory.
    /// </summary>
    [Fact]
    public async Task left_join_with_where_before_on_the_inner_source_and_after()
    {
        await using var session = _store.QuerySession();

        var rows = await session.Query<JoinShopper>().Where(x => x.Region == "shire" || x.Name == "pippin")
            .LeftJoin(session.Query<JoinPurchase>().Where(x => x.Amount > 5), x => x.Id, x => x.ShopperId,
                (shopper, purchase) => new { shopper.Name, Amount = purchase == null ? 0 : purchase.Amount })
            .ToListAsync(Token);

        ShouldBeTheSameRows(rows, Shoppers.Where(x => x.Region == "shire" || x.Name == "pippin")
            .LeftJoin(Purchases.Where(x => x.Amount > 5), x => x.Id, x => x.ShopperId,
                (shopper, purchase) => new { shopper.Name, Amount = purchase == null ? 0 : purchase.Amount }));

        var after = await session.Query<JoinShopper>()
            .LeftJoin(session.Query<JoinPurchase>(), x => x.Id, x => x.ShopperId,
                (shopper, purchase) => new { shopper.Name, shopper.Region, purchase })
            .Where(x => x.Region == "buckland")
            .ToListAsync(Token);

        after.Select(x => $"{x.Name}/{x.purchase?.Item ?? "none"}").Order(StringComparer.Ordinal)
            .ShouldBe(["merry/horn", "pippin/none"]);
    }

    [Fact]
    public async Task left_join_ordered_and_paged()
    {
        await using var session = _store.QuerySession();

        var expected = Shoppers.LeftJoin(Purchases, x => x.Id, x => x.ShopperId,
                (shopper, purchase) => new { shopper.Name, Item = purchase == null ? "" : purchase.Item })
            .OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Item, StringComparer.Ordinal)
            .Skip(1).Take(3).ToList();

        var rows = await session.Query<JoinShopper>()
            .LeftJoin(session.Query<JoinPurchase>(), x => x.Id, x => x.ShopperId,
                (shopper, purchase) => new { shopper.Name, purchase })
            .OrderBy(x => x.Name).ThenBy(x => x.purchase!.Item)
            .Skip(1).Take(3)
            .ToListAsync(Token);

        rows.Select(x => new { x.Name, Item = x.purchase == null ? "" : x.purchase.Item }).ToList().ShouldBe(expected);
    }

    [Fact]
    public async Task the_terminals_over_a_left_join()
    {
        await using var session = _store.QuerySession();

        var query = session.Query<JoinShopper>()
            .LeftJoin(session.Query<JoinPurchase>(), x => x.Id, x => x.ShopperId,
                (shopper, purchase) => new { shopper.Name, purchase });

        var expected = Shoppers.LeftJoin(Purchases, x => x.Id, x => x.ShopperId,
            (shopper, purchase) => new { shopper.Name, purchase }).ToList();

        (await query.CountAsync(Token)).ShouldBe(expected.Count);
        (await query.AnyAsync(Token)).ShouldBeTrue();
        (await query.OrderBy(x => x.Name).FirstAsync(Token)).Name.ShouldBe("frodo");
        (await query.OrderBy(x => x.Name).LastAsync(Token)).Name.ShouldBe("sam");

        // An aggregate over the inner side skips the unmatched rows, as SUM skips a NULL.
        (await query.SumAsync(x => x.purchase!.Amount, Token))
            .ShouldBe(expected.Where(x => x.purchase != null).Sum(x => x.purchase!.Amount));
    }

    // ---- RightJoin ----

    [Fact]
    public async Task right_join_keeps_an_inner_row_with_no_match()
    {
        await using var session = _store.QuerySession();

        #region sample_right_join
        var rows = await session.Query<JoinShopper>()
            .RightJoin(session.Query<JoinPurchase>(),
                shopper => shopper.Id,
                purchase => purchase.ShopperId,
                (shopper, purchase) => new { Name = shopper == null ? "nobody" : shopper.Name, purchase.Item })
            .ToListAsync();
        #endregion

        ShouldBeTheSameRows(rows, Shoppers.RightJoin(Purchases, x => x.Id, x => x.ShopperId,
            (shopper, purchase) => new { Name = shopper == null ? "nobody" : shopper.Name, purchase.Item }));

        rows.Single(x => x.Item == "ring").Name.ShouldBe("nobody");
    }

    /// <summary>A right join is the left join with its two sources, and its key selectors, swapped.</summary>
    [Fact]
    public async Task right_join_sql_is_the_swapped_left_join_sql()
    {
        await using var session = _store.QuerySession();

        var rightJoin = session.ToSql(session.Query<JoinShopper>()
            .RightJoin(session.Query<JoinPurchase>(), x => x.Id, x => x.ShopperId,
                (shopper, purchase) => new { shopper!.Name, purchase.Amount }));

        var leftJoin = session.ToSql(session.Query<JoinPurchase>()
            .LeftJoin(session.Query<JoinShopper>(), x => x.ShopperId, x => x.Id,
                (purchase, shopper) => new { shopper!.Name, purchase.Amount }));

        rightJoin.ShouldBe(leftJoin);
    }

    /// <summary>
    ///     The outer source's filter says which of its rows may match, so it goes in the <c>ON</c> clause and keeps
    ///     an unmatched purchase; the inner source's filter says which purchases are kept at all.
    /// </summary>
    [Fact]
    public async Task right_join_filters_both_sources()
    {
        await using var session = _store.QuerySession();

        var rows = await session.Query<JoinShopper>().Where(x => x.Region == "shire")
            .RightJoin(session.Query<JoinPurchase>().Where(x => x.Amount > 1), x => x.Id, x => x.ShopperId,
                (shopper, purchase) => new { Name = shopper == null ? "nobody" : shopper.Name, purchase.Item })
            .ToListAsync(Token);

        ShouldBeTheSameRows(rows, Shoppers.Where(x => x.Region == "shire")
            .RightJoin(Purchases.Where(x => x.Amount > 1), x => x.Id, x => x.ShopperId,
                (shopper, purchase) => new { Name = shopper == null ? "nobody" : shopper.Name, purchase.Item }));
    }

    [Fact]
    public async Task right_join_ordered_with_a_computed_key_and_counted()
    {
        await using var session = _store.QuerySession();

        var query = session.Query<JoinShopper>()
            .RightJoin(session.Query<JoinPurchase>(), x => x.Id, x => x.ShopperId,
                (shopper, purchase) => new { purchase.Item, purchase.Amount })
            .OrderBy(x => -x.Amount);

        (await query.ToListAsync(Token)).Select(x => x.Item)
            .ShouldBe(Purchases.OrderBy(x => -x.Amount).Select(x => x.Item));
        (await query.CountAsync(Token)).ShouldBe(Purchases.Length);
    }

    // ---- what is refused ----

    [Fact]
    public async Task right_join_with_another_operator_before_it_is_refused_by_name()
    {
        await using var session = _store.QuerySession();

        (await Should.ThrowAsync<BadLinqExpressionException>(() => session.Query<JoinShopper>().OrderBy(x => x.Name)
                .RightJoin(session.Query<JoinPurchase>(), x => x.Id, x => x.ShopperId, (s, p) => new { p.Item })
                .ToListAsync(Token)))
            .Message.ShouldContain("'OrderBy'");

        (await Should.ThrowAsync<BadLinqExpressionException>(() => session.Query<JoinShopper>()
                .RightJoin(session.Query<JoinPurchase>().Take(2), x => x.Id, x => x.ShopperId, (s, p) => new { p.Item })
                .ToListAsync(Token)))
            .Message.ShouldContain("'Take'");
    }

    [Fact]
    public async Task a_join_over_an_in_memory_sequence_is_refused_by_name()
    {
        await using var session = _store.QuerySession();

        await Should.ThrowAsync<BadLinqExpressionException>(() => session.Query<JoinShopper>()
            .RightJoin(Purchases, x => x.Id, x => x.ShopperId, (s, p) => new { p.Item }).ToListAsync(Token));

        await Should.ThrowAsync<BadLinqExpressionException>(() => session.Query<JoinShopper>()
            .LeftJoin(Purchases, x => x.Id, x => x.ShopperId, (s, p) => new { s.Name }).ToListAsync(Token));
    }

    /// <summary>
    ///     The join compares the keys' stored values in SQL, so a comparer would be ignored — which for a
    ///     case-insensitive one is a different answer. Refused on every join operator.
    /// </summary>
    [Fact]
    public async Task a_join_with_a_key_comparer_is_refused_by_name()
    {
        await using var session = _store.QuerySession();
        var comparer = StringComparer.OrdinalIgnoreCase;

        (await Should.ThrowAsync<BadLinqExpressionException>(() => session.Query<JoinShopper>()
                .Join(session.Query<JoinPurchase>(), x => x.Name, x => x.Item, (s, p) => new { s.Name }, comparer)
                .ToListAsync(Token)))
            .Message.ShouldContain("comparer");

        (await Should.ThrowAsync<BadLinqExpressionException>(() => session.Query<JoinShopper>()
                .GroupJoin(session.Query<JoinPurchase>(), x => x.Name, x => x.Item, (s, g) => new { s, g }, comparer)
                .SelectMany(x => x.g, (x, p) => new { x.s.Name })
                .ToListAsync(Token)))
            .Message.ShouldContain("comparer");

        (await Should.ThrowAsync<BadLinqExpressionException>(() => session.Query<JoinShopper>()
                .LeftJoin(session.Query<JoinPurchase>(), x => x.Name, x => x.Item, (s, p) => new { s.Name }, comparer)
                .ToListAsync(Token)))
            .Message.ShouldContain("comparer");

        (await Should.ThrowAsync<BadLinqExpressionException>(() => session.Query<JoinShopper>()
                .RightJoin(session.Query<JoinPurchase>(), x => x.Name, x => x.Item, (s, p) => new { p.Item }, comparer)
                .ToListAsync(Token)))
            .Message.ShouldContain("comparer");
    }
}
