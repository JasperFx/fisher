using Fisher.Linq;
using JasperFx;
using Shouldly;

namespace Fisher.Tests.Linq;

public class ClosedKeyCustomer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public class ClosedKeyOrder
{
    public string Id { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string Item { get; set; } = "";
    public int Quantity { get; set; }
}

public record ClosedKeyRow(string Name, string Item, int Quantity);

/// <summary>
///     Statics named after members of the joined documents and of the row on purpose: read as a JSON
///     path, <c>Quantity</c> is <c>$.quantity</c> on the order and <c>Name</c> is <c>$.name</c> on the
///     customer.
/// </summary>
public static class ClosedKeyStatics
{
    public static int Quantity = 0;
    public static string Name = "same";
}

/// <summary>
///     fisher#596, the join half of fisher#591. A static member or a captured local used as an ordering key
///     after a join is the same value for every row, so it is dropped and the keys after it decide, exactly
///     as on the document path. LINQ to Objects is the oracle throughout.
/// </summary>
/// <remarks>
///     Before this, the join path refused every such key ("Fisher cannot order a join by ..."), because
///     <c>JoinedMember</c> only accepts a member of a joined document. Not a wrong answer, but a refusal of
///     a query LINQ answers and the document path answers. The data is chosen so that ordering by
///     <c>$.quantity</c> or <c>$.name</c> instead of dropping the key would come out differently.
/// </remarks>
public class join_ordering_closed_values : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("join-closed-keys");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly List<ClosedKeyCustomer> Customers =
    [
        new() { Id = "c1", Name = "bob" },
        new() { Id = "c2", Name = "ann" },
        new() { Id = "c3", Name = "cat" }
    ];

    // Item order, quantity order and customer-name order all disagree.
    private static readonly List<ClosedKeyOrder> Orders =
    [
        new() { Id = "o1", CustomerId = "c2", Item = "echo", Quantity = 1 },
        new() { Id = "o2", CustomerId = "c1", Item = "alpha", Quantity = 4 },
        new() { Id = "o3", CustomerId = "c2", Item = "bravo", Quantity = 5 },
        new() { Id = "o4", CustomerId = "c1", Item = "delta", Quantity = 2 },
        new() { Id = "o5", CustomerId = "c2", Item = "charlie", Quantity = 3 }
    ];

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<ClosedKeyCustomer>();
            options.Schema.For<ClosedKeyOrder>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        foreach (var customer in Customers) session.Store(customer);
        foreach (var order in Orders) session.Store(order);
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private static IQueryable<ClosedKeyRow> Joined(IDocumentSession session)
        => session.Query<ClosedKeyCustomer>()
            .Join(session.Query<ClosedKeyOrder>(), c => c.Id, o => o.CustomerId,
                (c, o) => new ClosedKeyRow(c.Name, o.Item, o.Quantity));

    private static IEnumerable<ClosedKeyRow> JoinedInMemory()
        => Customers.Join(Orders, c => c.Id, o => o.CustomerId,
            (c, o) => new ClosedKeyRow(c.Name, o.Item, o.Quantity));

    private static IQueryable<ClosedKeyRow> GroupJoined(IDocumentSession session)
        => session.Query<ClosedKeyCustomer>()
            .GroupJoin(session.Query<ClosedKeyOrder>(), c => c.Id, o => o.CustomerId,
                (c, orders) => new { c, orders })
            .SelectMany(x => x.orders, (x, o) => new ClosedKeyRow(x.c.Name, o.Item, o.Quantity));

    private static IEnumerable<ClosedKeyRow> GroupJoinedInMemory()
        => Customers
            .GroupJoin(Orders, c => c.Id, o => o.CustomerId, (c, orders) => new { c, orders })
            .SelectMany(x => x.orders, (x, o) => new ClosedKeyRow(x.c.Name, o.Item, o.Quantity));

    // ---- Join(...) ----

    [Fact]
    public async Task join_order_by_a_static_named_like_a_member_then_by_a_member()
    {
        await using var session = _store.LightweightSession();

        var actual = await Joined(session).OrderBy(x => ClosedKeyStatics.Quantity).ThenBy(x => x.Item)
            .ToListAsync(Token);

        actual.ShouldBe(JoinedInMemory().OrderBy(x => ClosedKeyStatics.Quantity).ThenBy(x => x.Item).ToList());
    }

    [Fact]
    public async Task join_order_by_descending_a_captured_local_named_like_a_member()
    {
        await using var session = _store.LightweightSession();
        var quantity = 0;

        var actual = await Joined(session).OrderByDescending(x => quantity).ThenByDescending(x => x.Item)
            .ToListAsync(Token);

        actual.ShouldBe(JoinedInMemory().OrderByDescending(x => quantity).ThenByDescending(x => x.Item)
            .ToList());
    }

    [Fact]
    public async Task join_a_closed_key_in_the_middle_of_the_ordering()
    {
        await using var session = _store.LightweightSession();
        var name = "same";

        var actual = await Joined(session).OrderBy(x => x.Name).ThenBy(x => name)
            .ThenByDescending(x => ClosedKeyStatics.Quantity).ThenByDescending(x => x.Item)
            .ToListAsync(Token);

        actual.ShouldBe(JoinedInMemory().OrderBy(x => x.Name).ThenBy(x => name)
            .ThenByDescending(x => ClosedKeyStatics.Quantity).ThenByDescending(x => x.Item).ToList());
    }

