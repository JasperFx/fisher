using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

public class Shipment
{
    public Guid Id { get; set; }
    public string Region { get; set; } = "";
    public string Carrier { get; set; } = "";
    public bool Late { get; set; }
    public int? Damaged { get; set; }
}

/// <summary>
///     fisher#490, from marten#5625. <c>g.Count(predicate)</c> and <c>g.LongCount()</c> used to reach
///     <c>Compile()</c> still naming the group, and surfaced an error about a variable the caller never
///     wrote.
/// </summary>
public class grouped_count_predicates : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("grouped-count");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<Shipment>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();

        foreach (var (region, carrier, late, damaged) in new (string, string, bool, int?)[]
                 {
                     ("north", "fast", true, null),
                     ("north", "fast", false, 2),
                     ("north", "slow", true, null),
                     ("south", "slow", false, null)
                 })
        {
            session.Store(new Shipment
            {
                Id = Guid.NewGuid(), Region = region, Carrier = carrier, Late = late, Damaged = damaged
            });
        }

        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task count_with_a_boolean_predicate()
    {
        await using var session = _store.QuerySession();

        var rows = await session.Query<Shipment>().GroupBy(x => x.Region)
            .Select(g => new { g.Key, Late = g.Count(x => x.Late) })
            .ToListAsync(Token);

        rows.Single(x => x.Key == "north").Late.ShouldBe(2);
        rows.Single(x => x.Key == "south").Late.ShouldBe(0);
    }

    /// <summary>What marten#5625 was: a predicate more than a bare member, rendered as one.</summary>
    [Fact]
    public async Task count_with_a_null_test_and_a_compound_predicate()
    {
        await using var session = _store.QuerySession();

        var rows = await session.Query<Shipment>().GroupBy(x => x.Region)
            .Select(g => new { g.Key, Clean = g.Count(x => x.Damaged == null), Both = g.Count(x => x.Late && x.Damaged == null) })
            .ToListAsync(Token);

        var north = rows.Single(x => x.Key == "north");
        north.Clean.ShouldBe(2);
        north.Both.ShouldBe(2);
    }

    /// <summary>
    ///     The value binds, and the select list's parameter lands ahead of the <c>WHERE</c>'s, which is
    ///     the order they have to reach the command in.
    /// </summary>
    [Fact]
    public async Task a_captured_value_binds_alongside_a_where()
    {
        var carrier = "fast";
        var excluded = "south";

        await using var session = _store.QuerySession();

        var query = session.Query<Shipment>().Where(x => x.Region != excluded).GroupBy(x => x.Region)
            .Select(g => new { g.Key, Fast = g.Count(x => x.Carrier == carrier) });

        session.ToSql(query).ShouldNotContain("fast");

        var row = (await query.ToListAsync(Token)).Single();

        row.Key.ShouldBe("north");
        row.Fast.ShouldBe(2);
    }

    [Fact]
    public async Task long_count_with_and_without_a_predicate()
    {
        await using var session = _store.QuerySession();

        var rows = await session.Query<Shipment>().GroupBy(x => x.Region)
            .Select(g => new { g.Key, All = g.LongCount(), Late = g.LongCount(x => x.Late) })
            .ToListAsync(Token);

        var north = rows.Single(x => x.Key == "north");
        north.All.ShouldBe(3L);
        north.Late.ShouldBe(2L);
    }

    [Fact]
    public async Task several_columns_of_different_kinds_stay_in_their_slots()
    {
        await using var session = _store.QuerySession();

        var row = (await session.Query<Shipment>().Where(x => x.Region == "north").GroupBy(x => x.Region)
                .Select(g => new
                {
                    Late = g.Count(x => x.Late), g.Key, All = g.Count(), Fast = g.Count(x => x.Carrier == "fast"),
                    Worst = g.Max(x => x.Damaged)
                })
                .ToListAsync(Token))
            .Single();

        row.Late.ShouldBe(2);
        row.Key.ShouldBe("north");
        row.All.ShouldBe(3);
        row.Fast.ShouldBe(2);
        row.Worst.ShouldBe(2);
    }

    [Fact]
    public async Task a_filtered_count_in_a_having()
    {
        await using var session = _store.QuerySession();

        var least = 1;
        var keys = await session.Query<Shipment>().GroupBy(x => x.Region)
            .Where(g => g.Count(x => x.Late) > least)
            .Select(g => g.Key)
            .ToListAsync(Token);

        keys.ShouldBe(["north"]);

        var flipped = await session.Query<Shipment>().GroupBy(x => x.Region)
            .Where(g => least >= g.Count(x => x.Late))
            .Select(g => g.Key)
            .ToListAsync(Token);

        flipped.ShouldBe(["south"]);
    }

    [Fact]
    public async Task counting_the_groups_of_a_filtered_projection()
    {
        await using var session = _store.QuerySession();

        var groups = await session.Query<Shipment>().GroupBy(x => x.Region)
            .Select(g => new { g.Key, Late = g.Count(x => x.Late) })
            .CountAsync(Token);

        groups.ShouldBe(2);
    }

    [Fact]
    public async Task ordering_by_a_filtered_count_is_refused_rather_than_ignored()
    {
        await using var session = _store.QuerySession();

        await Should.ThrowAsync<BadLinqExpressionException>(() => session.Query<Shipment>()
            .GroupBy(x => x.Region)
            .OrderBy(g => g.Count(x => x.Late))
            .Select(g => new { g.Key, Late = g.Count(x => x.Late) })
            .ToListAsync(Token));
    }

    [Fact]
    public async Task any_other_call_over_the_group_is_refused_by_name()
    {
        await using var session = _store.QuerySession();

        var first = await Should.ThrowAsync<BadLinqExpressionException>(() => session.Query<Shipment>()
            .GroupBy(x => x.Region).Select(g => new { g.Key, One = g.First() }).ToListAsync(Token));
        first.Message.ShouldContain("'First'");

        await Should.ThrowAsync<BadLinqExpressionException>(() => session.Query<Shipment>()
            .GroupBy(x => x.Region).Select(g => new { g.Key, Any = g.Any() }).ToListAsync(Token));

        var whole = await Should.ThrowAsync<BadLinqExpressionException>(() => session.Query<Shipment>()
            .GroupBy(x => x.Region).Select(g => new { g.Key, Group = g }).ToListAsync(Token));
        whole.Message.ShouldContain("The group itself");
    }
}
