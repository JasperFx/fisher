using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

public class ReorderedItem
{
    public string Id { get; set; } = "";
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
}

public class ReorderedCustomer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public class ReorderedOrder
{
    public string Id { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public int Quantity { get; set; }
}

public record ReorderedRow(string Name, int Quantity);

/// <summary>
///     A second <c>OrderBy</c> re-sorts — fisher#588, marten#5959.
/// </summary>
/// <remarks>
///     <para>
///         LINQ's <c>OrderBy</c> is a stable sort, so <c>OrderBy(a).OrderBy(b)</c> means <c>b, a</c>: the
///         newest key is the primary one and every earlier key only breaks its ties. Fisher appended both
///         kinds of ordering, rendering <c>order by a, b</c> — a plausible order and the wrong one.
///     </para>
///     <para>
///         LINQ to Objects is the oracle throughout, over data chosen so that every shape here comes out
///         differently under the two readings. Ids and names are lowercase ASCII, so the oracle's culture
///         comparison and SQLite's binary one agree.
///     </para>
/// </remarks>
public class repeated_order_by : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("repeated_order_by");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly List<ReorderedItem> Items =
    [
        new() { Id = "t1", Number = 3, Name = "delta", Category = "b" },
        new() { Id = "t2", Number = 1, Name = "alpha", Category = "a" },
        new() { Id = "t3", Number = 5, Name = "echo", Category = "b" },
        new() { Id = "t4", Number = 2, Name = "charlie", Category = "c" },
        new() { Id = "t5", Number = 4, Name = "bravo", Category = "a" }
    ];

    private static readonly List<ReorderedCustomer> Customers =
    [
        new() { Id = "c1", Name = "ann" },
        new() { Id = "c2", Name = "bob" }
    ];

    private static readonly List<ReorderedOrder> Orders =
    [
        new() { Id = "o1", CustomerId = "c1", Quantity = 5 },
        new() { Id = "o2", CustomerId = "c1", Quantity = 2 },
        new() { Id = "o3", CustomerId = "c2", Quantity = 9 },
        new() { Id = "o4", CustomerId = "c2", Quantity = 1 },
        new() { Id = "o5", CustomerId = "c1", Quantity = 7 }
    ];

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<ReorderedItem>();
            options.Schema.For<ReorderedCustomer>();
            options.Schema.For<ReorderedOrder>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        foreach (var item in Items) session.Store(item);
        foreach (var customer in Customers) session.Store(customer);
        foreach (var order in Orders) session.Store(order);
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private IQueryable<ReorderedItem> Query(IDocumentSession session) => session.Query<ReorderedItem>();

    [Fact]
    public async Task a_second_order_by_becomes_the_primary_key()
    {
        await using var session = _store.LightweightSession();

        var actual = await Query(session).OrderBy(x => x.Category).OrderBy(x => x.Name).Select(x => x.Name)
            .ToListAsync(Token);

        actual.ShouldBe(Items.OrderBy(x => x.Category).OrderBy(x => x.Name).Select(x => x.Name).ToList());
    }

    [Fact]
    public async Task the_earlier_order_by_breaks_the_ties()
    {
        await using var session = _store.LightweightSession();

        var actual = await Query(session).OrderBy(x => x.Name).OrderBy(x => x.Category).Select(x => x.Name)
            .ToListAsync(Token);

        actual.ShouldBe(Items.OrderBy(x => x.Name).OrderBy(x => x.Category).Select(x => x.Name).ToList());
    }

    [Fact]
    public async Task descending_keys_keep_their_direction()
    {
        await using var session = _store.LightweightSession();

        var actual = await Query(session).OrderByDescending(x => x.Category).OrderBy(x => x.Number)
            .Select(x => x.Name).ToListAsync(Token);

        actual.ShouldBe(Items.OrderByDescending(x => x.Category).OrderBy(x => x.Number).Select(x => x.Name)
            .ToList());
    }

    [Fact]
    public async Task an_order_by_after_a_then_by_goes_in_front_of_both()
    {
        await using var session = _store.LightweightSession();

        var actual = await Query(session).OrderBy(x => x.Category).ThenBy(x => x.Name)
            .OrderByDescending(x => x.Number).Select(x => x.Name).ToListAsync(Token);

        actual.ShouldBe(Items.OrderBy(x => x.Category).ThenBy(x => x.Name).OrderByDescending(x => x.Number)
            .Select(x => x.Name).ToList());
    }

