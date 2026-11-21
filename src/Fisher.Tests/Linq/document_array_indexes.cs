using System.Linq.Expressions;
using Fisher.Linq;
using JasperFx;

namespace Fisher.Tests.Linq;

public enum IndexedShade
{
    Pale,
    Mid,
    Deep
}

public class IndexedArrayDoc
{
    public Guid Id { get; set; }
    public int Number { get; set; }
    public string Category { get; set; } = "";
    public int[] Values { get; set; } = [];
    public List<int> List { get; set; } = [];
    public string[] Names { get; set; } = [];
    public List<string> NameList { get; set; } = [];
    public bool[] Flags { get; set; } = [];
    public double[] Ratios { get; set; } = [];
    public decimal[] Prices { get; set; } = [];
    public IndexedShade[] Shades { get; set; } = [];
    public DateTimeOffset[] Stamps { get; set; } = [];
    public int[][] Grid { get; set; } = [];
    public List<List<int>> Nested { get; set; } = [];
    public string[][] Words { get; set; } = [];
    public int[][][] Cube { get; set; } = [];
    public IndexedChild[] Children { get; set; } = [];
    public List<IndexedChild> ChildList { get; set; } = [];
    public IndexedChild[][] ChildGrid { get; set; } = [];
    public int?[] Maybes { get; set; } = [];
    public IndexedChild?[] Sparse { get; set; } = [];
    public byte[] Blob { get; set; } = [1, 2];
}

public record IndexedChild
{
    public string Name { get; set; } = "";
    public int Score { get; set; }
    public int[] Marks { get; set; } = [];
}

/// <summary>
///     Indexing into an array or a list on the document in <c>Where</c> and <c>OrderBy</c> — fisher#666, the
///     port of marten#5949. Every case is checked against LINQ to Objects over the same documents, with the
///     index written as a literal and as a captured local.
/// </summary>
/// <remarks>
///     <para>
///         Before this <c>x.Values[1] &gt; 3</c> was refused as a comparison Fisher could not translate, and
///         <c>OrderBy(x =&gt; x.Values[0])</c> as an ordering that was not over a member. An index is now one more
///         step of the JSON path, <c>$.values[1]</c>, typed by the element's CLR type.
///     </para>
///     <para>
///         <b>An index past the end reads as missing</b>, as <c>json_extract</c> reads any path that is not
///         there: it matches no comparison and sorts first, where LINQ to Objects would throw. A negative index
///         is refused, since C# throws on it too.
///     </para>
/// </remarks>
public class document_array_indexes : IAsyncLifetime
{
    private readonly TemporaryDatabase _database = TemporaryDatabase.Create("document-array-indexes");
    private DocumentStore _store = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Noon = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly IndexedArrayDoc[] Docs = Enumerable.Range(1, 6).Select(i => new IndexedArrayDoc
    {
        Id = Guid.NewGuid(),
        Number = i,
        Category = i % 2 == 0 ? "even" : "odd",
        Values = [i, 10 - i, i * 3],
        List = [i * 2, 7 - i, i],
        Names = ["n" + i, "m" + (7 - i)],
        NameList = ["p" + (7 - i), "q" + i],
        Flags = [i % 2 == 0, i > 3],
        Ratios = [i / 4.0, (7 - i) / 2.0],
        Prices = [i * 1.1m, 10m - i * 0.01m],
        Shades = [(IndexedShade)(i % 3), (IndexedShade)((i + 1) % 3)],
        Stamps = [Noon.AddHours(i), Noon.AddHours(-i).ToOffset(TimeSpan.FromHours(i % 3))],
        Grid = [[i, 7 - i], [i * 2, 10 - i]],
        Nested = [[7 - i, i], [i % 3, i]],
        Words = [["a" + i, "b" + (7 - i)], ["c" + i % 2, "d" + i]],
        Cube = [[[i, 1], [2, 7 - i]], [[i * 3, 4], [5, i % 4]]],
        Children =
        [
            new IndexedChild { Name = "c" + (7 - i), Score = i * 2, Marks = [i, 1] },
            new IndexedChild { Name = "x" + i, Score = 10 - i, Marks = [7 - i] }
        ],
        ChildList = [new IndexedChild { Name = "y" + i, Score = 7 - i }, new IndexedChild { Name = "z" + (7 - i), Score = i }],
        ChildGrid = [[new IndexedChild { Name = "g" + i, Score = i }], [new IndexedChild { Name = "h" + (7 - i), Score = 7 - i }]],
        Maybes = [i % 3 == 0 ? null : i, 5 - i],
        Sparse = [i % 2 == 0 ? null : new IndexedChild { Name = "s" + i, Score = i }]
    }).ToArray();

