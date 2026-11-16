using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

public enum ProbeColour
{
    Red,
    Green,
    Blue
}

public class ProbeChild
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
}

public class ProbeTarget
{
    public Guid Id { get; set; }
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public int[] Values { get; set; } = [];
    public int[][] Grid { get; set; } = [];
    public List<int> List { get; set; } = [];
    public string[] Names { get; set; } = [];
    public ProbeColour[] Colours { get; set; } = [];
    public List<ProbeChild> Children { get; set; } = [];
}

public class ProbeCustomer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Region { get; set; } = "";
}

public class ProbeOrder
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public int Quantity { get; set; }
    public string Sku { get; set; } = "";
}

public static class ProbeStatics
{
    public static int Value = 7;
    public static int[][] StaticArray = [[1, 2], [3, 4]];
    public static List<int> StaticList = [2, 9];
    public static int Number = 0;
    public static string Category = "same";
}

/// <summary>
///     Probes of the LINQ shapes Marten fixed on 2026-11-15 (marten#5910 – #5970), run against Fisher's own
///     provider with LINQ to Objects as the oracle. A shape passes when Fisher either answers correctly or
///     refuses it with a <see cref="BadLinqExpressionException" />; a silently wrong answer or any other
///     exception fails it. A probe that demonstrates a Fisher bug is skipped with the reason, and keeps
///     the correct expectation.
/// </summary>
/// <remarks>
///     Set <c>FISHER_PROBE_REPORT</c> to a file path to have every probe append its classification there.
/// </remarks>
public class marten_1115_probes : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("marten-1115");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly List<ProbeTarget> Targets =
    [
        new()
        {
            Id = Guid.NewGuid(), Number = 3, Name = "delta", Category = "b", Values = [3, 1, 2],
            Grid = [[1, 2], [3, 4]], List = [3, 1], Names = ["ab", "abcd"], Colours = [ProbeColour.Red],
            Children = [new() { Number = 30, Name = "d1" }, new() { Number = 31, Name = "d2" }]
        },
        new()
        {
            Id = Guid.NewGuid(), Number = 1, Name = "alpha", Category = "a", Values = [5, 0, 9],
            Grid = [[5, 6], [7, 8]], List = [5, 0], Names = ["xyzw"], Colours = [ProbeColour.Blue, ProbeColour.Green],
            Children = [new() { Number = 10, Name = "a1" }]
        },
        new()
        {
            Id = Guid.NewGuid(), Number = 5, Name = "echo", Category = "b", Values = [2, 8, 4],
            Grid = [[9, 1], [2, 3]], List = [2, 8], Names = ["e"], Colours = [],
            Children = [new() { Number = 50, Name = "e1" }]
        },
        new()
        {
            Id = Guid.NewGuid(), Number = 2, Name = "charlie", Category = "c", Values = [7, 6, 1],
            Grid = [[4, 4], [0, 0]], List = [7, 6], Names = ["cc", "c"], Colours = [ProbeColour.Green],
            Children = []
        },
        new()
        {
            Id = Guid.NewGuid(), Number = 4, Name = "bravo", Category = "a", Values = [1, 3, 5],
            Grid = [[6, 2], [4, 1]], List = [1, 3], Names = ["bbbb"], Colours = [ProbeColour.Red],
            Children = [new() { Number = 40, Name = "b1" }, new() { Number = 41, Name = "b2" }]
        }
    ];

    private static readonly List<ProbeCustomer> Customers =
    [
        new() { Id = Guid.NewGuid(), Name = "Ann", Region = "north" },
        new() { Id = Guid.NewGuid(), Name = "Bob", Region = "south" },
        new() { Id = Guid.NewGuid(), Name = "Cat", Region = "north" }
    ];

    private static readonly List<ProbeOrder> Orders =
    [
        new() { Id = Guid.NewGuid(), CustomerId = Customers[0].Id, Quantity = 5, Sku = "s1" },
        new() { Id = Guid.NewGuid(), CustomerId = Customers[0].Id, Quantity = 2, Sku = "s2" },
        new() { Id = Guid.NewGuid(), CustomerId = Customers[1].Id, Quantity = 9, Sku = "s3" },
        new() { Id = Guid.NewGuid(), CustomerId = Customers[1].Id, Quantity = 1, Sku = "s4" },
        new() { Id = Guid.NewGuid(), CustomerId = Customers[0].Id, Quantity = 7, Sku = "s5" }
    ];

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<ProbeTarget>();
            options.Schema.For<ProbeCustomer>();
            options.Schema.For<ProbeOrder>();
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

    private IDocumentSession Session() => _store.LightweightSession();

    // ---- the classifier ----

    private static void Report(string shape, string outcome)
    {
        var path = Environment.GetEnvironmentVariable("FISHER_PROBE_REPORT");
        if (string.IsNullOrEmpty(path)) return;
        lock (typeof(marten_1115_probes))
        {
            File.AppendAllText(path, $"{shape}\t{outcome.ReplaceLineEndings(" ")}\n");
        }
    }

    private static string Render<T>(IEnumerable<T> values) => "[" + string.Join(", ", values) + "]";

    /// <summary>Correct or refused by name passes; wrong or any other exception fails.</summary>
    private static async Task Probe<T>(string shape, Func<Task<IEnumerable<T>>> fisher, IEnumerable<T> expected,
        bool ordered = true)
    {
        string outcome;
        var failed = false;

        try
        {
            var actual = (await fisher()).ToList();
            var oracle = expected.ToList();
            var same = ordered
                ? actual.SequenceEqual(oracle)
                : actual.Select(x => x?.ToString()).Order().SequenceEqual(oracle.Select(x => x?.ToString()).Order());

            if (same)
            {
                outcome = "CORRECT";
            }
            else
            {
                outcome = $"WRONG-SILENTLY actual={Render(actual)} expected={Render(oracle)}";
                failed = true;
            }
        }
        catch (BadLinqExpressionException e)
        {
            outcome = "REFUSED-CLEARLY: " + e.Message;
        }
        catch (Exception e)
        {
            outcome = $"INTERNAL-EXCEPTION {e.GetType().FullName}: {e.Message}";
            failed = true;
        }

        Report(shape, outcome);

        if (failed)
        {
            throw new Shouldly.ShouldAssertException($"{shape}: {outcome}");
        }
    }

    private static Task ProbeScalar<T>(string shape, Func<Task<T>> fisher, T expected) =>
        Probe(shape, async () => (IEnumerable<T>)[await fisher()], [expected]);

    private static async Task<IEnumerable<T>> List<T>(IQueryable<T> queryable) =>
        await queryable.ToListAsync(Token);

    // =========================================================================================
    // marten#5957 — a computed OrderBy key on the document
    // =========================================================================================

    [Fact]
    public async Task order_by_a_negated_member()
    {
        await using var session = Session();
        await Probe("5957 OrderBy(x => -x.Number)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => -x.Number).Select(x => x.Name)),
            Targets.OrderBy(x => -x.Number).Select(x => x.Name));
    }

    [Fact]
    public async Task order_by_descending_a_negated_member()
    {
        await using var session = Session();
        await Probe("5957 OrderByDescending(x => -x.Number)",
            () => List(session.Query<ProbeTarget>().OrderByDescending(x => -x.Number).Select(x => x.Name)),
            Targets.OrderByDescending(x => -x.Number).Select(x => x.Name));
    }

    [Fact]
    public async Task order_by_a_modulo_then_by_name()
    {
        await using var session = Session();
        await Probe("5957 OrderBy(x => x.Number % 3).ThenBy(Name)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Number % 3).ThenBy(x => x.Name).Select(x => x.Name)),
            Targets.OrderBy(x => x.Number % 3).ThenBy(x => x.Name).Select(x => x.Name));
    }

    [Fact]
    public async Task then_by_a_negated_member()
    {
        await using var session = Session();
        await Probe("5957 OrderBy(Category).ThenBy(x => -x.Number)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Category).ThenBy(x => -x.Number).Select(x => x.Name)),
            Targets.OrderBy(x => x.Category).ThenBy(x => -x.Number).Select(x => x.Name));
    }

    [Fact]
    public async Task order_by_a_string_length()
    {
        await using var session = Session();
        await Probe("5957-neighbour OrderBy(x => x.Name.Length).ThenBy(Name)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Name.Length).ThenBy(x => x.Name).Select(x => x.Name)),
            Targets.OrderBy(x => x.Name.Length).ThenBy(x => x.Name).Select(x => x.Name));
    }

    [Fact]
    public async Task order_by_to_upper()
    {
        await using var session = Session();
        await Probe("5957-neighbour OrderBy(x => x.Name.ToUpper())",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Name.ToUpper()).Select(x => x.Name)),
            Targets.OrderBy(x => x.Name.ToUpper(), StringComparer.Ordinal).Select(x => x.Name));
    }

    [Fact]
    public async Task first_after_a_negated_ordering()
    {
        await using var session = Session();
        await Probe("5957-neighbour OrderBy(x => -x.Number).FirstAsync()",
            async () => [(await session.Query<ProbeTarget>().OrderBy(x => -x.Number).FirstAsync(Token))!.Name],
            [Targets.OrderBy(x => -x.Number).First().Name]);
    }

    // =========================================================================================
    // marten#5959 — a second OrderBy re-sorts; the earlier keys only break ties
    // =========================================================================================

    [Fact(Skip = "fisher: a second OrderBy is appended as a secondary key instead of becoming the primary one")]
    public async Task a_second_order_by_re_sorts()
    {
        await using var session = Session();
        await Probe("5959 OrderBy(Category).OrderBy(Name)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Category).OrderBy(x => x.Name).Select(x => x.Name)),
            Targets.OrderBy(x => x.Category).OrderBy(x => x.Name).Select(x => x.Name));
    }

    [Fact(Skip = "fisher: a second OrderBy is appended as a secondary key instead of becoming the primary one")]
    public async Task a_second_order_by_keeps_the_first_as_tie_breaker()
    {
        await using var session = Session();
        await Probe("5959 OrderBy(Name).OrderBy(Category)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Name).OrderBy(x => x.Category).Select(x => x.Name)),
            Targets.OrderBy(x => x.Name).OrderBy(x => x.Category).Select(x => x.Name));
    }

    [Fact(Skip = "fisher: a second OrderBy is appended as a secondary key instead of becoming the primary one")]
    public async Task a_second_order_by_descending_re_sorts()
    {
        await using var session = Session();
        await Probe("5959 OrderByDescending(Category).OrderBy(Number)",
            () => List(session.Query<ProbeTarget>().OrderByDescending(x => x.Category).OrderBy(x => x.Number)
                .Select(x => x.Name)),
            Targets.OrderByDescending(x => x.Category).OrderBy(x => x.Number).Select(x => x.Name));
    }

    [Fact(Skip = "fisher: a second OrderBy is appended as a secondary key instead of becoming the primary one")]
    public async Task order_then_then_by_then_order_by_again()
    {
        await using var session = Session();
        await Probe("5959 OrderBy(Category).ThenBy(Name).OrderByDescending(Number)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Category).ThenBy(x => x.Name)
                .OrderByDescending(x => x.Number).Select(x => x.Name)),
            Targets.OrderBy(x => x.Category).ThenBy(x => x.Name).OrderByDescending(x => x.Number).Select(x => x.Name));
    }

    [Fact(Skip = "fisher: a second OrderBy is appended as a secondary key instead of becoming the primary one")]
    public async Task first_after_a_second_order_by()
    {
        await using var session = Session();
        await Probe("5959 OrderBy(Category).OrderByDescending(Number).FirstAsync()",
            async () => [(await session.Query<ProbeTarget>().OrderBy(x => x.Category).OrderByDescending(x => x.Number)
                .FirstAsync(Token))!.Name],
            [Targets.OrderBy(x => x.Category).OrderByDescending(x => x.Number).First().Name]);
    }

    [Fact(Skip = "fisher: a second OrderBy is appended as a secondary key instead of becoming the primary one")]
    public async Task paging_after_a_second_order_by()
    {
        await using var session = Session();
        await Probe("5959 OrderBy(Name).OrderBy(Category).Take(3)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Name).OrderBy(x => x.Category).Take(3)
                .Select(x => x.Name)),
            Targets.OrderBy(x => x.Name).OrderBy(x => x.Category).Take(3).Select(x => x.Name));
    }

    // =========================================================================================
    // marten#5955 — OrderBy after a projection
    // =========================================================================================

    [Fact]
    public async Task select_a_member_then_order_by_it()
    {
        await using var session = Session();
        await Probe("5955 Select(x => x.Number).OrderBy(n => n)",
            () => List(session.Query<ProbeTarget>().Select(x => x.Number).OrderBy(n => n)),
            Targets.Select(x => x.Number).OrderBy(n => n));
    }

    [Fact]
    public async Task select_a_member_then_order_by_it_descending()
    {
        await using var session = Session();
        await Probe("5955 Select(x => x.Number).OrderByDescending(n => n)",
            () => List(session.Query<ProbeTarget>().Select(x => x.Number).OrderByDescending(n => n)),
            Targets.Select(x => x.Number).OrderByDescending(n => n));
    }

    [Fact]
    public async Task select_a_member_then_order_by_its_negation()
    {
        await using var session = Session();
        await Probe("5955 Select(x => x.Number).OrderBy(n => -n)",
            () => List(session.Query<ProbeTarget>().Select(x => x.Number).OrderBy(n => -n)),
            Targets.Select(x => x.Number).OrderBy(n => -n));
    }

    [Fact]
    public async Task select_then_order_then_take_then_first()
    {
        await using var session = Session();
        await Probe("5955 Select(x => x.Name).OrderBy(n => n).Take(2)",
            () => List(session.Query<ProbeTarget>().Select(x => x.Name).OrderBy(n => n).Take(2)),
            Targets.Select(x => x.Name).OrderBy(n => n).Take(2));

        await Probe("5955 Select(x => x.Number).OrderByDescending(n => n).FirstAsync()",
            async () => [await session.Query<ProbeTarget>().Select(x => x.Number).OrderByDescending(n => n).FirstAsync(Token)],
            [Targets.Select(x => x.Number).OrderByDescending(n => n).First()]);

        await Probe("5955 Select(x => x.Number).OrderBy(n => n).LastAsync()",
            async () => [await session.Query<ProbeTarget>().Select(x => x.Number).OrderBy(n => n).LastAsync(Token)],
            [Targets.Select(x => x.Number).OrderBy(n => n).Last()]);
    }

    [Fact(Skip = "fisher: an operator after a computed Select acts on the member the projection reads, not the value it computes")]
    public async Task select_a_computed_value_then_order_by_it()
    {
        await using var session = Session();
        await Probe("5955 Select(x => x.Number % 3).OrderBy(n => n)",
            () => List(session.Query<ProbeTarget>().Select(x => x.Number % 3).OrderBy(n => n)),
            Targets.Select(x => x.Number % 3).OrderBy(n => n));

        await Probe("5955 Select(x => -x.Number).OrderBy(n => n)",
            () => List(session.Query<ProbeTarget>().Select(x => -x.Number).OrderBy(n => n)),
            Targets.Select(x => -x.Number).OrderBy(n => n));
    }

    [Fact(Skip = "fisher: a second OrderBy is appended as a secondary key instead of becoming the primary one")]
    public async Task order_by_member_then_select_then_order_by_value()
    {
        await using var session = Session();
        // An OrderBy before the Select and another after it: LINQ re-sorts by the value, the first key only
        // breaking ties.
        await Probe("5955+5959 OrderBy(Name).Select(x => x.Category).OrderBy(c => c)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Name).Select(x => x.Category).OrderBy(c => c)),
            Targets.OrderBy(x => x.Name).Select(x => x.Category).OrderBy(c => c));
    }

    // =========================================================================================
    // marten#5950 — OrderBy over a closed value
    // =========================================================================================

    [Fact]
    public async Task order_by_a_static_member_then_by_number()
    {
        await using var session = Session();
        await Probe("5950 OrderBy(x => ProbeStatics.Value).ThenBy(Number)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => ProbeStatics.Value).ThenBy(x => x.Number)
                .Select(x => x.Number)),
            Targets.OrderBy(x => ProbeStatics.Value).ThenBy(x => x.Number).Select(x => x.Number));
    }

    [Fact]
    public async Task order_by_a_constant_then_by_number()
    {
        await using var session = Session();
        await Probe("5950 OrderBy(x => 1).ThenBy(Number)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => 1).ThenBy(x => x.Number).Select(x => x.Number)),
            Targets.OrderBy(x => 1).ThenBy(x => x.Number).Select(x => x.Number));
    }

    [Fact]
    public async Task order_by_a_captured_local_then_by_number()
    {
        await using var session = Session();
        var key = "k";
        await Probe("5950 OrderBy(x => local).ThenBy(Number)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => key).ThenBy(x => x.Number).Select(x => x.Number)),
            Targets.OrderBy(x => key).ThenBy(x => x.Number).Select(x => x.Number));
    }

    // =========================================================================================
    // marten#5949 / #5943 — indexing arrays
    // =========================================================================================

    [Fact]
    public async Task order_by_an_array_element()
    {
        await using var session = Session();
        await Probe("5949 OrderBy(x => x.Values[1])",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Values[1]).Select(x => x.Name)),
            Targets.OrderBy(x => x.Values[1]).Select(x => x.Name));
    }

    [Fact]
    public async Task order_by_a_list_element()
    {
        await using var session = Session();
        await Probe("5949 OrderBy(x => x.List[1])",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.List[1]).Select(x => x.Name)),
            Targets.OrderBy(x => x.List[1]).Select(x => x.Name));
    }

    [Fact]
    public async Task select_an_array_element()
    {
        await using var session = Session();
        await Probe("5949 Select(x => x.Values[1])",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Number).Select(x => x.Values[1])),
            Targets.OrderBy(x => x.Number).Select(x => x.Values[1]));
    }

    [Fact]
    public async Task select_a_jagged_array_element()
    {
        await using var session = Session();
        await Probe("5949 Select(x => x.Grid[1][0])",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Number).Select(x => x.Grid[1][0])),
            Targets.OrderBy(x => x.Number).Select(x => x.Grid[1][0]));
    }

    [Fact]
    public async Task where_on_an_array_element()
    {
        await using var session = Session();
        await Probe("5949 Where(x => x.Values[1] == 1)",
            () => List(session.Query<ProbeTarget>().Where(x => x.Values[1] == 1).Select(x => x.Name)),
            Targets.Where(x => x.Values[1] == 1).Select(x => x.Name), ordered: false);

        await Probe("5949 Where(x => x.List[1] == 0)",
            () => List(session.Query<ProbeTarget>().Where(x => x.List[1] == 0).Select(x => x.Name)),
            Targets.Where(x => x.List[1] == 0).Select(x => x.Name), ordered: false);
    }

    [Fact]
    public async Task where_on_a_jagged_array_element()
    {
        await using var session = Session();
        await Probe("5949 Where(x => x.Grid[1][0] == 7)",
            () => List(session.Query<ProbeTarget>().Where(x => x.Grid[1][0] == 7).Select(x => x.Name)),
            Targets.Where(x => x.Grid[1][0] == 7).Select(x => x.Name), ordered: false);
    }

    [Fact]
    public async Task where_against_a_static_jagged_array()
    {
        await using var session = Session();
        await Probe("5943 Where(x => x.Number != ProbeStatics.StaticArray[1][0])",
            () => List(session.Query<ProbeTarget>().Where(x => x.Number != ProbeStatics.StaticArray[1][0])
                .Select(x => x.Name)),
            Targets.Where(x => x.Number != ProbeStatics.StaticArray[1][0]).Select(x => x.Name), ordered: false);
    }

    [Fact]
    public async Task where_against_a_static_list_element()
    {
        await using var session = Session();
        await Probe("5943 Where(x => x.Number == ProbeStatics.StaticList[0])",
            () => List(session.Query<ProbeTarget>().Where(x => x.Number == ProbeStatics.StaticList[0])
                .Select(x => x.Name)),
            Targets.Where(x => x.Number == ProbeStatics.StaticList[0]).Select(x => x.Name), ordered: false);
    }

    [Fact(Skip = "fisher: arithmetic over a closed value in Where compiles the member side and throws an internal InvalidOperationException")]
    public async Task where_against_a_static_member()
    {
        await using var session = Session();
        await Probe("5943 Where(x => x.Number < ProbeStatics.Value - 4)",
            () => List(session.Query<ProbeTarget>().Where(x => x.Number < ProbeStatics.Value - 4).Select(x => x.Name)),
            Targets.Where(x => x.Number < ProbeStatics.Value - 4).Select(x => x.Name), ordered: false);
    }

    [Fact]
    public async Task select_with_a_static_member()
    {
        await using var session = Session();
        await Probe("5943 Select(x => x.Number + ProbeStatics.StaticArray[0][1])",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Number)
                .Select(x => x.Number + ProbeStatics.StaticArray[0][1])),
            Targets.OrderBy(x => x.Number).Select(x => x.Number + ProbeStatics.StaticArray[0][1]));
    }

    // =========================================================================================
    // marten#5942 — computed top-level Select
    // =========================================================================================

    [Fact]
    public async Task select_a_modulo()
    {
        await using var session = Session();
        await Probe("5942 Select(x => x.Number % 3)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Number).Select(x => x.Number % 3)),
            Targets.OrderBy(x => x.Number).Select(x => x.Number % 3));
    }

    [Fact]
    public async Task select_id_to_string()
    {
        await using var session = Session();
        await Probe("5942 Select(x => x.Id.ToString())",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Number).Select(x => x.Id.ToString())),
            Targets.OrderBy(x => x.Number).Select(x => x.Id.ToString()));
    }

    [Fact]
    public async Task select_number_to_string_and_negation_and_length()
    {
        await using var session = Session();
        await Probe("5942 Select(x => x.Number.ToString())",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Number).Select(x => x.Number.ToString())),
            Targets.OrderBy(x => x.Number).Select(x => x.Number.ToString()));

        await Probe("5942 Select(x => -x.Number)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Number).Select(x => -x.Number)),
            Targets.OrderBy(x => x.Number).Select(x => -x.Number));

        await Probe("5942 Select(x => x.Name.Length)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Number).Select(x => x.Name.Length)),
            Targets.OrderBy(x => x.Number).Select(x => x.Name.Length));

        await Probe("5942 Select(x => new { x.Name, Mod = x.Number % 2 })",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Number).Select(x => new { x.Name, Mod = x.Number % 2 })),
            Targets.OrderBy(x => x.Number).Select(x => new { x.Name, Mod = x.Number % 2 }));
    }

    // =========================================================================================
    // marten#5921 / #5928 / #5933 / #5934 / #5940 / #5941 — SelectMany outside a GroupJoin
    // =========================================================================================

    [Fact]
    public async Task select_many_over_a_value_collection()
    {
        await using var session = Session();
        await Probe("5933 SelectMany(x => x.Values).Select(v => v % 3)",
            () => List(session.Query<ProbeTarget>().SelectMany(x => x.Values).Select(v => v % 3)),
            Targets.SelectMany(x => x.Values).Select(v => v % 3), ordered: false);

        await Probe("5955 SelectMany(x => x.Values).OrderBy(v => -v)",
            () => List(session.Query<ProbeTarget>().SelectMany(x => x.Values).OrderBy(v => -v)),
            Targets.SelectMany(x => x.Values).OrderBy(v => -v));

        await Probe("5921 SelectMany(x => x.Values).FirstAsync()",
            async () => [await session.Query<ProbeTarget>().OrderBy(x => x.Number).SelectMany(x => x.Values).FirstAsync(Token)],
            [Targets.OrderBy(x => x.Number).SelectMany(x => x.Values).First()]);
    }

    [Fact]
    public async Task select_many_over_strings_enums_and_children()
    {
        await using var session = Session();
        await Probe("5941 SelectMany(x => x.Names).Where(n => n.Length == 4)",
            () => List(session.Query<ProbeTarget>().SelectMany(x => x.Names).Where(n => n.Length == 4)),
            Targets.SelectMany(x => x.Names).Where(n => n.Length == 4), ordered: false);

        await Probe("5940 SelectMany(x => x.Colours)",
            () => List(session.Query<ProbeTarget>().SelectMany(x => x.Colours)),
            Targets.SelectMany(x => x.Colours), ordered: false);

        await Probe("5928 OrderBy(Number).Take(3).SelectMany(x => x.Children)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Number).Take(3).SelectMany(x => x.Children)
                .Select(c => c.Name)),
            Targets.OrderBy(x => x.Number).Take(3).SelectMany(x => x.Children).Select(c => c.Name), ordered: false);
    }

    // =========================================================================================
    // marten#5928 neighbours — operators applied AFTER paging. LINQ applies each to the page; a
    // provider that folds them into one statement applies them before the LIMIT.
    // =========================================================================================

    [Fact(Skip = "fisher: an operator after Take/Skip is folded into the same statement, so it applies before the LIMIT/OFFSET instead of to the page")]
    public async Task where_after_take()
    {
        await using var session = Session();
        await Probe("5928-neighbour OrderBy(Number).Take(3).Where(Number > 1)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Number).Take(3).Where(x => x.Number > 1)
                .Select(x => x.Number)),
            Targets.OrderBy(x => x.Number).Take(3).Where(x => x.Number > 1).Select(x => x.Number));
    }

    [Fact(Skip = "fisher: an operator after Take/Skip is folded into the same statement, so it applies before the LIMIT/OFFSET instead of to the page")]
    public async Task take_then_take_and_skip_then_skip()
    {
        await using var session = Session();
        await Probe("5928-neighbour OrderBy(Number).Take(2).Take(4)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Number).Take(2).Take(4).Select(x => x.Number)),
            Targets.OrderBy(x => x.Number).Take(2).Take(4).Select(x => x.Number));

        await Probe("5928-neighbour OrderBy(Number).Skip(1).Skip(1)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Number).Skip(1).Skip(1).Select(x => x.Number)),
            Targets.OrderBy(x => x.Number).Skip(1).Skip(1).Select(x => x.Number));
    }

    [Fact(Skip = "fisher: an operator after Take/Skip is folded into the same statement, so it applies before the LIMIT/OFFSET instead of to the page")]
    public async Task take_then_skip()
    {
        await using var session = Session();
        await Probe("5928-neighbour OrderBy(Number).Take(3).Skip(2)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Number).Take(3).Skip(2).Select(x => x.Number)),
            Targets.OrderBy(x => x.Number).Take(3).Skip(2).Select(x => x.Number));
    }

    [Fact(Skip = "fisher: an operator after Take/Skip is folded into the same statement, so it applies before the LIMIT/OFFSET instead of to the page")]
    public async Task order_by_after_take()
    {
        await using var session = Session();
        await Probe("5928-neighbour OrderBy(Number).Take(3).OrderByDescending(Name)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => x.Number).Take(3).OrderByDescending(x => x.Name)
                .Select(x => x.Name)),
            Targets.OrderBy(x => x.Number).Take(3).OrderByDescending(x => x.Name).Select(x => x.Name));
    }

    [Fact(Skip = "fisher: an operator after Take/Skip is folded into the same statement, so it applies before the LIMIT/OFFSET instead of to the page")]
    public async Task distinct_after_take()
    {
        await using var session = Session();
        await Probe("5928-neighbour OrderByDescending(Number).Select(Category).Take(3).Distinct()",
            () => List(session.Query<ProbeTarget>().OrderByDescending(x => x.Number).Select(x => x.Category).Take(3)
                .Distinct()),
            Targets.OrderByDescending(x => x.Number).Select(x => x.Category).Take(3).Distinct(), ordered: false);
    }

    // =========================================================================================
    // marten#5920 — counting a page
    // =========================================================================================

    [Fact]
    public async Task counting_a_page()
    {
        await using var session = Session();
        var q = session.Query<ProbeTarget>().OrderBy(x => x.Number);
        var o = Targets.OrderBy(x => x.Number);

        await ProbeScalar("5920 Take(2).CountAsync()", () => q.Take(2).CountAsync(Token), o.Take(2).Count());
        await ProbeScalar("5920 Skip(1).Take(2).CountAsync()", () => q.Skip(1).Take(2).CountAsync(Token), o.Skip(1).Take(2).Count());
        await ProbeScalar("5920 Skip(4).CountAsync()", () => q.Skip(4).CountAsync(Token), o.Skip(4).Count());
        await ProbeScalar("5920 Skip(3).Take(5).CountAsync()", () => q.Skip(3).Take(5).CountAsync(Token), o.Skip(3).Take(5).Count());
        await ProbeScalar("5920 Take(2).LongCountAsync()", () => q.Take(2).LongCountAsync(Token), o.Take(2).LongCount());
        await ProbeScalar("5920 Skip(10).AnyAsync()", () => q.Skip(10).AnyAsync(Token), o.Skip(10).Any());
        await ProbeScalar("5920 Skip(4).AnyAsync()", () => q.Skip(4).AnyAsync(Token), o.Skip(4).Any());
        await ProbeScalar("5920 Skip(1).Take(2).SumAsync(Number)", () => q.Skip(1).Take(2).SumAsync(x => x.Number, Token),
            o.Skip(1).Take(2).Sum(x => x.Number));
        await ProbeScalar("5920 Select(Category).Distinct().Take(2).CountAsync()",
            () => session.Query<ProbeTarget>().Select(x => x.Category).Distinct().Take(2).CountAsync(Token),
            Targets.Select(x => x.Category).Distinct().Take(2).Count());
    }

    [Fact(Skip = "fisher: an operator after Take/Skip is folded into the same statement, so it applies before the LIMIT/OFFSET instead of to the page")]
    public async Task a_terminal_predicate_after_a_page()
    {
        await using var session = Session();
        var q = session.Query<ProbeTarget>().OrderBy(x => x.Number);
        var o = Targets.OrderBy(x => x.Number);

        await ProbeScalar("5920-neighbour Take(2).CountAsync(x => x.Number > 1)",
            () => q.Take(2).CountAsync(x => x.Number > 1, Token), o.Take(2).Count(x => x.Number > 1));

        await ProbeScalar("5920-neighbour Take(2).AnyAsync(x => x.Number > 2)",
            () => q.Take(2).AnyAsync(x => x.Number > 2, Token), o.Take(2).Any(x => x.Number > 2));

        await ProbeScalar("5920-neighbour Take(2).FirstOrDefaultAsync(x => x.Number > 2)",
            async () => (await q.Take(2).FirstOrDefaultAsync(x => x.Number > 2, Token))?.Name,
            o.Take(2).FirstOrDefault(x => x.Number > 2)?.Name);
    }

    // =========================================================================================
    // marten#5910 / #5917 / #5968 / #5916 — joins
    // =========================================================================================

    private record Row(string Name, int Quantity);

    private IEnumerable<Row> OracleJoin() =>
        Customers.Join(Orders, c => c.Id, o => o.CustomerId, (c, o) => new Row(c.Name, o.Quantity));

    [Fact]
    public async Task join_then_order_by_a_negated_key()
    {
        await using var session = Session();
        await Probe("5968 Join(...).OrderBy(x => -x.Quantity)",
            () => List(session.Query<ProbeCustomer>()
                .Join(session.Query<ProbeOrder>(), c => c.Id, o => o.CustomerId, (c, o) => new Row(c.Name, o.Quantity))
                .OrderBy(x => -x.Quantity)),
            OracleJoin().OrderBy(x => -x.Quantity));

        await Probe("5968 GroupJoin/SelectMany OrderBy(x => x.Quantity % 3).ThenBy(Quantity)",
            () => List(session.Query<ProbeCustomer>()
                .GroupJoin(session.Query<ProbeOrder>(), c => c.Id, o => o.CustomerId, (c, os) => new { c, os })
                .SelectMany(x => x.os, (x, o) => new Row(x.c.Name, o.Quantity))
                .OrderBy(x => x.Quantity % 3).ThenBy(x => x.Quantity)),
            OracleJoin().OrderBy(x => x.Quantity % 3).ThenBy(x => x.Quantity));
    }

    [Fact]
    public async Task join_then_order_by_a_key()
    {
        await using var session = Session();
        await Probe("5968-control Join(...).OrderByDescending(x => x.Quantity)",
            () => List(session.Query<ProbeCustomer>()
                .Join(session.Query<ProbeOrder>(), c => c.Id, o => o.CustomerId, (c, o) => new Row(c.Name, o.Quantity))
                .OrderByDescending(x => x.Quantity)),
            OracleJoin().OrderByDescending(x => x.Quantity));
    }

    [Fact(Skip = "fisher: a second OrderBy is appended as a secondary key instead of becoming the primary one")]
    public async Task join_then_a_second_order_by()
    {
        await using var session = Session();
        await Probe("5959-join Join(...).OrderBy(Name).OrderBy(Quantity)",
            () => List(session.Query<ProbeCustomer>()
                .Join(session.Query<ProbeOrder>(), c => c.Id, o => o.CustomerId, (c, o) => new Row(c.Name, o.Quantity))
                .OrderBy(x => x.Name).OrderBy(x => x.Quantity)),
            OracleJoin().OrderBy(x => x.Name).OrderBy(x => x.Quantity));
    }

    [Fact]
    public async Task join_any_and_count_honour_the_inner_side()
    {
        await using var session = Session();

        IQueryable<Row> Joined() => session.Query<ProbeCustomer>()
            .GroupJoin(session.Query<ProbeOrder>(), c => c.Id, o => o.CustomerId, (c, os) => new { c, os })
            .SelectMany(x => x.os, (x, o) => new Row(x.c.Name, o.Quantity));

        await ProbeScalar("5917 GroupJoin/SelectMany.Where(Quantity > 1000).AnyAsync()",
            () => Joined().Where(x => x.Quantity > 1000).AnyAsync(Token), OracleJoin().Any(x => x.Quantity > 1000));

        await ProbeScalar("5917 GroupJoin/SelectMany.Where(Quantity > 4).CountAsync()",
            () => Joined().Where(x => x.Quantity > 4).CountAsync(Token), OracleJoin().Count(x => x.Quantity > 4));

        await ProbeScalar("5917 GroupJoin/SelectMany.CountAsync()",
            () => Joined().CountAsync(Token), OracleJoin().Count());

        await ProbeScalar("5917 GroupJoin/SelectMany.AnyAsync() (Cat has no orders, inner only)",
            () => session.Query<ProbeCustomer>().Where(c => c.Name == "Cat")
                .GroupJoin(session.Query<ProbeOrder>(), c => c.Id, o => o.CustomerId, (c, os) => new { c, os })
                .SelectMany(x => x.os, (x, o) => new Row(x.c.Name, o.Quantity)).AnyAsync(Token),
            false);

        await ProbeScalar("5920-join OrderBy(Quantity).Take(2).CountAsync()",
            () => Joined().OrderBy(x => x.Quantity).Take(2).CountAsync(Token), OracleJoin().OrderBy(x => x.Quantity).Take(2).Count());

        await ProbeScalar("5920-join OrderBy(Quantity).Skip(3).Take(5).CountAsync()",
            () => Joined().OrderBy(x => x.Quantity).Skip(3).Take(5).CountAsync(Token),
            OracleJoin().OrderBy(x => x.Quantity).Skip(3).Take(5).Count());

        await ProbeScalar("5921-join OrderBy(Quantity).FirstAsync()",
            async () => (await Joined().OrderBy(x => x.Quantity).FirstAsync(Token))!.Quantity,
            OracleJoin().OrderBy(x => x.Quantity).First().Quantity);

        await ProbeScalar("5920-join OrderBy(Quantity).Skip(1).Take(2).SumAsync(Quantity)",
            () => Joined().OrderBy(x => x.Quantity).Skip(1).Take(2).SumAsync(x => x.Quantity, Token),
            OracleJoin().OrderBy(x => x.Quantity).Skip(1).Take(2).Sum(x => x.Quantity));
    }

    [Fact]
    public async Task left_join_operator()
    {
        await using var session = Session();
        await Probe("5910 Queryable.LeftJoin(...)",
            () => List(session.Query<ProbeCustomer>()
                .LeftJoin(session.Query<ProbeOrder>(), c => c.Id, o => o.CustomerId,
                    (c, o) => new { c.Name, Sku = o == null ? null : o.Sku })),
            Customers.LeftJoin(Orders, c => c.Id, o => o.CustomerId,
                (c, o) => new { c.Name, Sku = o == null ? null : o.Sku }), ordered: false);
    }

    [Fact]
    public async Task join_where_on_the_inner_side_then_left_join_keeps_unmatched()
    {
        await using var session = Session();
        await Probe("5910 GroupJoin+DefaultIfEmpty left join",
            () => List(session.Query<ProbeCustomer>()
                .GroupJoin(session.Query<ProbeOrder>(), c => c.Id, o => o.CustomerId, (c, os) => new { c, os })
                .SelectMany(x => x.os.DefaultIfEmpty(), (x, o) => new { x.c.Name, Sku = o == null ? null : o.Sku })),
            Customers.GroupJoin(Orders, c => c.Id, o => o.CustomerId, (c, os) => new { c, os })
                .SelectMany(x => x.os.DefaultIfEmpty(), (x, o) => new { x.c.Name, Sku = o == null ? null : o.Sku }),
            ordered: false);
    }

    // =========================================================================================
    // marten#5950 / #5943 neighbours — a closed value whose NAME matches a document member. The
    // ordering, aggregate, DistinctBy and GroupBy paths resolve any member access as a JSON path
    // without checking it is rooted at the lambda's parameter.
    // =========================================================================================

    [Fact(Skip = "fisher: a closed value (static/captured local) is resolved as a JSON path named after the member, not as a constant")]
    public async Task order_by_a_static_named_like_a_member()
    {
        await using var session = Session();
        await Probe("5950-neighbour OrderBy(x => ProbeStatics.Number).ThenBy(Name)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => ProbeStatics.Number).ThenBy(x => x.Name).Select(x => x.Name)),
            Targets.OrderBy(x => ProbeStatics.Number).ThenBy(x => x.Name).Select(x => x.Name));
    }

    [Fact(Skip = "fisher: a closed value (static/captured local) is resolved as a JSON path named after the member, not as a constant")]
    public async Task order_by_a_local_named_like_a_member()
    {
        await using var session = Session();
        var number = 0;
        await Probe("5950-neighbour OrderBy(x => number /* local */).ThenBy(Name)",
            () => List(session.Query<ProbeTarget>().OrderBy(x => number).ThenBy(x => x.Name).Select(x => x.Name)),
            Targets.OrderBy(x => number).ThenBy(x => x.Name).Select(x => x.Name));
    }

    [Fact(Skip = "fisher: a closed value (static/captured local) is resolved as a JSON path named after the member, not as a constant")]
    public async Task sum_of_a_static_named_like_a_member()
    {
        await using var session = Session();
        await ProbeScalar("5950-neighbour SumAsync(x => ProbeStatics.Number)",
            () => session.Query<ProbeTarget>().SumAsync(x => ProbeStatics.Number, Token),
            Targets.Sum(x => ProbeStatics.Number));
    }

    [Fact(Skip = "fisher: a closed value (static/captured local) is resolved as a JSON path named after the member, not as a constant")]
    public async Task distinct_by_a_static_named_like_a_member()
    {
        await using var session = Session();
        await ProbeScalar("5950-neighbour DistinctBy(x => ProbeStatics.Category).ToList().Count",
            async () => (await session.Query<ProbeTarget>().DistinctBy(x => ProbeStatics.Category).ToListAsync(Token)).Count,
            Targets.DistinctBy(x => ProbeStatics.Category).Count());
    }

    [Fact(Skip = "fisher: a closed value (static/captured local) is resolved as a JSON path named after the member, not as a constant")]
    public async Task group_by_a_static_named_like_a_member()
    {
        await using var session = Session();
        await Probe("5950-neighbour GroupBy(x => ProbeStatics.Category).Select(g => g.Count())",
            () => List(session.Query<ProbeTarget>().GroupBy(x => ProbeStatics.Category).Select(g => g.Count())),
            Targets.GroupBy(x => ProbeStatics.Category).Select(g => g.Count()), ordered: false);
    }

    [Fact(Skip = "fisher: arithmetic over a closed value in Where compiles the member side and throws an internal InvalidOperationException")]
    public async Task where_against_a_local_in_arithmetic()
    {
        await using var session = Session();
        var seven = 7;
        await Probe("5943 Where(x => x.Number < local - 4)",
            () => List(session.Query<ProbeTarget>().Where(x => x.Number < seven - 4).Select(x => x.Name)),
            Targets.Where(x => x.Number < seven - 4).Select(x => x.Name), ordered: false);
    }

    [Fact]
    public async Task where_against_a_bare_static()
    {
        await using var session = Session();
        await Probe("5943 Where(x => x.Number < ProbeStatics.Value)",
            () => List(session.Query<ProbeTarget>().Where(x => x.Number < ProbeStatics.Value).Select(x => x.Name)),
            Targets.Where(x => x.Number < ProbeStatics.Value).Select(x => x.Name), ordered: false);
    }

    // =========================================================================================
    // marten#5955 / #5957 neighbours — a COMPUTED Select followed by an operator that works on the
    // selected column. Fisher's select list holds the members the projection reads, not the value it
    // computes, so the operator acts on the member.
    // =========================================================================================

    [Fact(Skip = "fisher: an operator after a computed Select acts on the member the projection reads, not the value it computes")]
    public async Task order_a_negated_projection()
    {
        await using var session = Session();
        await Probe("5955 Select(x => -x.Number).OrderBy(n => n)",
            () => List(session.Query<ProbeTarget>().Select(x => -x.Number).OrderBy(n => n)),
            Targets.Select(x => -x.Number).OrderBy(n => n));
    }

    [Fact(Skip = "fisher: an operator after a computed Select acts on the member the projection reads, not the value it computes")]
    public async Task order_a_modulo_projection()
    {
        await using var session = Session();
        await Probe("5955 Select(x => x.Number % 3).OrderBy(n => n)",
            () => List(session.Query<ProbeTarget>().Select(x => x.Number % 3).OrderBy(n => n)),
            Targets.Select(x => x.Number % 3).OrderBy(n => n));
    }

    [Fact(Skip = "fisher: an operator after a computed Select acts on the member the projection reads, not the value it computes")]
    public async Task first_of_an_ordered_computed_projection()
    {
        await using var session = Session();
        await ProbeScalar("5955 Select(x => x.Number * -1).OrderBy(n => n).FirstAsync()",
            () => session.Query<ProbeTarget>().Select(x => x.Number * -1).OrderBy(n => n).FirstAsync(Token),
            Targets.Select(x => x.Number * -1).OrderBy(n => n).First());
    }

    [Fact]
    public async Task order_a_length_projection()
    {
        await using var session = Session();
        await Probe("5955 Select(x => x.Name.Length).OrderBy(n => n)",
            () => List(session.Query<ProbeTarget>().Select(x => x.Name.Length).OrderBy(n => n)),
            Targets.Select(x => x.Name.Length).OrderBy(n => n));
    }

    [Fact(Skip = "fisher: an operator after a computed Select acts on the member the projection reads, not the value it computes")]
    public async Task distinct_over_a_computed_projection()
    {
        await using var session = Session();
        await Probe("5933-neighbour Select(x => x.Number % 3).Distinct()",
            () => List(session.Query<ProbeTarget>().Select(x => x.Number % 3).Distinct()),
            Targets.Select(x => x.Number % 3).Distinct(), ordered: false);
    }

    [Fact(Skip = "fisher: an operator after a computed Select acts on the member the projection reads, not the value it computes")]
    public async Task count_of_a_distinct_computed_projection()
    {
        await using var session = Session();
        await ProbeScalar("5933-neighbour Select(x => x.Number % 3).Distinct().CountAsync()",
            () => session.Query<ProbeTarget>().Select(x => x.Number % 3).Distinct().CountAsync(Token),
            Targets.Select(x => x.Number % 3).Distinct().Count());
    }

    [Fact(Skip = "fisher: an operator after a computed Select acts on the member the projection reads, not the value it computes")]
    public async Task distinct_over_a_computed_anonymous_projection()
    {
        await using var session = Session();
        await Probe("5933-neighbour Select(x => new { M = x.Number % 2 }).Distinct()",
            () => List(session.Query<ProbeTarget>().Select(x => new { M = x.Number % 2 }).Distinct()),
            Targets.Select(x => new { M = x.Number % 2 }).Distinct(), ordered: false);
    }

    [Fact]
    public async Task distinct_over_a_string_length_projection()
    {
        await using var session = Session();
        await Probe("5933-neighbour Select(x => x.Name.Length).Distinct()",
            () => List(session.Query<ProbeTarget>().Select(x => x.Name.Length).Distinct()),
            Targets.Select(x => x.Name.Length).Distinct(), ordered: false);
    }

    [Fact(Skip = "fisher: an operator after a computed Select acts on the member the projection reads, not the value it computes")]
    public async Task order_a_computed_group_projection()
    {
        await using var session = Session();
        await Probe("5955-group GroupBy(Category).Select(g => -g.Count()).OrderBy(n => n)",
            () => List(session.Query<ProbeTarget>().GroupBy(x => x.Category).Select(g => -g.Count()).OrderBy(n => n)),
            Targets.GroupBy(x => x.Category).Select(g => -g.Count()).OrderBy(n => n));
    }

    [Fact(Skip = "fisher: an operator after a computed Select acts on the member the projection reads, not the value it computes")]
    public async Task distinct_over_a_computed_group_projection()
    {
        await using var session = Session();
        await Probe("5933-group GroupBy(Category).Select(g => g.Count() / 3).Distinct()",
            () => List(session.Query<ProbeTarget>().GroupBy(x => x.Category).Select(g => g.Count() / 3).Distinct()),
            Targets.GroupBy(x => x.Category).Select(g => g.Count() / 3).Distinct(), ordered: false);
    }
}