    [Fact]
    public async Task a_then_by_after_a_second_order_by_refines_the_new_primary_key()
    {
        await using var session = _store.LightweightSession();

        var actual = await Query(session).OrderBy(x => x.Number).OrderBy(x => x.Category)
            .ThenByDescending(x => x.Name).Select(x => x.Name).ToListAsync(Token);

        actual.ShouldBe(Items.OrderBy(x => x.Number).OrderBy(x => x.Category).ThenByDescending(x => x.Name)
            .Select(x => x.Name).ToList());
    }

    [Fact]
    public async Task first_reads_the_new_primary_key()
    {
        await using var session = _store.LightweightSession();

        var actual = await Query(session).OrderBy(x => x.Category).OrderByDescending(x => x.Number)
            .FirstAsync(Token);

        actual!.Name.ShouldBe(Items.OrderBy(x => x.Category).OrderByDescending(x => x.Number).First().Name);
    }

    [Fact]
    public async Task last_reverses_every_key_in_the_new_order()
    {
        await using var session = _store.LightweightSession();

        var actual = await Query(session).OrderBy(x => x.Name).OrderBy(x => x.Category).LastAsync(Token);

        actual!.Name.ShouldBe(Items.OrderBy(x => x.Name).OrderBy(x => x.Category).Last().Name);
    }

    [Fact]
    public async Task take_pages_the_new_order()
    {
        await using var session = _store.LightweightSession();

        var actual = await Query(session).OrderBy(x => x.Name).OrderBy(x => x.Category).Take(3)
            .Select(x => x.Name).ToListAsync(Token);

        actual.ShouldBe(Items.OrderBy(x => x.Name).OrderBy(x => x.Category).Take(3).Select(x => x.Name)
            .ToList());
    }

    /// <summary>
    ///     The identity was the first key and is the tie-breaker after the re-sort, which is the order keyset
    ///     paging requires. Under the appended order it was the primary key and the walk was refused, so
    ///     this also pins that the members behind the keys moved with them.
    /// </summary>
    [Fact]
    public async Task a_cursor_walk_follows_the_new_order()
    {
        await using var session = _store.LightweightSession();

        var seen = new List<string>();
        string? cursor = null;

        do
        {
            var page = await Query(session).OrderBy(x => x.Id).OrderBy(x => x.Category)
                .ToCursorPageAsync(2, cursor, Token);

            seen.AddRange(page.Items.Select(x => x.Name));
            cursor = page.NextCursor;
        } while (cursor is not null);

        seen.ShouldBe(Items.OrderBy(x => x.Id, StringComparer.Ordinal).OrderBy(x => x.Category)
            .Select(x => x.Name).ToList());
    }

    [Fact]
    public async Task an_order_by_after_a_select_re_sorts_the_projected_value()
    {
        await using var session = _store.LightweightSession();

        var actual = await Query(session).OrderBy(x => x.Name).Select(x => x.Category).OrderBy(c => c)
            .ToListAsync(Token);

        actual.ShouldBe(Items.OrderBy(x => x.Name).Select(x => x.Category).OrderBy(c => c).ToList());
    }

    [Fact]
    public async Task a_second_order_by_after_a_join_becomes_the_primary_key()
    {
        await using var session = _store.LightweightSession();

        var actual = await session.Query<ReorderedCustomer>()
            .Join(session.Query<ReorderedOrder>(), c => c.Id, o => o.CustomerId,
                (c, o) => new ReorderedRow(c.Name, o.Quantity))
            .OrderBy(x => x.Name).OrderBy(x => x.Quantity)
            .ToListAsync(Token);

        var expected = Customers
            .Join(Orders, c => c.Id, o => o.CustomerId, (c, o) => new ReorderedRow(c.Name, o.Quantity))
            .OrderBy(x => x.Name).OrderBy(x => x.Quantity)
            .ToList();

        actual.ShouldBe(expected);
    }

    [Fact]
    public async Task a_then_by_after_a_join_still_refines()
    {
        await using var session = _store.LightweightSession();

        var actual = await session.Query<ReorderedCustomer>()
            .Join(session.Query<ReorderedOrder>(), c => c.Id, o => o.CustomerId,
                (c, o) => new ReorderedRow(c.Name, o.Quantity))
            .OrderByDescending(x => x.Quantity).OrderBy(x => x.Name).ThenBy(x => x.Quantity)
            .ToListAsync(Token);

        var expected = Customers
            .Join(Orders, c => c.Id, o => o.CustomerId, (c, o) => new ReorderedRow(c.Name, o.Quantity))
            .OrderByDescending(x => x.Quantity).OrderBy(x => x.Name).ThenBy(x => x.Quantity)
            .ToList();

        actual.ShouldBe(expected);
    }
}