    public async ValueTask InitializeAsync()
    {
        _store = DocumentStore.For(options =>
        {
            options.ConnectionString = _database.ConnectionString;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<IndexedArrayDoc>();
        });

        await _store.ApplyAllConfiguredChangesToDatabaseAsync(Token);

        await using var session = _store.LightweightSession();
        foreach (var doc in Docs) session.Store(doc);

        // No arrays at all: every index reads as missing.
        session.Store(new IndexedArrayDoc { Id = Guid.NewGuid(), Number = 99, Category = "empty" });
        await session.SaveChangesAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        _database.Dispose();
    }

    // ---- Where ----

    public static TheoryData<string> PredicateNames => [.. Predicates().Keys];

    private static Dictionary<string, Expression<Func<IndexedArrayDoc, bool>>> Predicates()
    {
        var one = 1;
        var zero = 0;

        return new()
        {
            ["value_array_literal"] = x => x.Values[1] == 7,
            ["value_array_local"] = x => x.Values[one] > 6,
            ["list_literal"] = x => x.List[1] == 4,
            ["list_local"] = x => x.List[zero] >= 8,
            ["element_at"] = x => x.Values.ElementAt(2) > 9,
            ["string_array"] = x => x.Names[1] == "m4",
            ["string_list"] = x => x.NameList[1] != "q2",
            ["string_method"] = x => x.Names[0].StartsWith("n2"),
            ["string_length"] = x => x.NameList[0].Length == 2 && x.Number < 4,
            ["double_array"] = x => x.Ratios[0] > 1.0,
            ["decimal_array"] = x => x.Prices[1] < 9.97m,
            ["enum_array"] = x => x.Shades[0] == IndexedShade.Mid,
            ["timestamp_array"] = x => x.Stamps[1] < Noon.AddHours(-3),
            ["bool_array"] = x => x.Flags[1],
            ["not_bool_array"] = x => !x.Flags[0],
            ["bool_array_compared"] = x => x.Flags[0] == true && x.Number < 5,
            ["nullable_element"] = x => x.Maybes[0] == null,
            ["nullable_element_value"] = x => x.Maybes[0] > 3,
            ["jagged_literal"] = x => x.Grid[1][0] == 4,
            ["jagged_locals"] = x => x.Grid[one][zero] > 6,
            ["jagged_not_equal"] = x => x.Grid[0][1] != 3,
            ["nested_list"] = x => x.Nested[1][0] == 0,
            ["jagged_string"] = x => x.Words[1][0] == "c1",
            ["three_deep"] = x => x.Cube[1][1][1] == 3,
            ["indexed_child_member"] = x => x.Children[1].Score > 6,
            ["list_indexed_child_member"] = x => x.ChildList[0].Name == "y3",
            ["jagged_indexed_child_member"] = x => x.ChildGrid[1][0].Score < 3,
            ["index_of_an_indexed_child_member"] = x => x.Children[0].Marks[0] >= 4,
            ["null_child_element"] = x => x.Sparse[0] == null,
            ["member_of_a_maybe_null_child"] = x => x.Sparse[0] != null && x.Sparse[0]!.Score > 2,
            ["two_indexes_compared"] = x => x.Grid[0][0] < x.Values[1],
            ["modulo_of_an_index"] = x => x.Values[2] % 2 == 0,
            ["arithmetic_over_an_index"] = x => x.Values[0] + 2 > 5,
            ["jagged_and_member"] = x => x.Grid[1][1] >= 6 && x.Number != 2,
            ["jagged_or"] = x => x.Grid[0][0] == 1 || x.Words[0][1] == "b2",
            ["not_jagged"] = x => !(x.Grid[1][0] == 4),
            ["inside_any"] = x => x.Children.Any(c => c.Marks[0] == 1 || c.Score > 7)
        };
    }

    [Theory]
    [MemberData(nameof(PredicateNames))]
    public async Task where_matches_linq_to_objects(string name)
    {
        var predicate = Predicates()[name];
        var expected = Docs.AsQueryable().Where(predicate).Select(x => x.Number).OrderBy(x => x).ToList();

        // Every predicate selects something and leaves something out.
        expected.ShouldNotBeEmpty(name);
        expected.Count.ShouldBeLessThan(Docs.Length, name);

        await using var session = _store.QuerySession();

        // The document with no arrays is left out of the comparison: LINQ to Objects would throw on it.
        var query = session.Query<IndexedArrayDoc>().Where(x => x.Number != 99).Where(predicate).Select(x => x.Number);

        (await query.ToListAsync(Token)).OrderBy(x => x).ToList().ShouldBe(expected, session.ToSql(query));
    }

