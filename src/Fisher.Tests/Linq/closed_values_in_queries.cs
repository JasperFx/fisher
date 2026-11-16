using Fisher.Linq;
using JasperFx;
using Shouldly;

namespace Fisher.Tests.Linq;

public class ClosedValueTarget
{
    public Guid Id { get; set; }
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
}

/// <summary>
///     Statics whose names collide with <see cref="ClosedValueTarget" />'s members on purpose: a static
///     walked as a JSON path reads <c>$.number</c> and <c>$.category</c>, which every document has.
/// </summary>
public static class ClosedValueStatics
{
    public static int Number = 0;
    public static string Category = "same";
    public static int Value = 7;
}

/// <summary>
///     fisher#591 (the neighbour of marten#5950 / marten#5943). A static member or a captured local in a
///     query is a value, the same for every row. It used to be resolved as a JSON path named after it, so
///     where the name matched a document member the query read that member, silently. LINQ to Objects is
///     the oracle; a shape Fisher refuses has to be refused by name.
/// </summary>
public class closed_values_in_queries : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("closed-values");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Number is deliberately NOT in Name order, overall or within category "a", so ordering by $.number
    // instead of dropping the key is visible in the result.
    private static readonly List<ClosedValueTarget> Targets =
    [
        new() { Id = Guid.NewGuid(), Number = 3, Name = "delta", Category = "b" },
        new() { Id = Guid.NewGuid(), Number = 4, Name = "echo", Category = "a" },
        new() { Id = Guid.NewGuid(), Number = 5, Name = "alpha", Category = "b" },
        new() { Id = Guid.NewGuid(), Number = 2, Name = "charlie", Category = "c" },
        new() { Id = Guid.NewGuid(), Number = 1, Name = "bravo", Category = "a" }
    ];

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<ClosedValueTarget>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        foreach (var target in Targets) session.Store(target);
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    private IQueryable<ClosedValueTarget> Query(IDocumentSession session) => session.Query<ClosedValueTarget>();

    // ---- ordering: a closed key is dropped, so the keys after it decide ----

    [Fact]
    public async Task order_by_a_static_member_named_like_a_document_member()
    {
        await using var session = _store.LightweightSession();

        var names = await Query(session).OrderBy(x => ClosedValueStatics.Number).ThenBy(x => x.Name)
            .Select(x => x.Name).ToListAsync(Token);

        names.ShouldBe(Targets.OrderBy(x => ClosedValueStatics.Number).ThenBy(x => x.Name).Select(x => x.Name));
    }

    [Fact]
    public async Task order_by_a_captured_local_named_like_a_document_member()
    {
        await using var session = _store.LightweightSession();
        var number = 0;

        var names = await Query(session).OrderByDescending(x => number).ThenBy(x => x.Name)
            .Select(x => x.Name).ToListAsync(Token);

        names.ShouldBe(Targets.OrderByDescending(x => number).ThenBy(x => x.Name).Select(x => x.Name));
    }

    [Fact]
    public async Task order_by_a_literal_constant()
    {
        await using var session = _store.LightweightSession();

        var names = await Query(session).OrderBy(x => 1).ThenBy(x => x.Name).Select(x => x.Name)
            .ToListAsync(Token);

        names.ShouldBe(Targets.OrderBy(x => 1).ThenBy(x => x.Name).Select(x => x.Name));
    }

    [Fact]
    public async Task a_closed_key_in_the_middle_of_the_ordering()
    {
        await using var session = _store.LightweightSession();

        var names = await Query(session).OrderBy(x => x.Category).ThenBy(x => ClosedValueStatics.Number)
            .ThenByDescending(x => x.Name).Select(x => x.Name).ToListAsync(Token);

        names.ShouldBe(Targets.OrderBy(x => x.Category).ThenBy(x => ClosedValueStatics.Number)
            .ThenByDescending(x => x.Name).Select(x => x.Name));
    }

    [Fact]
    public async Task last_after_a_closed_key_reverses_the_keys_that_remain()
    {
        await using var session = _store.LightweightSession();

        var last = await Query(session).OrderBy(x => ClosedValueStatics.Number).ThenBy(x => x.Name)
            .LastAsync(Token);

        last.Name.ShouldBe(Targets.OrderBy(x => ClosedValueStatics.Number).ThenBy(x => x.Name).Last().Name);
    }

    [Fact]
    public async Task last_over_only_a_closed_key_is_refused_by_name()
    {
        await using var session = _store.LightweightSession();

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            Query(session).OrderBy(x => ClosedValueStatics.Number).LastAsync(Token));

        ex.Message.ShouldContain("LastAsync requires an OrderBy");
    }

    [Fact]
    public async Task a_cursor_walk_with_a_closed_key_pages_by_the_keys_that_remain()
    {
        await using var session = _store.LightweightSession();

        var seen = new List<string>();
        string? cursor = null;

        do
        {
            var page = await Query(session).OrderBy(x => ClosedValueStatics.Number).ThenBy(x => x.Name)
                .ThenBy(x => x.Id).ToCursorPageAsync(2, cursor, Token);

            seen.AddRange(page.Items.Select(x => x.Name));
            cursor = page.NextCursor;
        } while (cursor is not null);

        seen.ShouldBe(Targets.OrderBy(x => x.Name).Select(x => x.Name));
    }

    // ---- GroupBy, DistinctBy and aggregates over a closed value are refused by name ----

    [Fact]
    public async Task distinct_by_a_static_member_is_refused()
    {
        await using var session = _store.LightweightSession();

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            Query(session).DistinctBy(x => ClosedValueStatics.Category).ToListAsync(Token));

        ex.Message.ShouldContain("reads nothing from the document");
    }

    [Fact]
    public async Task group_by_a_static_member_is_refused()
    {
        await using var session = _store.LightweightSession();

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            Query(session).GroupBy(x => ClosedValueStatics.Category).Select(g => g.Count()).ToListAsync(Token));

        ex.Message.ShouldContain("reads nothing from the document");
    }

    [Fact]
    public async Task group_by_a_captured_local_is_refused()
    {
        await using var session = _store.LightweightSession();
        var category = "same";

        await Should.ThrowAsync<BadLinqExpressionException>(() =>
            Query(session).GroupBy(x => category).Select(g => g.Count()).ToListAsync(Token));
    }

    [Fact]
    public async Task a_group_aggregate_over_a_static_member_is_refused()
    {
        await using var session = _store.LightweightSession();

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            Query(session).GroupBy(x => x.Category)
                .Select(g => new { g.Key, Total = g.Sum(y => ClosedValueStatics.Number) })
                .ToListAsync(Token));

        ex.Message.ShouldContain("not reached from the lambda's parameter");
    }

    [Fact]
    public async Task sum_over_a_static_member_is_refused()
    {
        await using var session = _store.LightweightSession();

        var ex = await Should.ThrowAsync<BadLinqExpressionException>(() =>
            Query(session).SumAsync(x => ClosedValueStatics.Number, Token));

        ex.Message.ShouldContain("not reached from the lambda's parameter");
    }

    [Fact]
    public async Task max_over_a_captured_local_is_refused()
    {
        await using var session = _store.LightweightSession();
        var number = 0;

        await Should.ThrowAsync<BadLinqExpressionException>(() => Query(session).MaxAsync(x => number, Token));
    }

    // ---- arithmetic against a closed value in a Where ----

    [Fact]
    public async Task arithmetic_over_a_static_member_is_a_constant()
    {
        await using var session = _store.LightweightSession();

        var names = await Query(session).Where(x => x.Number < ClosedValueStatics.Value - 4)
            .Select(x => x.Name).ToListAsync(Token);

        names.ShouldBe(Targets.Where(x => x.Number < ClosedValueStatics.Value - 4).Select(x => x.Name),
            ignoreOrder: true);
    }

    [Fact]
    public async Task arithmetic_over_a_captured_local_named_like_a_document_member()
    {
        await using var session = _store.LightweightSession();
        var number = 3;

        var names = await Query(session).Where(x => number + 1 > x.Number).Select(x => x.Name)
            .ToListAsync(Token);

        names.ShouldBe(Targets.Where(x => number + 1 > x.Number).Select(x => x.Name), ignoreOrder: true);
    }

    [Fact]
    public async Task arithmetic_mixing_a_static_member_and_a_document_member()
    {
        await using var session = _store.LightweightSession();

        var names = await Query(session).Where(x => ClosedValueStatics.Value - x.Number > 3)
            .Select(x => x.Name).ToListAsync(Token);

        names.ShouldBe(Targets.Where(x => ClosedValueStatics.Value - x.Number > 3).Select(x => x.Name),
            ignoreOrder: true);
    }
}
