using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

public class KeyParent
{
    public Guid Id { get; set; }
    public int Number { get; set; }
    public List<KeyChild> Children { get; set; } = [];
    public List<int> Values { get; set; } = [];
}

/// <summary>A record, so the flattened rows compare by value against LINQ to Objects.</summary>
public record KeyChild
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public decimal Weight { get; set; }
}

public class KeyCustomer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int Rank { get; set; }
}

public class KeyOrder
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public int Amount { get; set; }
    public string Status { get; set; } = "";
}

/// <summary>
///     Computed ordering keys over the child documents a <c>SelectMany</c> flattens and over either side of a
///     join — fisher#666, the port of marten#5968.
/// </summary>
/// <remarks>
///     Marten's bug was that these took the member lookup, which walks past a negation to the member under it,
///     so <c>OrderBy(c =&gt; -c.Index)</c> ordered by <c>c.Index</c>. Fisher refused them instead; now they are
///     translated over the element's own locators and over each side's alias. Every shape is compared, in
///     order, with LINQ to Objects, along with a page of it, its first row and its count.
/// </remarks>
public class computed_ordering_keys_over_children_and_joins : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("computed-keys-children-joins");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Lower case only, and compared ordinally, which is how SQLite compares text.
    private static readonly string[] Pool = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf"];

    private static readonly KeyParent[] Parents = new[] { 3, 1, 5, 2, 4 }.Select(i => new KeyParent
    {
        Id = Guid.NewGuid(),
        Number = i,
        Children = Enumerable.Range(0, 3).Select(c => new KeyChild
        {
            Index = i * 3 + c - 7, Name = Pool[(i * 2 + c) % Pool.Length] + c, Weight = i + c / 4m
        }).ToList(),
        Values = [i * 2 - 5, 9 - i]
    }).ToArray();

    private static readonly KeyCustomer[] Customers = Enumerable.Range(1, 4)
        .Select(i => new KeyCustomer { Id = Guid.NewGuid(), Name = Pool[i] + i, Rank = i * 3 % 5 }).ToArray();

    private static readonly KeyOrder[] Orders = Customers.SelectMany((c, i) => Enumerable.Range(0, 3)
        .Select(k => new KeyOrder
        {
            Id = Guid.NewGuid(), CustomerId = c.Id, Amount = (i + 1) * 10 + k, Status = Pool[(i + k) % Pool.Length]
        })).ToArray();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<KeyParent>();
            options.Schema.For<KeyCustomer>();
            options.Schema.For<KeyOrder>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        foreach (var parent in Parents) session.Store(parent);
        foreach (var customer in Customers) session.Store(customer);
        foreach (var order in Orders) session.Store(order);
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    /// <summary>The rows in order, a page of them, the first row and the count.</summary>
    private async Task Children<T>(Func<IQueryable<KeyParent>, IQueryable<T>> query,
        Func<IEnumerable<KeyParent>, IEnumerable<T>>? oracle = null)
    {
        var expected = (oracle?.Invoke(Parents) ?? query(Parents.AsQueryable())).ToList();

        await using var session = _store.QuerySession();

        (await query(session.Query<KeyParent>()).ToListAsync(Token))
            .ShouldBe(expected, session.ToSql(query(session.Query<KeyParent>())));
        (await query(session.Query<KeyParent>()).Skip(2).Take(4).ToListAsync(Token))
            .ShouldBe(expected.Skip(2).Take(4).ToList());
        (await query(session.Query<KeyParent>()).FirstAsync(Token)).ShouldBe(expected.First());
        (await query(session.Query<KeyParent>()).CountAsync(Token)).ShouldBe(expected.Count);
    }

    private async Task Joined<T>(Func<IQueryable<KeyCustomer>, IQueryable<KeyOrder>, IQueryable<T>> query,
        Func<IEnumerable<KeyCustomer>, IEnumerable<KeyOrder>, IEnumerable<T>>? oracle = null)
    {
        var expected = (oracle?.Invoke(Customers, Orders) ?? query(Customers.AsQueryable(), Orders.AsQueryable()))
            .ToList();

        await using var session = _store.QuerySession();

        (await query(session.Query<KeyCustomer>(), session.Query<KeyOrder>()).ToListAsync(Token))
            .ShouldBe(expected, session.ToSql(query(session.Query<KeyCustomer>(), session.Query<KeyOrder>())));
        (await query(session.Query<KeyCustomer>(), session.Query<KeyOrder>()).Skip(2).Take(4).ToListAsync(Token))
            .ShouldBe(expected.Skip(2).Take(4).ToList());
        (await query(session.Query<KeyCustomer>(), session.Query<KeyOrder>()).FirstAsync(Token))
            .ShouldBe(expected.First());
        (await query(session.Query<KeyCustomer>(), session.Query<KeyOrder>()).CountAsync(Token))
            .ShouldBe(expected.Count);
    }

    public static TheoryData<string> ChildShapes =>
    [
        "negation", "negation_descending", "modulo_then_member", "modulo_descending_then_negation", "multiplication",
        "concatenation", "to_lower_then_member", "length_then_member", "member", "where_then_negation",
        "paged_parents_negation", "value_negation", "value_modulo_then_value", "member_of_an_indexed_parent_value"
    ];

    [Theory]
    [MemberData(nameof(ChildShapes))]
    public Task after_select_many_over_child_documents(string shape) => shape switch
    {
        "negation" => Children(q => q.SelectMany(x => x.Children).OrderBy(c => -c.Index)),
        "negation_descending" => Children(q => q.SelectMany(x => x.Children).OrderByDescending(c => -c.Index)),
        "modulo_then_member" => Children(q =>
            q.SelectMany(x => x.Children).OrderBy(c => c.Index % 3).ThenBy(c => c.Index)),
        "modulo_descending_then_negation" => Children(q =>
            q.SelectMany(x => x.Children).OrderByDescending(c => c.Index % 4).ThenBy(c => -c.Index)),
        "multiplication" => Children(q => q.SelectMany(x => x.Children).OrderBy(c => c.Index * -2 + 1)),
        "concatenation" => Children(q => q.SelectMany(x => x.Children).OrderBy(c => c.Name + c.Index),
            p => p.SelectMany(x => x.Children).OrderBy(c => c.Name + c.Index, StringComparer.Ordinal)),
        "to_lower_then_member" => Children(q =>
                q.SelectMany(x => x.Children).OrderBy(c => c.Name.ToLower()).ThenBy(c => c.Index),
            p => p.SelectMany(x => x.Children).OrderBy(c => c.Name.ToLower(), StringComparer.Ordinal)
                .ThenBy(c => c.Index)),
        "length_then_member" => Children(q =>
            q.SelectMany(x => x.Children).OrderBy(c => c.Name.Length).ThenByDescending(c => c.Index)),
        "member" => Children(q => q.SelectMany(x => x.Children).OrderByDescending(c => c.Index)),
        "where_then_negation" => Children(q =>
            q.SelectMany(x => x.Children).Where(c => c.Index % 2 == 0).OrderBy(c => -c.Index)),
        "paged_parents_negation" => Children(q =>
            q.OrderBy(x => x.Number).Skip(1).Take(3).SelectMany(x => x.Children).OrderBy(c => -c.Index)),
        "value_negation" => Children(q => q.SelectMany(x => x.Values).OrderBy(v => -v)),
        "value_modulo_then_value" => Children(q => q.SelectMany(x => x.Values).OrderBy(v => v % 3).ThenBy(v => v)),
        "member_of_an_indexed_parent_value" => Children(q =>
            q.OrderBy(x => x.Values[1]).SelectMany(x => x.Children).OrderBy(c => c.Index % 2)),
        _ => throw new ArgumentOutOfRangeException(nameof(shape))
    };

    public static TheoryData<string> JoinShapes =>
    [
        "inner_negation", "inner_negation_descending", "inner_modulo_then_member", "outer_negation_then_inner",
        "concatenation_then_member", "group_join_inner_negation", "group_join_outer_arithmetic",
        "query_syntax_negation", "left_join_inner_negation", "inner_member", "outer_member_then_inner"
    ];

    [Theory]
    [MemberData(nameof(JoinShapes))]
    public Task over_joined_rows(string shape) => shape switch
    {
        "inner_negation" => Joined((c, o) =>
            c.Join(o, x => x.Id, y => y.CustomerId, (x, y) => new { x.Name, y.Amount }).OrderBy(r => -r.Amount)),
        "inner_negation_descending" => Joined((c, o) =>
            c.Join(o, x => x.Id, y => y.CustomerId, (x, y) => new { x.Name, y.Amount })
                .OrderByDescending(r => -r.Amount)),
        "inner_modulo_then_member" => Joined((c, o) =>
            c.Join(o, x => x.Id, y => y.CustomerId, (x, y) => new { x.Name, y.Amount })
                .OrderBy(r => r.Amount % 3).ThenBy(r => r.Amount)),
        "outer_negation_then_inner" => Joined((c, o) =>
            c.Join(o, x => x.Id, y => y.CustomerId, (x, y) => new { x.Rank, y.Amount })
                .OrderBy(r => -r.Rank).ThenBy(r => r.Amount)),
        "concatenation_then_member" => Joined((c, o) =>
                c.Join(o, x => x.Id, y => y.CustomerId, (x, y) => new { x.Name, y.Status, y.Amount })
                    .OrderBy(r => r.Status + "-").ThenByDescending(r => r.Amount),
            (c, o) => c.Join(o, x => x.Id, y => y.CustomerId, (x, y) => new { x.Name, y.Status, y.Amount })
                .OrderBy(r => r.Status + "-", StringComparer.Ordinal).ThenByDescending(r => r.Amount)),
        "group_join_inner_negation" => Joined((c, o) =>
            c.GroupJoin(o, x => x.Id, y => y.CustomerId, (x, ys) => new { x, ys })
                .SelectMany(z => z.ys, (z, y) => new { z.x.Name, y.Amount }).OrderBy(r => -r.Amount)),
        "group_join_outer_arithmetic" => Joined((c, o) =>
            c.GroupJoin(o, x => x.Id, y => y.CustomerId, (x, ys) => new { x, ys })
                .SelectMany(z => z.ys, (z, y) => new { z.x.Rank, y.Amount })
                .OrderBy(r => r.Rank * -3 + 1).ThenBy(r => r.Amount)),
        "query_syntax_negation" => Joined((c, o) =>
            from x in c
            join y in o on x.Id equals y.CustomerId
            orderby -y.Amount
            select new { x.Name, y.Amount }),
        "left_join_inner_negation" => Joined((c, o) =>
            c.LeftJoin(o, x => x.Id, y => y.CustomerId, (x, y) => new { x.Name, y!.Amount }).OrderBy(r => -r.Amount)),
        "inner_member" => Joined((c, o) =>
            c.Join(o, x => x.Id, y => y.CustomerId, (x, y) => new { x.Name, y.Amount }).OrderByDescending(r => r.Amount)),
        "outer_member_then_inner" => Joined((c, o) =>
                c.Join(o, x => x.Id, y => y.CustomerId, (x, y) => new { x.Name, y.Amount })
                    .OrderBy(r => r.Name).ThenBy(r => r.Amount),
            (c, o) => c.Join(o, x => x.Id, y => y.CustomerId, (x, y) => new { x.Name, y.Amount })
                .OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Amount)),
        _ => throw new ArgumentOutOfRangeException(nameof(shape))
    };

    public static TheoryData<string> RefusedShapes =>
    [
        "child_math_call", "child_conditional", "child_decimal_division", "child_long_arithmetic", "join_math_call",
        "join_narrowing", "join_long_arithmetic"
    ];

    [Theory]
    [MemberData(nameof(RefusedShapes))]
    public async Task untranslatable_keys_are_refused_by_name(string shape)
    {
        await using var session = _store.QuerySession();
        var parents = session.Query<KeyParent>();
        var customers = session.Query<KeyCustomer>();
        var orders = session.Query<KeyOrder>();

        Func<Task> query = shape switch
        {
            "child_math_call" => () => parents.SelectMany(x => x.Children).OrderBy(c => Math.Abs(c.Index - 7))
                .ToListAsync(Token),
            "child_conditional" => () => parents.SelectMany(x => x.Children).OrderBy(c => c.Index > 5 ? 0 : 1)
                .ToListAsync(Token),
            "child_decimal_division" => () => parents.SelectMany(x => x.Children).OrderBy(c => c.Weight / 3m)
                .ToListAsync(Token),
            "child_long_arithmetic" => () => parents.SelectMany(x => x.Children).OrderBy(c => (long)c.Index * -1)
                .ToListAsync(Token),
            "join_math_call" => () => customers
                .Join(orders, x => x.Id, y => y.CustomerId, (x, y) => new { x.Name, y.Amount })
                .OrderBy(r => Math.Abs(r.Amount - 25)).ToListAsync(Token),
            "join_narrowing" => () => customers
                .Join(orders, x => x.Id, y => y.CustomerId, (x, y) => new { x.Name, y.Amount })
                .OrderBy(r => (short)r.Amount).ToListAsync(Token),
            "join_long_arithmetic" => () => customers
                .Join(orders, x => x.Id, y => y.CustomerId, (x, y) => new { x.Name, y.Amount })
                .OrderBy(r => r.Amount * 2L).ToListAsync(Token),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(query);
        ex.Message.ShouldContain("Fisher cannot");
    }

    /// <summary>Plain member keys render the SQL they did before computed keys were translated.</summary>
    [Fact]
    public async Task sql_of_plain_member_keys_is_unchanged()
    {
        await using var session = _store.QuerySession();

        session.ToSql(session.Query<KeyCustomer>()
                .Join(session.Query<KeyOrder>(), x => x.Id, y => y.CustomerId, (x, y) => new { x.Name, y.Amount })
                .OrderBy(r => r.Name).ThenByDescending(r => r.Amount))
            .ShouldEndWith("order by json_extract(outer_t.data, '$.name'), json_extract(inner_t.data, '$.amount') desc");
    }
}