    [Fact]
    public async Task indexing_in_where_and_order_by()
    {
        await using var session = _store.QuerySession();

        #region sample_array_index_in_where_and_order_by
        var numbers = await session.Query<IndexedArrayDoc>()
            .Where(x => x.Values[1] > 5 && x.Children[0].Score >= 4)
            .OrderBy(x => x.Grid[1][0])
            .Select(x => x.Number)
            .ToListAsync();
        #endregion

        numbers.ShouldBe(Docs.Where(x => x.Values[1] > 5 && x.Children[0].Score >= 4).OrderBy(x => x.Grid[1][0])
            .Select(x => x.Number).ToList());
    }

    [Fact]
    public async Task an_index_reads_as_a_json_path()
    {
        await using var session = _store.QuerySession();

        session.ToSql(session.Query<IndexedArrayDoc>().Where(x => x.Grid[1][0] == 4 && x.Children[1].Name == "x"))
            .ShouldContain("json_extract(data, '$.grid[1][0]')");
        session.ToSql(session.Query<IndexedArrayDoc>().Where(x => x.Children[1].Name == "x"))
            .ShouldContain("json_extract(data, '$.children[1].name')");
    }

    /// <summary>
    ///     LINQ to Objects throws <see cref="IndexOutOfRangeException" /> here. SQLite reads a missing element as
    ///     NULL, so it matches no comparison — and the document with no arrays at all matches none either.
    /// </summary>
    [Fact]
    public async Task an_index_past_the_end_or_into_a_missing_array_matches_nothing()
    {
        await using var session = _store.QuerySession();

        (await session.Query<IndexedArrayDoc>().Where(x => x.Values[7] == 3).ToListAsync(Token)).ShouldBeEmpty();
        (await session.Query<IndexedArrayDoc>().Where(x => x.Grid[1][7] == 3).ToListAsync(Token)).ShouldBeEmpty();
        (await session.Query<IndexedArrayDoc>().Where(x => x.Grid[7][0] == 3).ToListAsync(Token)).ShouldBeEmpty();
        (await session.Query<IndexedArrayDoc>().Where(x => x.Children[5].Score > 0).ToListAsync(Token)).ShouldBeEmpty();

        (await session.Query<IndexedArrayDoc>().Where(x => x.Values[0] > -1000).Select(x => x.Number).ToListAsync(Token))
            .ShouldNotContain(99);
    }