    /// <summary>
    ///     A constant <c>OrderBy</c> still re-sorts (fisher#588): the <c>ThenBy</c> after it refines the
    ///     new, empty group, so it outranks the earlier <c>OrderBy</c>. Dropping the key without resetting
    ///     the group would render <c>quantity, item</c>.
    /// </summary>
    [Fact]
    public async Task join_a_closed_order_by_still_re_sorts()
    {
        await using var session = _store.LightweightSession();

        var actual = await Joined(session).OrderBy(x => x.Quantity).OrderBy(x => ClosedKeyStatics.Name)
            .ThenBy(x => x.Item).ToListAsync(Token);

        actual.ShouldBe(JoinedInMemory().OrderBy(x => x.Quantity).OrderBy(x => ClosedKeyStatics.Name)
            .ThenBy(x => x.Item).ToList());
    }

    [Fact]
    public async Task join_last_over_a_closed_key_and_a_member()
    {
        await using var session = _store.LightweightSession();

        var actual = await Joined(session).OrderBy(x => ClosedKeyStatics.Quantity).ThenBy(x => x.Item)
            .LastAsync(Token);

        actual.ShouldBe(JoinedInMemory().OrderBy(x => ClosedKeyStatics.Quantity).ThenBy(x => x.Item).Last());
    }

    [Fact]
    public async Task join_paged_last_over_a_closed_key_and_a_member()
    {
        await using var session = _store.LightweightSession();
        var quantity = 0;

        var actual = await Joined(session).OrderBy(x => quantity).ThenByDescending(x => x.Quantity).Take(3)
            .LastAsync(Token);

        actual.ShouldBe(JoinedInMemory().OrderBy(x => quantity).ThenByDescending(x => x.Quantity).Take(3)
            .Last());
    }

    [Fact]
    public async Task join_last_over_only_closed_keys_is_refused()
    {
        await using var session = _store.LightweightSession();

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            Joined(session).OrderBy(x => ClosedKeyStatics.Quantity).LastAsync(Token));

        ex.Message.ShouldContain("LastAsync requires an OrderBy");
    }

    [Fact]
    public async Task join_query_syntax_orders_by_a_captured_local_named_like_a_member()
    {
        await using var session = _store.LightweightSession();
        var quantity = 0;

        var actual = await (
                from c in session.Query<ClosedKeyCustomer>()
                join o in session.Query<ClosedKeyOrder>() on c.Id equals o.CustomerId
                orderby quantity, o.Item descending
                select new ClosedKeyRow(c.Name, o.Item, o.Quantity))
            .ToListAsync(Token);

        var expected = (
                from c in Customers
                join o in Orders on c.Id equals o.CustomerId
                orderby quantity, o.Item descending
                select new ClosedKeyRow(c.Name, o.Item, o.Quantity))
            .ToList();

        actual.ShouldBe(expected);
    }

    /// <summary>
    ///     A key that reads the row and also something the query closes over is not a constant and not a
    ///     member: it is refused by name rather than dropped or approximated.
    /// </summary>
    [Fact]
    public async Task join_a_key_computed_from_a_member_and_a_static_is_refused_by_name()
    {
        await using var session = _store.LightweightSession();

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            Joined(session).OrderBy(x => x.Quantity + ClosedKeyStatics.Quantity).ToListAsync(Token));

        ex.Message.ShouldContain("Fisher cannot order a join by");
    }

    // ---- GroupJoin(...).SelectMany(...) ----

    [Fact]
    public async Task group_join_order_by_a_static_named_like_a_member_then_by_a_member()
    {
        await using var session = _store.LightweightSession();

        var actual = await GroupJoined(session).OrderBy(x => ClosedKeyStatics.Name).ThenBy(x => x.Item)
            .ToListAsync(Token);

        actual.ShouldBe(GroupJoinedInMemory().OrderBy(x => ClosedKeyStatics.Name).ThenBy(x => x.Item).ToList());
    }

    [Fact]
    public async Task group_join_order_by_descending_a_captured_local_then_by_descending()
    {
        await using var session = _store.LightweightSession();
        var name = "same";

        var actual = await GroupJoined(session).OrderByDescending(x => name).ThenByDescending(x => x.Quantity)
            .ToListAsync(Token);

        actual.ShouldBe(GroupJoinedInMemory().OrderByDescending(x => name).ThenByDescending(x => x.Quantity)
            .ToList());
    }

    [Fact]
    public async Task group_join_last_over_a_closed_key_and_a_member()
    {
        await using var session = _store.LightweightSession();
        var quantity = 0;

        var actual = await GroupJoined(session).OrderBy(x => quantity).ThenBy(x => x.Name)
            .ThenBy(x => ClosedKeyStatics.Quantity).ThenBy(x => x.Item).LastAsync(Token);

        actual.ShouldBe(GroupJoinedInMemory().OrderBy(x => quantity).ThenBy(x => x.Name)
            .ThenBy(x => ClosedKeyStatics.Quantity).ThenBy(x => x.Item).Last());
    }

    [Fact]
    public async Task group_join_last_over_only_closed_keys_is_refused()
    {
        await using var session = _store.LightweightSession();
        var quantity = 0;

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            GroupJoined(session).OrderBy(x => quantity).ThenByDescending(x => ClosedKeyStatics.Name)
                .LastAsync(Token));

        ex.Message.ShouldContain("LastAsync requires an OrderBy");
    }

    // ---- the document path, where fisher#588's re-sort meets fisher#591's drop ----

    [Fact]
    public async Task document_a_closed_order_by_still_re_sorts()
    {
        await using var session = _store.LightweightSession();

        var actual = await session.Query<ClosedKeyOrder>().OrderBy(x => x.Quantity)
            .OrderBy(x => ClosedKeyStatics.Quantity).ThenBy(x => x.Item).Select(x => x.Item)
            .ToListAsync(Token);

        actual.ShouldBe(Orders.OrderBy(x => x.Quantity).OrderBy(x => ClosedKeyStatics.Quantity)
            .ThenBy(x => x.Item).Select(x => x.Item).ToList());
    }
}