    [Fact]
    public async Task a_negative_index_is_refused()
    {
        await using var session = _store.QuerySession();
        var index = -1;

        (await Should.ThrowAsync<BadLinqExpressionException>(() =>
                session.Query<IndexedArrayDoc>().Where(x => x.Values[index] == 3).ToListAsync(Token)))
            .Message.ShouldContain("negative");
        await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<IndexedArrayDoc>().Where(x => x.Grid[0][index] == 3).ToListAsync(Token));
        await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<IndexedArrayDoc>().OrderBy(x => x.Values[index]).ToListAsync(Token));
    }

    /// <summary>The index is part of the JSON path, so it cannot be read from the row.</summary>
    [Fact]
    public async Task an_index_computed_from_the_document_is_refused()
    {
        await using var session = _store.QuerySession();

        (await Should.ThrowAsync<BadLinqExpressionException>(() =>
                session.Query<IndexedArrayDoc>().Where(x => x.Values[x.Number] == 3).ToListAsync(Token)))
            .Message.ShouldContain("computed from the document");
    }

    /// <summary>
    ///     A byte[] is stored as a base64 string and a string's ElementAt is a character, so neither has an
    ///     element on a JSON path. Read as a path they would match nothing; refused instead.
    /// </summary>
    [Fact]
    public async Task indexing_a_value_that_is_not_a_json_array_is_refused()
    {
        await using var session = _store.QuerySession();

        (await Should.ThrowAsync<BadLinqExpressionException>(() =>
                session.Query<IndexedArrayDoc>().Where(x => x.Blob[0] == 1).ToListAsync(Token)))
            .Message.ShouldContain("not stored as a JSON array");

        await Should.ThrowAsync<BadLinqExpressionException>(() =>
            session.Query<IndexedArrayDoc>().Where(x => x.Category.ElementAt(0) == 'e').ToListAsync(Token));
    }

    // ---- OrderBy ----

    private async Task OrderingMatches<TKey>(Expression<Func<IndexedArrayDoc, TKey>> key, bool descending)
    {
        var expected = (descending ? Docs.AsQueryable().OrderByDescending(key) : Docs.AsQueryable().OrderBy(key))
            .ThenBy(x => x.Number).Select(x => x.Number).ToList();

        await using var session = _store.QuerySession();

        var query = (descending
                ? session.Query<IndexedArrayDoc>().Where(x => x.Number != 99).OrderByDescending(key)
                : session.Query<IndexedArrayDoc>().Where(x => x.Number != 99).OrderBy(key))
            .ThenBy(x => x.Number).Select(x => x.Number);

        (await query.ToListAsync(Token)).ShouldBe(expected, session.ToSql(query));
    }

    public static TheoryData<bool> Directions => [false, true];

    [Theory]
    [MemberData(nameof(Directions))]
    public Task order_by_value_array_index(bool descending) => OrderingMatches(x => x.Values[1], descending);

    [Theory]
    [MemberData(nameof(Directions))]
    public Task order_by_value_array_index_from_a_local(bool descending)
    {
        var index = 2;
        return OrderingMatches(x => x.Values[index], descending);
    }

    [Theory]
    [MemberData(nameof(Directions))]
    public Task order_by_list_indexer(bool descending) => OrderingMatches(x => x.List[1], descending);

    [Theory]
    [MemberData(nameof(Directions))]
    public Task order_by_element_at(bool descending) => OrderingMatches(x => x.Values.ElementAt(2), descending);

    [Theory]
    [MemberData(nameof(Directions))]
    public Task order_by_double_array_index(bool descending) => OrderingMatches(x => x.Ratios[1], descending);

    [Theory]
    [MemberData(nameof(Directions))]
    public Task order_by_decimal_array_index(bool descending) => OrderingMatches(x => x.Prices[1], descending);

    [Theory]
    [MemberData(nameof(Directions))]
    public Task order_by_timestamp_array_index(bool descending) => OrderingMatches(x => x.Stamps[1], descending);

    [Theory]
    [MemberData(nameof(Directions))]
    public Task order_by_nullable_element(bool descending) => OrderingMatches(x => x.Maybes[0], descending);

    [Theory]
    [MemberData(nameof(Directions))]
    public Task order_by_jagged_array_index(bool descending) => OrderingMatches(x => x.Grid[1][0], descending);

    [Theory]
    [MemberData(nameof(Directions))]
    public Task order_by_nested_list_indexer(bool descending) => OrderingMatches(x => x.Nested[1][0], descending);

    [Theory]
    [MemberData(nameof(Directions))]
    public Task order_by_three_deep_index(bool descending) => OrderingMatches(x => x.Cube[1][1][1], descending);

    [Theory]
    [MemberData(nameof(Directions))]
    public Task order_by_member_of_indexed_child(bool descending) => OrderingMatches(x => x.Children[1].Score, descending);

    [Theory]
    [MemberData(nameof(Directions))]
    public Task order_by_member_of_jagged_indexed_child(bool descending) =>
        OrderingMatches(x => x.ChildGrid[1][0].Score, descending);

    [Theory]
    [MemberData(nameof(Directions))]
    public Task order_by_negated_index(bool descending) => OrderingMatches(x => -x.Values[0], descending);

    [Theory]
    [MemberData(nameof(Directions))]
    public Task order_by_modulo_of_jagged_index(bool descending) => OrderingMatches(x => x.Grid[1][0] % 3, descending);

    /// <summary>Strings are compared ordinally, which is how SQLite compares them.</summary>
    [Fact]
    public async Task order_by_string_indexes()
    {
        await using var session = _store.QuerySession();

        (await session.Query<IndexedArrayDoc>().Where(x => x.Number != 99).OrderBy(x => x.Names[1])
                .Select(x => x.Number).ToListAsync(Token))
            .ShouldBe(Docs.OrderBy(x => x.Names[1], StringComparer.Ordinal).Select(x => x.Number).ToList());

        (await session.Query<IndexedArrayDoc>().Where(x => x.Number != 99).OrderByDescending(x => x.Words[0][1])
                .Select(x => x.Number).ToListAsync(Token))
            .ShouldBe(Docs.OrderByDescending(x => x.Words[0][1], StringComparer.Ordinal).Select(x => x.Number).ToList());

        (await session.Query<IndexedArrayDoc>().Where(x => x.Number != 99).OrderBy(x => x.ChildList[0].Name.ToUpper())
                .Select(x => x.Number).ToListAsync(Token))
            .ShouldBe(Docs.OrderBy(x => x.ChildList[0].Name.ToUpper(), StringComparer.Ordinal).Select(x => x.Number)
                .ToList());
    }

    [Fact]
    public async Task then_by_an_index()
    {
        await using var session = _store.QuerySession();

        var expected = Docs.OrderBy(x => x.Category).ThenByDescending(x => x.Grid[1][1]).Select(x => x.Number).ToList();

        (await session.Query<IndexedArrayDoc>().Where(x => x.Number != 99).OrderBy(x => x.Category)
                .ThenByDescending(x => x.Grid[1][1]).Select(x => x.Number).ToListAsync(Token))
            .ShouldBe(expected);
    }

    /// <summary>
    ///     A missing element sorts as SQLite's NULL, first in ascending order — LINQ to Objects would throw. The
    ///     document with no arrays at all sorts with them.
    /// </summary>
    [Fact]
    public async Task order_by_an_index_past_the_end_sorts_the_missing_values_first()
    {
        await using var session = _store.QuerySession();

        (await session.Query<IndexedArrayDoc>().OrderBy(x => x.Values[7]).ThenBy(x => x.Number)
                .Select(x => x.Number).ToListAsync(Token))
            .ShouldBe(Docs.Select(x => x.Number).Append(99).OrderBy(x => x).ToList());

        (await session.Query<IndexedArrayDoc>().OrderBy(x => x.Values[0]).Select(x => x.Number).FirstAsync(Token))
            .ShouldBe(99);
    }

    /// <summary>An indexed key is a member, so a keyset page can carry it.</summary>
    [Fact]
    public async Task keyset_paging_over_an_index()
    {
        await using var session = _store.QuerySession();

        var page = await session.Query<IndexedArrayDoc>().Where(x => x.Number != 99).OrderBy(x => x.Values[1])
            .ThenBy(x => x.Id).ToCursorPageAsync(4, null, Token);

        page.Items.Select(x => x.Number).ShouldBe(Docs.OrderBy(x => x.Values[1]).Take(4).Select(x => x.Number));
    }

    [Fact]
    public async Task grouping_by_an_index()
    {
        await using var session = _store.QuerySession();

        var groups = await session.Query<IndexedArrayDoc>().Where(x => x.Number != 99).GroupBy(x => x.Flags[0])
            .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(Token);

        groups.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Count}")
            .ShouldBe(Docs.GroupBy(x => x.Flags[0]).OrderBy(x => x.Key).Select(g => $"{g.Key}:{g.Count()}"));
    }

    /// <summary>An index into an array of the child documents a <c>SelectMany</c> flattens.</summary>
    [Fact]
    public async Task indexing_the_elements_of_a_select_many()
    {
        await using var session = _store.QuerySession();

        var expected = Docs.SelectMany(x => x.Children).Where(c => c.Marks[0] > 2).OrderBy(c => -c.Marks[0])
            .ThenBy(c => c.Score).Select(c => c.Name).ToList();

        (await session.Query<IndexedArrayDoc>().SelectMany(x => x.Children).Where(c => c.Marks[0] > 2)
                .OrderBy(c => -c.Marks[0]).ThenBy(c => c.Score).Select(c => c.Name).ToListAsync(Token))
            .ShouldBe(expected);
    }

    // ---- Select ----

    /// <summary>
    ///     A <c>Select</c> reads the array and indexes it in .NET, as it did before fisher#666, so it answers as
    ///     LINQ to Objects does.
    /// </summary>
    [Fact]
    public async Task selecting_an_index_answers_as_linq_to_objects()
    {
        await using var session = _store.QuerySession();

        (await session.Query<IndexedArrayDoc>().Where(x => x.Number != 99).OrderBy(x => x.Number)
                .Select(x => new { A = x.Values[1], B = x.Grid[1][0], C = x.Children[0].Name }).ToListAsync(Token))
            .ShouldBe(Docs.OrderBy(x => x.Number).Select(x => new { A = x.Values[1], B = x.Grid[1][0], C = x.Children[0].Name })
                .ToList());
    }
}
